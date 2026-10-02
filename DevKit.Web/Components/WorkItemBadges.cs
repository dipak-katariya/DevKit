namespace DevKit.Web.Components;

/// <summary>Formatting the git tools' grids share: work-item state and type pill classes, and short SHAs.</summary>
public static class WorkItemBadges
{
    private const int ShortShaLength = 8;

    /// <summary>The state pill's class. Matched by keyword, so "In Progress" and "Active" read the same.</summary>
    public static string StateClass(string? state)
    {
        var s = (state ?? "").ToLowerInvariant();
        if (s.Contains("progress") || s.Contains("active")) return "state-active";
        if (s.Contains("resolve") || s.Contains("done") || s.Contains("complete")) return "state-resolved";
        if (s.Contains("closed")) return "state-closed";
        return "state-new";
    }

    public static string TypeClass(string? type) => (type ?? "").ToLowerInvariant() switch
    {
        "bug" => "bug",
        "change request" => "cr",
        _ => ""
    };

    public static string ShortSha(string? objectId) =>
        string.IsNullOrEmpty(objectId) ? "" : objectId[..Math.Min(ShortShaLength, objectId.Length)];
}
