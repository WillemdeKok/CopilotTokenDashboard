using System.IO.Compression;
using System.Text.Json;
using ToolCallsInspector.Core.Storage;

namespace ToolCallsInspector.Core.Analysis;

/// <summary>
/// The computed token attribution of a completed trace, as saved in the database. It keeps historical numbers stable when
/// the local transcripts / request-body logs they were computed from are deleted, and avoids recomputing on startup.
/// </summary>
public sealed class SavedBreakdown
{
    private static readonly JsonSerializerOptions Json = new() { DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull };

    /// <summary>Per span (record id): what the attribution produced.</summary>
    public Dictionary<string, SavedSpan> Spans { get; set; } = new(StringComparer.Ordinal);

    public SavedCarry? Carry { get; set; }

    /// <summary>Not serialized with the rest: the carry's transcript is stored compressed in its own column.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public IReadOnlyList<ChatMessage>? CarryTranscript { get; set; }

    public bool UsedTranscript => Spans.Values.Any(s => s.PromptSource is not null);

    public static SavedBreakdown Capture(TraceAnalysis trace)
    {
        var saved = new SavedBreakdown();
        foreach (var node in trace.Roots.Concat(trace.Roots.SelectMany(r => r.Descendants())).Where(n => !n.IsPlaceholder))
        {
            if (node.Composition is null && node.IntroducedTokens is null && node.CallIndex is null)
            {
                continue;
            }

            saved.Spans[node.Span.Id] = new SavedSpan
            {
                CallIndex = node.CallIndex,
                Composition = node.Composition is null ? null : SavedComposition.From(node.Composition),
                Introduced = node.IntroducedTokens,
                IntroducedIsEstimate = node.IntroducedIsEstimate,
                PromptSource = node.PromptSource,
            };
        }

        if (trace.CarryOut is { } carry)
        {
            saved.Carry = new SavedCarry
            {
                Composition = SavedComposition.From(carry.Composition),
                Input = carry.InputTokens,
                Output = carry.OutputTokens,
                Reasoning = carry.ReasoningTokens,
                StartTicks = carry.Start.UtcTicks,
            };
            saved.CarryTranscript = carry.Transcript;
        }

        return saved;
    }

    /// <summary>Writes the saved figures onto freshly built nodes; returns false when the saved data doesn't fit them.</summary>
    public bool ApplyTo(IEnumerable<SpanNode> nodes)
    {
        var list = nodes.Where(n => !n.IsPlaceholder).ToList();
        if (list.Any(n => n.Category == SpanCategory.LlmCall && !Spans.ContainsKey(n.Span.Id)))
        {
            return false;
        }

        foreach (var node in list)
        {
            if (!Spans.TryGetValue(node.Span.Id, out var span))
            {
                continue;
            }

            node.CallIndex = span.CallIndex;
            node.Composition = span.Composition?.ToComposition();
            node.IntroducedTokens = span.Introduced;
            node.IntroducedIsEstimate = span.IntroducedIsEstimate;
            node.PromptSource = span.PromptSource;
        }

        return true;
    }

    public AttributionCarry? RestoreCarry() => Carry is null
        ? null
        : new AttributionCarry
        {
            Composition = Carry.Composition.ToComposition(),
            InputTokens = Carry.Input,
            OutputTokens = Carry.Output,
            ReasoningTokens = Carry.Reasoning,
            Start = new DateTimeOffset(Carry.StartTicks, TimeSpan.Zero),
            Transcript = CarryTranscript,
        };

    public string Serialize() => JsonSerializer.Serialize(this, Json);

    public static SavedBreakdown? Deserialize(string json, byte[]? carryTranscript)
    {
        try
        {
            var saved = JsonSerializer.Deserialize<SavedBreakdown>(json, Json);
            if (saved is not null && carryTranscript is { Length: > 0 })
            {
                saved.CarryTranscript = DecompressTranscript(carryTranscript);
            }

            return saved;
        }
        catch (Exception ex) when (ex is JsonException or InvalidDataException)
        {
            return null;
        }
    }

    public static byte[]? CompressTranscript(IReadOnlyList<ChatMessage>? transcript)
    {
        if (transcript is null || transcript.Count == 0)
        {
            return null;
        }

        using var buffer = new MemoryStream();
        using (var gzip = new GZipStream(buffer, CompressionLevel.Fastest))
        {
            JsonSerializer.Serialize(gzip, transcript, Json);
        }

        return buffer.ToArray();
    }

    private static List<ChatMessage>? DecompressTranscript(byte[] data)
    {
        using var gzip = new GZipStream(new MemoryStream(data), CompressionMode.Decompress);
        return JsonSerializer.Deserialize<List<ChatMessage>>(gzip, Json);
    }
}

public sealed class SavedSpan
{
    public int? CallIndex { get; set; }
    public SavedComposition? Composition { get; set; }
    public double? Introduced { get; set; }
    public bool IntroducedIsEstimate { get; set; }
    public string? PromptSource { get; set; }
}

public sealed class SavedComposition
{
    public AttributionMethod Method { get; set; }
    public Dictionary<TokenCategory, double> ByCategory { get; set; } = new();
    public Dictionary<string, double> Tools { get; set; } = new(StringComparer.Ordinal);
    public Dictionary<TokenCategory, double> Cached { get; set; } = new();
    public Dictionary<string, double> CachedTools { get; set; } = new(StringComparer.Ordinal);
    public double? TokenizerScale { get; set; }

    public static SavedComposition From(TokenComposition c) => new()
    {
        Method = c.Method,
        ByCategory = new(c.ByCategory),
        Tools = new(c.ToolResultsByTool, StringComparer.Ordinal),
        Cached = new(c.CachedByCategory),
        CachedTools = new(c.CachedToolResultsByTool, StringComparer.Ordinal),
        TokenizerScale = c.TokenizerScale,
    };

    public TokenComposition ToComposition()
    {
        var c = new TokenComposition { Method = Method, TokenizerScale = TokenizerScale };
        foreach (var (k, v) in ByCategory)
        {
            c.ByCategory[k] = v;
        }

        foreach (var (k, v) in Tools)
        {
            c.ToolResultsByTool[k] = v;
        }

        foreach (var (k, v) in Cached)
        {
            c.CachedByCategory[k] = v;
        }

        foreach (var (k, v) in CachedTools)
        {
            c.CachedToolResultsByTool[k] = v;
        }

        return c;
    }
}

public sealed class SavedCarry
{
    public required SavedComposition Composition { get; set; }
    public long Input { get; set; }
    public long Output { get; set; }
    public long Reasoning { get; set; }
    public long StartTicks { get; set; }
}
