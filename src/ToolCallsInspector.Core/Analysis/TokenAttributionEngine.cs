using System.Text.Json;
using ToolCallsInspector.Core.Storage;

namespace ToolCallsInspector.Core.Analysis;

/// <summary>
/// Splits each LLM call's reported input tokens into categories (system prompt, user context,
/// assistant history, tool results per tool, overhead) and estimates how many tokens each tool
/// call introduced into the context.
/// </summary>
/// <remarks>
/// Strategy per chain of LLM calls that share a parent (one agent loop):
/// <list type="number">
/// <item>Content: when <c>gen_ai.input.messages</c> is captured and parseable, tokenize every message and
/// scale the estimate to the reported input tokens (remainder = tool schemas &amp; overhead).</item>
/// <item>Delta: otherwise carry the previous call's composition forward and attribute the growth in reported
/// input tokens to the previous call's output (assistant history) and the tool results produced in between.</item>
/// </list>
/// Tool "introduced" tokens always come from the input growth between calls (exact totals from the provider),
/// distributed over the tools that ran in between, weighted by their result sizes when content is available.
/// </remarks>
public sealed class TokenAttributionEngine(ITokenCounter counter)
{
    private const int PerMessageOverhead = 4;

    public void AttributeChain(IReadOnlyList<SpanNode> llmCalls, IReadOnlyList<SpanNode> tools, IReadOnlyDictionary<string, SpanContent> content)
    {
        var ordered = llmCalls.OrderBy(c => c.Span.StartTime).ToList();
        var toolNamesById = tools
            .Where(t => !string.IsNullOrEmpty(t.Span.ToolCallId) && !string.IsNullOrEmpty(t.Span.ToolName))
            .GroupBy(t => t.Span.ToolCallId!, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First().Span.ToolName!, StringComparer.Ordinal);
        SpanNode? previous = null;

        // Prompt + response of the previous call. Emitters such as the Copilot CLI only capture the messages that
        // are new since the previous call, so the full prompt is reconstructed by appending them to this transcript.
        List<ChatMessage>? transcript = null;

        for (var i = 0; i < ordered.Count; i++)
        {
            var call = ordered[i];
            call.CallIndex = i + 1;
            var reported = (double)(call.Span.InputTokens ?? 0);
            var callContent = content.GetValueOrDefault(call.Span.Id);

            var inputs = ParseUsable(callContent?.InputMessages);
            List<ChatMessage>? prompt = inputs is null
                ? null
                : transcript is not null && IsIncremental(inputs, transcript) ? [.. transcript, .. inputs] : [.. inputs];
            transcript = prompt is null
                ? null
                : [.. prompt, .. GenAiMessageParser.TryParse(callContent?.OutputMessages) ?? []];

            var between = tools
                .Where(t => t.Span.StartTime < call.Span.StartTime && (previous is null || t.Span.StartTime >= previous.Span.StartTime))
                .OrderBy(t => t.Span.StartTime)
                .ToList();

            if (reported <= 0)
            {
                call.Composition = new TokenComposition { Method = AttributionMethod.None };
                continue;
            }

            var toolShares = previous?.Composition is not null && previous.Span.InputTokens > 0
                ? DistributeGrowth(previous, call, between, content)
                : null;

            var composition = prompt is null ? null : EstimateFromMessages(prompt, callContent, toolNamesById);
            if (composition is not null)
            {
                composition.FitTo(reported);
            }
            else if (previous?.Composition is { Method: not AttributionMethod.None } prevComp && toolShares is not null)
            {
                composition = CarryForward(prevComp, previous, call, toolShares);
            }
            else
            {
                composition = EstimateInitial(callContent);
                composition.FitTo(reported);
            }

            call.Composition = composition;
            previous = call;
        }

        foreach (var tool in tools.Where(t => t.IntroducedTokens is null))
        {
            var result = content.GetValueOrDefault(tool.Span.Id)?.ToolResult;
            if (result is not null)
            {
                tool.IntroducedTokens = counter.Count(result) + PerMessageOverhead;
                tool.IntroducedIsEstimate = true;
            }
        }
    }

