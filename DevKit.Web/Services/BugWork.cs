using DevKit.Web.Models;

namespace DevKit.Web.Services;

/// <summary>
/// What counts as bug work, in one place for the whole Insight Hub. The Burndown's split of
/// regular vs. bug hours and the capacity tab's bug column both read this, so a task can never
/// be bug time on one tab and planned work on the other.
/// </summary>
public static class BugWork
{
    public const string BugType = "Bug";

    /// <summary>Tag that marks a task as bug or maintenance work regardless of its parent.</summary>
    public const string MaintenanceTag = "Bug/Maintenance";

    public static bool IsBugType(string? workItemType) =>
        string.Equals(workItemType, BugType, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// A Bug itself, anything tagged <see cref="MaintenanceTag"/>, or a task whose parent is a Bug
    /// — which is where "Bug Resolution" tasks live.
    /// </summary>
    public static bool Is(WorkItem item, bool parentIsBug) =>
        IsBugType(item.Type) || item.HasTag(MaintenanceTag) || parentIsBug;
}
