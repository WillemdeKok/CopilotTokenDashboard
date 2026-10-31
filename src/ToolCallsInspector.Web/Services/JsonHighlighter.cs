using System.Text;

namespace ToolCallsInspector.Web.Services;

/// <summary>
/// Turns JSON text into HTML with <c>j-key</c>, <c>j-str</c>, <c>j-num</c>, <c>j-lit</c> spans. Tolerant of truncated
/// or invalid input: anything it does not recognize is emitted as encoded plain text.
/// </summary>
public static class JsonHighlighter
{
    public static bool LooksLikeJson(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return false;
        }

        foreach (var c in text)
        {
            if (!char.IsWhiteSpace(c))
            {
                return c is '{' or '[';
            }
        }

        return false;
    }

    public static string ToHtml(string json)
    {
        var html = new StringBuilder(json.Length * 3 / 2);
        var i = 0;
        while (i < json.Length)
        {
            var c = json[i];
            if (c == '"')
            {
                var end = StringEnd(json, i);
                Span(html, IsKey(json, end) ? "j-key" : "j-str", json, i, end);
                i = end;
            }
            else if (c == '-' || char.IsAsciiDigit(c))
            {
                var end = i + 1;
                while (end < json.Length && (char.IsAsciiDigit(json[end]) || json[end] is '.' or 'e' or 'E' or '+' or '-'))
                {
                    end++;
                }

                Span(html, "j-num", json, i, end);
                i = end;
            }
            else if (Literal(json, i) is { } length)
            {
                Span(html, "j-lit", json, i, i + length);
                i += length;
            }
            else
            {
                Encode(html, c);
                i++;
            }
        }

        return html.ToString();
    }

    /// <summary>Index just past the closing quote, or the end of the text for an unterminated string.</summary>
    private static int StringEnd(string json, int start)
    {
        for (var i = start + 1; i < json.Length; i++)
        {
            if (json[i] == '\\')
            {
                i++;
            }
            else if (json[i] == '"')
            {
                return i + 1;
            }
        }

        return json.Length;
    }

    private static bool IsKey(string json, int afterString)
    {
        for (var i = afterString; i < json.Length; i++)
        {
            if (!char.IsWhiteSpace(json[i]))
            {
                return json[i] == ':';
            }
        }

        return false;
    }

    private static int? Literal(string json, int i)
    {
        foreach (var word in (ReadOnlySpan<string>)["true", "false", "null"])
        {
            if (string.CompareOrdinal(json, i, word, 0, word.Length) == 0
                && (i + word.Length == json.Length || !char.IsAsciiLetterOrDigit(json[i + word.Length])))
            {
                return word.Length;
            }
        }

        return null;
    }

    private static void Span(StringBuilder html, string cssClass, string json, int start, int end)
    {
        html.Append("<span class=\"").Append(cssClass).Append("\">");
        for (var i = start; i < end; i++)
        {
            Encode(html, json[i]);
        }

        html.Append("</span>");
    }

    private static void Encode(StringBuilder html, char c) => _ = c switch
    {
        '<' => html.Append("&lt;"),
        '>' => html.Append("&gt;"),
        '&' => html.Append("&amp;"),
        '"' => html.Append("&quot;"),
        '\'' => html.Append("&#39;"),
        _ => html.Append(c),
    };
}