    /// <summary>Splits the input growth between two calls; returns (assistantShare, per-tool shares, leftover).</summary>
    private GrowthShares DistributeGrowth(SpanNode previous, SpanNode call, List<SpanNode> between, IReadOnlyDictionary<string, SpanContent> content)
    {
        var growth = (double)(call.Span.InputTokens ?? 0) - (previous.Span.InputTokens ?? 0);
        if (growth < 0)
        {
            return new GrowthShares(growth, 0, [], 0);
        }

        var prevVisibleOutput = Math.Max(0, (previous.Span.OutputTokens ?? 0) - (previous.Span.ReasoningTokens ?? 0));
        var assistant = Math.Min(growth, prevVisibleOutput);
        var remaining = growth - assistant;

        var shares = new List<(SpanNode Tool, double Tokens)>();
        if (between.Count > 0 && remaining > 0)
        {
            var weights = between.Select(t =>
            {
                var result = content.GetValueOrDefault(t.Span.Id)?.ToolResult;
                return result is null ? 1d : Math.Max(1, counter.Count(result));
            }).ToList();
            var weightSum = weights.Sum();
            for (var i = 0; i < between.Count; i++)
            {
                var share = remaining * weights[i] / weightSum;
                shares.Add((between[i], share));
                between[i].IntroducedTokens = share;
                between[i].IntroducedIsEstimate = false;
            }

            remaining = 0;
        }

        return new GrowthShares(growth, assistant, shares, remaining);
    }

    private static TokenComposition CarryForward(TokenComposition prevComp, SpanNode previous, SpanNode call, GrowthShares growth)
    {
        var reported = (double)(call.Span.InputTokens ?? 0);
        var comp = prevComp.Clone(AttributionMethod.Delta);

        if (growth.Growth < 0)
        {
            // Context shrank (summarization / compaction): keep proportions, fit to the new total.
            comp.Scale(reported / Math.Max(1, previous.Span.InputTokens ?? 1));
            return comp;
        }

        comp.Add(TokenCategory.AssistantHistory, growth.Assistant);
        foreach (var (tool, tokens) in growth.ToolShares)
        {
            comp.Add(TokenCategory.ToolResults, tokens, tool.Span.ToolName);
        }

        // Growth without tool results: a follow-up/steering user message or injected context.
        comp.Add(TokenCategory.UserContext, growth.Leftover);
        comp.FitTo(reported);
        return comp;
    }

    /// <summary>First call of a chain without parseable messages: split off system prompt and user request.</summary>
    private TokenComposition EstimateInitial(SpanContent? content)
    {
        var comp = new TokenComposition { Method = AttributionMethod.Delta };
        comp.Add(TokenCategory.SystemPrompt, counter.Count(content?.SystemInstructions));
        comp.Add(TokenCategory.UserContext, counter.Count(content?.UserRequest));
        return comp;
    }

    internal TokenComposition? EstimateFromContent(SpanContent? content)
    {
        var messages = ParseUsable(content?.InputMessages);
        return messages is null ? null : EstimateFromMessages(messages, content, null);
    }

    /// <summary>Parses captured input messages; <c>null</c> when absent, truncated or incomplete.</summary>
    private static IReadOnlyList<ChatMessage>? ParseUsable(string? json)
    {
        var messages = GenAiMessageParser.TryParse(json);
        if (messages is null || messages.Count == 0)
        {
            return null;
        }

        // Items the emitter could not map (e.g. Responses API function_call items) arrive without role/parts:
        // the content is incomplete, so fall back to the delta method rather than mis-attributing.
        return messages.Any(m => m.Parts.Count == 0 && m.Role is "unknown" or "") ? null : messages;
    }

    /// <summary>
    /// True when <paramref name="inputs"/> only holds the messages added since the previous call: the prompt does not
    /// start where the conversation started. A compaction summary always starts a fresh transcript.
    /// </summary>
    private static bool IsIncremental(IReadOnlyList<ChatMessage> inputs, List<ChatMessage> transcript) =>
        transcript.Count > 0
        && !SameMessage(inputs[0], transcript[0])
        && !inputs.SelectMany(m => m.Parts).Any(p => p.Kind == MessagePartKind.Other && p.Content.Contains("compaction", StringComparison.OrdinalIgnoreCase));

    private static bool SameMessage(ChatMessage a, ChatMessage b) =>
        string.Equals(a.Role, b.Role, StringComparison.OrdinalIgnoreCase)
        && a.Parts.Count == b.Parts.Count
        && a.Parts.Zip(b.Parts).All(p => p.First.Kind == p.Second.Kind && p.First.Content == p.Second.Content);

