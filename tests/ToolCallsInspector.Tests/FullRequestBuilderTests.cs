using System.Text.Json.Nodes;
using ToolCallsInspector.Core.Storage;
using ToolCallsInspector.Web.Services;

namespace ToolCallsInspector.Tests;

public sealed class FullRequestBuilderTests
{
    private static readonly QuarterCharCounter Counter = new();

    [Fact]
    public void Combines_system_tools_and_messages_into_one_request()
    {
        var span = new SpanRecord { Id = "s", TraceId = "t", SpanId = "s", ClientId = "copilot-cli", Name = "chat", RequestModel = "claude-opus-5.5", MaxOutputTokens = 32000 };
        var content = new SpanContent
        {
            Id = "s",
            SystemInstructions = "You are a helpful assistant.",
            ToolDefinitions = """[{"type":"function","name":"get_weather"},{"type":"function","name":"get_date"}]""",
            InputMessages = """[{"role":"user","parts":[{"type":"text","content":"hi"}]}]""",
            TranscriptPath = "events.jsonl",
        };

        var request = FullRequestBuilder.Build(span, content, Counter);

        var json = JsonNode.Parse(request.Json)!;
        Assert.Equal("claude-opus-5.5", (string?)json["model"]);
        Assert.Equal(32000, (long?)json["max_tokens"]);
        Assert.Equal("You are a helpful assistant.", (string?)json["system"]);
        Assert.Equal(2, json["tools"]!.AsArray().Count);
        Assert.Equal("hi", (string?)json["messages"]![0]!["parts"]![0]!["content"]);
        Assert.True(request.HasFullHistory);
        Assert.Equal(["system", "tools", "messages"], request.Sections.Select(s => s.Key));
        Assert.Equal(["System prompt", "Tool schemas · 2", "Messages · 1"], request.Sections.Select(s => s.Label));
        Assert.All(request.Sections, s => Assert.True(s.Tokens > 0));
    }

    [Fact]
    public void Keeps_truncated_payloads_as_strings_and_skips_missing_parts()
    {
        var span = new SpanRecord { Id = "s", TraceId = "t", SpanId = "s", ClientId = "vscode", Name = "chat", ResponseModel = "gpt-5" };
        var content = new SpanContent { Id = "s", InputMessages = """[{"role":"user","parts":[{"ty""" };

        var request = FullRequestBuilder.Build(span, content, Counter);

        var json = JsonNode.Parse(request.Json)!;
        Assert.Equal("gpt-5", (string?)json["model"]);
        Assert.Null(json["max_tokens"]);
        Assert.Null(json["system"]);
        Assert.StartsWith("[{\"role\"", (string?)json["messages"]);
        Assert.False(request.HasFullHistory);
        Assert.Equal("messages", request.Sections.Single().Key);
    }

    private sealed class QuarterCharCounter : Core.Analysis.ITokenCounter
    {
        public int Count(string? text) => string.IsNullOrEmpty(text) ? 0 : Math.Max(1, text.Length / 4);
    }
}
