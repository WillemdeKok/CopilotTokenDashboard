using ToolCallsInspector.Core.Analysis;

namespace ToolCallsInspector.Web.Services;

/// <summary>
/// Display preferences shared by every page and tab (the inspector is a local, single-user tool), kept until the app restarts.
/// </summary>
public sealed class ViewPreferences
{
    private bool _showTimings;

    /// <summary>Show the Copilot CLI's diagnostic timing spans (<see cref="SpanNode.IsTiming"/>). Hidden by default.</summary>
    public bool ShowTimings
    {
        get => _showTimings;
        set
        {
            if (_showTimings != value)
            {
                _showTimings = value;
                Changed?.Invoke();
            }
        }
    }

    /// <summary>Raised on any thread when a preference changes.</summary>
    public event Action? Changed;

    public IEnumerable<SpanNode> Visible(IEnumerable<SpanNode> nodes) => ShowTimings ? nodes : nodes.Where(n => !n.IsTiming);

    /// <summary>Number of timing spans among <paramref name="nodes"/> and their descendants.</summary>
    public static int CountTimings(IEnumerable<SpanNode> nodes) =>
        nodes.SelectMany(n => n.Descendants().Prepend(n)).Count(n => n.IsTiming);
}
