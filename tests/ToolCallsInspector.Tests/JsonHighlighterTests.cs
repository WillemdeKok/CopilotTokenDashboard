using ToolCallsInspector.Web.Components.Shared;
using ToolCallsInspector.Web.Services;

namespace ToolCallsInspector.Tests;

public class JsonHighlighterTests
{
    [Fact]
    public void Distinguishes_keys_from_string_values()
    {
        var html = JsonHighlighter.ToHtml("{\"city\" : \"Paris\"}");

        Assert.Equal("{<span class=\"j-key\">&quot;city&quot;</span> : <span class=\"j-str\">&quot;Paris&quot;</span>}", html);
    }

    [Fact]
    public void Highlights_numbers_and_literals()
    {
        var html = JsonHighlighter.ToHtml("[-1.5e3, true, false, null]");

        Assert.Contains("<span class=\"j-num\">-1.5e3</span>", html);
        Assert.Contains("<span class=\"j-lit\">true</span>", html);
        Assert.Contains("<span class=\"j-lit\">false</span>", html);
        Assert.Contains("<span class=\"j-lit\">null</span>", html);
    }

    [Fact]
    public void Encodes_html_inside_strings()
    {
        var html = JsonHighlighter.ToHtml("{\"x\":\"<script>alert('a & b')</script>\"}");

        Assert.DoesNotContain("<script>", html);
        Assert.Contains("&lt;script&gt;alert(&#39;a &amp; b&#39;)&lt;/script&gt;", html);
    }

    [Fact]
    public void Keeps_escaped_quotes_inside_a_string()
    {
        var html = JsonHighlighter.ToHtml("{\"a\":\"say \\\"hi\\\"\"}");

        Assert.Contains("<span class=\"j-str\">&quot;say \\&quot;hi\\&quot;&quot;</span>", html);
    }

    [Fact]
    public void Tolerates_truncated_input()
    {
        var html = JsonHighlighter.ToHtml("{\"a\":\"unterminated");

        Assert.EndsWith("<span class=\"j-str\">&quot;unterminated</span>", html);
    }

    [Theory]
    [InlineData("{\"a\":1}", true)]
    [InlineData("  \n[1,2]", true)]
    [InlineData("Hello {world}", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void Detects_json(string? text, bool expected) => Assert.Equal(expected, JsonHighlighter.LooksLikeJson(text));

    [Fact]
    public void Code_block_pretty_prints_json_and_leaves_plain_text_encoded()
    {
        var json = CodeBlock.Render("{\"a\":1}").Value;
        var text = CodeBlock.Render("1 < 2 and true").Value;

        Assert.Contains("\n", json);
        Assert.Contains("<span class=\"j-num\">1</span>", json);
        Assert.Equal("1 &lt; 2 and true", text);
    }

    [Fact]
    public void Code_block_truncates_and_shows_empty_text()
    {
        Assert.Equal("(empty)", CodeBlock.Render(null, empty: "(empty)").Value);
        Assert.Equal("abc\n<span class=\"j-more\">… (3 more chars)</span>", CodeBlock.Render("abcdef", max: 3).Value);
    }
}
