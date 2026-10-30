using System.Collections.Concurrent;
using Microsoft.ML.Tokenizers;

namespace ToolCallsInspector.Core.Analysis;

/// <summary>
/// Estimates token counts for text content. Reported token counts from the LLM are always preferred;
/// estimates are only used to split a reported total into categories.
/// </summary>
public interface ITokenCounter
{
    int Count(string? text);
}

/// <summary>o200k_base (GPT-4o family) tokenizer – a reasonable proxy for relative sizes across models.</summary>
public sealed class TiktokenTokenCounter : ITokenCounter
{
    private const int CacheLimit = 20_000;
    private readonly Tokenizer _tokenizer = TiktokenTokenizer.CreateForModel("gpt-4o");
    private readonly ConcurrentDictionary<int, (string Text, int Count)> _cache = new();

    public int Count(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return 0;
        }

        if (text.Length < 64)
        {
            return _tokenizer.CountTokens(text);
        }

        var key = string.GetHashCode(text, StringComparison.Ordinal);
        if (_cache.TryGetValue(key, out var cached) && cached.Text == text)
        {
            return cached.Count;
        }

        var count = _tokenizer.CountTokens(text);
        if (_cache.Count > CacheLimit)
        {
            _cache.Clear();
        }

        _cache[key] = (text, count);
        return count;
    }
}
