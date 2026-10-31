using ToolCallsInspector.Core.Storage;

namespace ToolCallsInspector.Core.Analysis;

/// <summary>Builds the correlated span tree for one trace and runs token attribution.</summary>
public sealed class TraceAnalyzer(TokenAttributionEngine attribution)
{
    // Spans carry an explicit session key; in-progress turns (root agent span not exported yet) fall back to the conversation id on child spans.
    public static string ResolveSessionKey(string traceId, IEnumerable<SpanRecord> spans)
    {
        var list = spans as IReadOnlyCollection<SpanRecord> ?? spans.ToList();
        return MostCommon(list.Select(s => s.SessionKey)) ?? MostCommon(list.Select(s => s.ConversationId)) ?? traceId;

        static string? MostCommon(IEnumerable<string?> values) =>
            values.Where(v => !string.IsNullOrEmpty(v)).GroupBy(v => v!).OrderByDescending(g => g.Count()).Select(g => g.Key).FirstOrDefault();
    }

    /// <param name="carryIn">The session's main loop at the end of its previous trace; continued by this trace's main loop.</param>
    /// <param name="saved">A breakdown saved for this trace; used instead of recomputing when it covers every LLM call.</param>
    public TraceAnalysis Analyze(string traceId, IReadOnlyList<SpanRecord> spans, IReadOnlyDictionary<string, SpanContent> content, string? sessionKey = null,
        AttributionCarry? carryIn = null, SavedBreakdown? saved = null)
    {
        var nodes = spans
            .DistinctBy(s => s.SpanId)
            .ToDictionary(s => s.SpanId, s => new SpanNode { Span = s.CloneMetadata() });

        var roots = new List<SpanNode>();
        var placeholders = new Dictionary<string, SpanNode>();

        foreach (var node in nodes.Values)
        {
            var parentId = node.Span.ParentSpanId;
            if (parentId is null)
            {
                roots.Add(node);
            }
            else if (nodes.TryGetValue(parentId, out var parent))
            {
                node.Parent = parent;
                parent.Children.Add(node);
            }
            else
            {
                if (!placeholders.TryGetValue(parentId, out var placeholder))
                {
                    placeholder = CreatePlaceholder(node.Span, parentId);
                    placeholders[parentId] = placeholder;
                    roots.Add(placeholder);
                }

                node.Parent = placeholder;
                placeholder.Children.Add(node);
            }
        }

        foreach (var placeholder in placeholders.Values)
        {
            placeholder.Span.StartTime = placeholder.Children.Min(c => c.Span.StartTime);
            placeholder.Span.EndTime = placeholder.Children.Max(c => c.Span.EndTime);
            placeholder.Span.DurationMs = (placeholder.Span.EndTime - placeholder.Span.StartTime).TotalMilliseconds;
            placeholder.Span.Category = placeholder.Children.Any(c => c.Category is SpanCategory.LlmCall or SpanCategory.Tool)
                ? SpanCategory.Agent
                : SpanCategory.Other;
        }

        var allNodes = roots.Concat(roots.SelectMany(r => r.Descendants())).ToList();
        var llmCalls = allNodes.Where(n => n.Category == SpanCategory.LlmCall && !n.IsPlaceholder).OrderBy(n => n.Span.StartTime).ToList();
        var toolCalls = allNodes.Where(n => n.Category == SpanCategory.Tool && !n.IsPlaceholder).OrderBy(n => n.Span.StartTime).ToList();

        var topAgent = allNodes
            .Where(n => n.Category == SpanCategory.Agent && !n.IsPlaceholder)
            .OrderBy(n => Depth(n))
            .ThenBy(n => n.Span.StartTime)
            .FirstOrDefault();

        // The root agent span is only exported when the turn ends; until then, read the prompt from the first LLM request.
        var prompt = GenAiMessageParser.CleanPrompt(topAgent?.Span.UserPrompt)
                     ?? (topAgent is null && placeholders.Count > 0 && llmCalls.Count > 0
                         ? GenAiMessageParser.LastUserPrompt(content.GetValueOrDefault(llmCalls[0].Span.Id)?.InputMessages)
                         : null);
        var utility = UtilityPrompts.Match(prompt);
        var isBackground = utility is not null || (topAgent is null && placeholders.Count == 0);

        var groups = allNodes
            .Where(n => n.Children.Count > 0)
            .Select(n => (Node: n, Chats: n.Children.Where(c => c.Category == SpanCategory.LlmCall).ToList(), Tools: n.Children.Where(c => c.Category == SpanCategory.Tool).ToList()))
            .Where(g => g.Chats.Count > 0)
            .ToList();
        var rootChats = roots.Where(r => r.Category == SpanCategory.LlmCall).ToList();

        // Background requests (title generation etc.) are unrelated to the conversation: they neither continue nor change it.
        var carryOut = carryIn;
        var fromSaved = saved is not null && saved.ApplyTo(allNodes);
        if (fromSaved)
        {
            carryOut = isBackground ? carryIn : saved!.RestoreCarry() ?? carryIn;
        }
        else
        {
            // The main agent loop (not beneath a tool call) continues from the previous trace; sub-agents start fresh.
            var carry = isBackground ? null : carryIn;
            foreach (var group in groups.OrderBy(g => g.Chats.Min(c => c.Span.StartTime)))
            {
                var main = !isBackground && group.Node.Category != SpanCategory.Tool && !group.Node.IsInSubAgent;
                var result = attribution.AttributeChain(group.Chats, group.Tools, content, main ? carry : null);
                if (main)
                {
                    carry = result;
                }
            }

            // Root-level LLM calls (background requests without an agent) form their own chains.
            foreach (var chat in rootChats)
            {
                attribution.AttributeChain([chat], [], content);
            }

            carryOut = isBackground ? carryIn : carry;
        }

        foreach (var group in groups)
        {
            LinkToolsToRequestingCall(group.Chats, group.Tools, content);
        }

        foreach (var tool in toolCalls)
        {
            var subCalls = tool.Descendants().Where(n => n.Category == SpanCategory.LlmCall && !n.IsPlaceholder).ToList();
            if (subCalls.Count > 0)
            {
                tool.SubAgent = new SubAgentUsage(
                    subCalls.Count,
                    subCalls.Sum(c => c.Span.InputTokens ?? 0),
                    subCalls.Sum(c => c.Span.OutputTokens ?? 0),
                    subCalls.Sum(c => c.Span.CacheReadTokens ?? 0));
            }
        }

        var totals = new TokenTotals();
        llmCalls.ForEach(totals.AddLlmCall);
        toolCalls.ForEach(totals.AddToolCall);

        var real = spans.ToList();
        return new TraceAnalysis
        {
            TraceId = traceId,
            SessionKey = sessionKey ?? ResolveSessionKey(traceId, real),
            ClientId = real.GroupBy(s => s.ClientId).OrderByDescending(g => g.Count()).First().Key,
            Roots = roots.OrderBy(r => r.Span.StartTime).ToList(),
            LlmCalls = llmCalls,
            ToolCalls = toolCalls,
            Totals = totals,
            Start = real.Min(s => s.StartTime),
            End = real.Max(s => s.EndTime),
            Prompt = utility is null ? prompt : utility.Subject,
            AgentName = topAgent?.Span.AgentName ?? llmCalls.FirstOrDefault()?.Span.AgentName,
            BackgroundTask = utility?.Label,
            IsBackground = isBackground,
            InProgress = placeholders.Count > 0,
            CarryOut = carryOut,
            FromSavedBreakdown = fromSaved,
        };
    }

