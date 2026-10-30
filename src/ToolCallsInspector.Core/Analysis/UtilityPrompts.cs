using System.Text.RegularExpressions;

namespace ToolCallsInspector.Core.Analysis;

/// <summary>A housekeeping agent run started by the client itself (e.g. naming a session), not by the user.</summary>
public sealed record UtilityTask(string Label, string? Subject);

/// <summary>
/// Recognises prompts the Copilot clients send on their own behalf. These runs have a real agent span, so without this
/// they would look like user interactions titled with the client's instruction text.
/// </summary>
public static partial class UtilityPrompts
{
    private static readonly (Regex Pattern, string Label)[] Known =
    [
        (SessionNaming(), "Session naming"),
    ];

    public static UtilityTask? Match(string? cleanedPrompt)
    {
        if (string.IsNullOrWhiteSpace(cleanedPrompt))
        {
            return null;
        }

        foreach (var (pattern, label) in Known)
        {
            var match = pattern.Match(cleanedPrompt);
            if (match.Success)
            {
                var subject = match.Groups["subject"].Value.Trim();
                return new UtilityTask(label, subject.Length > 0 ? subject : null);
            }
        }

        return null;
    }

    [GeneratedRegex(@"^\s*Name this session based on the user's (first )?message:?\s*(?<subject>[\s\S]*)$", RegexOptions.IgnoreCase)]
    private static partial Regex SessionNaming();
}