    private TokenComposition EstimateFromMessages(IReadOnlyList<ChatMessage> messages, SpanContent? content, IReadOnlyDictionary<string, string>? toolNamesById)
    {
        var comp = new TokenComposition { Method = AttributionMethod.Content };
        var idToName = toolNamesById is null
            ? new Dictionary<string, string>(StringComparer.Ordinal)
            : new Dictionary<string, string>(toolNamesById, StringComparer.Ordinal);
        var pendingNames = new Queue<string>();
        var hasSystem = false;

        foreach (var message in messages)
        {
            var role = message.Role.ToLowerInvariant();
            var roleCategory = role switch
            {
                "system" or "developer" => TokenCategory.SystemPrompt,
                "assistant" or "model" => TokenCategory.AssistantHistory,
                "tool" or "function" => TokenCategory.ToolResults,
                _ => TokenCategory.UserContext,
            };
            hasSystem |= roleCategory == TokenCategory.SystemPrompt;
            var first = true;

            foreach (var part in message.Parts)
            {
                foreach (var block in Expand(part))
                {
                    var tokens = counter.Count(block.Content) + (first ? PerMessageOverhead : 0);
                    first = false;

                    switch (block.Kind)
                    {
                        case MessagePartKind.ToolCall:
                            var name = block.ToolName
                                       ?? (block.ToolCallId is not null && idToName.TryGetValue(block.ToolCallId, out var known) ? known : TokenComposition.UnknownTool);
                            if (block.ToolCallId is not null)
                            {
                                idToName[block.ToolCallId] = name;
                            }

                            pendingNames.Enqueue(name);
                            comp.Add(TokenCategory.AssistantHistory, tokens + counter.Count(name));
                            break;

                        case MessagePartKind.ToolCallResponse:
                            comp.Add(TokenCategory.ToolResults, tokens, ResolveToolName(block, idToName, pendingNames));
                            break;

                        default:
                            if (roleCategory == TokenCategory.ToolResults)
                            {
                                comp.Add(TokenCategory.ToolResults, tokens, ResolveToolName(block, idToName, pendingNames));
                            }
                            else
                            {
                                comp.Add(roleCategory, tokens);
                            }

                            break;
                    }
                }
            }
        }

        if (!hasSystem)
        {
            comp.Add(TokenCategory.SystemPrompt, counter.Count(content?.SystemInstructions));
        }

        return comp;
    }

    private static string ResolveToolName(ChatMessagePart block, Dictionary<string, string> idToName, Queue<string> pending)
    {
        if (block.ToolCallId is not null && idToName.TryGetValue(block.ToolCallId, out var byId))
        {
            return byId;
        }

        if (block.ToolName is not null)
        {
            return block.ToolName;
        }

        return pending.TryDequeue(out var next) ? next : TokenComposition.UnknownTool;
    }

    /// <summary>
    /// Some providers' content arrives as a JSON-stringified block array (Anthropic messages:
    /// text / tool_use / tool_result). Expand those so tool results inside user messages are attributed correctly.
    /// </summary>
    private static IEnumerable<ChatMessagePart> Expand(ChatMessagePart part)
    {
        var text = part.Content;
        if (part.Kind != MessagePartKind.Text || text.Length < 2 || text[0] != '[')
        {
            yield return part;
            yield break;
        }

        List<ChatMessagePart>? blocks = null;
        try
        {
            using var doc = JsonDocument.Parse(text);
            if (doc.RootElement.ValueKind == JsonValueKind.Array && doc.RootElement.EnumerateArray().All(e => e.ValueKind == JsonValueKind.Object && e.TryGetProperty("type", out _)))
            {
                blocks = [];
                foreach (var el in doc.RootElement.EnumerateArray())
                {
                    var type = el.GetProperty("type").GetString();
                    string? S(string name) => el.TryGetProperty(name, out var v) ? (v.ValueKind == JsonValueKind.String ? v.GetString() : v.GetRawText()) : null;
                    blocks.Add(type switch
                    {
                        "text" or "input_text" or "output_text" => new ChatMessagePart(MessagePartKind.Text, S("text") ?? string.Empty),
                        "tool_use" or "function_call" => new ChatMessagePart(MessagePartKind.ToolCall, S("input") ?? S("arguments") ?? string.Empty, S("id") ?? S("call_id"), S("name")),
                        "tool_result" or "function_call_output" => new ChatMessagePart(MessagePartKind.ToolCallResponse, S("content") ?? S("output") ?? string.Empty, S("tool_use_id") ?? S("call_id")),
                        "thinking" or "reasoning" => new ChatMessagePart(MessagePartKind.Reasoning, S("thinking") ?? S("text") ?? string.Empty),
                        _ => new ChatMessagePart(MessagePartKind.Other, el.GetRawText()),
                    });
                }
            }
        }
        catch (JsonException)
        {
            blocks = null;
        }

        if (blocks is null)
        {
            yield return part;
            yield break;
        }

        foreach (var block in blocks)
        {
            yield return block;
        }
    }

    private sealed record GrowthShares(double Growth, double Assistant, List<(SpanNode Tool, double Tokens)> ToolShares, double Leftover);
}