    private static void LinkToolsToRequestingCall(List<SpanNode> chats, List<SpanNode> tools, IReadOnlyDictionary<string, SpanContent> content)
    {
        var byToolCallId = new Dictionary<string, SpanNode>(StringComparer.Ordinal);
        foreach (var chat in chats)
        {
            var output = GenAiMessageParser.TryParse(content.GetValueOrDefault(chat.Span.Id)?.OutputMessages);
            if (output is null)
            {
                continue;
            }

            foreach (var part in output.SelectMany(m => m.Parts).Where(p => p.Kind == MessagePartKind.ToolCall && p.ToolCallId is not null))
            {
                byToolCallId.TryAdd(part.ToolCallId!, chat);
            }
        }

        var orderedChats = chats.OrderBy(c => c.Span.StartTime).ToList();
        foreach (var tool in tools.OrderBy(t => t.Span.StartTime))
        {
            var requester = tool.Span.ToolCallId is { Length: > 0 } id && byToolCallId.TryGetValue(id, out var byId)
                ? byId
                : orderedChats.LastOrDefault(c => c.Span.StartTime <= tool.Span.StartTime);

            if (requester is null)
            {
                continue;
            }

            tool.RequestedBy = requester;
            requester.RequestedTools.Add(tool);
        }
    }

    private static SpanNode CreatePlaceholder(SpanRecord child, string parentId) => new()
    {
        IsPlaceholder = true,
        Span = new SpanRecord
        {
            Id = $"{child.TraceId}-{parentId}",
            TraceId = child.TraceId,
            SpanId = parentId,
            ClientId = child.ClientId,
            Name = "Agent (in progress)",
            AgentName = child.AgentName,
            SessionKey = child.SessionKey,
            Category = SpanCategory.Agent,
        },
    };

    private static int Depth(SpanNode node)
    {
        var depth = 0;
        for (var p = node.Parent; p is not null; p = p.Parent)
        {
            depth++;
        }

        return depth;
    }
}
