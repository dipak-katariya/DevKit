namespace DevKit.Web.Components;

/// <summary>
/// Formatting the grids share: the work-item state and type pill classes, the pull-request status badge
/// class, and short SHAs. Every grid takes its pill colours from here, so one state or type reads the same
/// on every page.
/// </summary>
public static class WorkItemBadges
{
    private const int ShortShaLength = 8;

    private const string StateActive = "state-active";
    private const string StateResolved = "state-resolved";
    private const string StateClosed = "state-closed";
    private const string StateNew = "state-new";

    private const string TypeBug = "bug";
    private const string TypeChangeRequest = "cr";

    private const string PrCompleted = "pr-completed";
    private const string PrAbandoned = "pr-abandoned";
    private const string PrActive = "pr-active";

    /// <summary>The state pill's class. Matched by keyword, so "In Progress" and "Active" read the same.</summary>
    public static string StateClass(string? state)
    {
        var s = (state ?? "").ToLowerInvariant();
        if (s.Contains("progress") || s.Contains("active"))
        {
            return StateActive;
        }
        if (s.Contains("resolve") || s.Contains("done") || s.Contains("complete"))
        {
            return StateResolved;
        }
        if (s.Contains("closed"))
        {
            return StateClosed;
        }
        return StateNew;
    }

    /// <summary>The type pill's extra class: Bugs and Change Requests get their own colour, anything else none.</summary>
    public static string TypeClass(string? type) => (type ?? "").ToLowerInvariant() switch
    {
        "bug" => TypeBug,
        "change request" => TypeChangeRequest,
        _ => ""
    };

    /// <summary>The pull-request status badge's class. Any status other than completed or abandoned reads as active.</summary>
    public static string PrStatusClass(string? status) => (status ?? "").ToLowerInvariant() switch
    {
        "completed" => PrCompleted,
        "abandoned" => PrAbandoned,
        _ => PrActive
    };

    public static string ShortSha(string? objectId) =>
        string.IsNullOrEmpty(objectId) ? "" : objectId[..Math.Min(ShortShaLength, objectId.Length)];
}
