namespace DevKit.Web.Components;

public record ToolEntry(string Name, string Path, string Description, string Icon);

/// <summary>
/// Single source of truth for DevKit's tools, shared by the sidebar nav,
/// the dashboard cards, and the command search.
/// </summary>
public static class ToolCatalog
{
    public static readonly ToolEntry Dashboard =
        new("Dashboard", "/", "Overview of every DevKit tool in one place.", Icons.Dashboard);

    public static readonly ToolEntry Settings =
        new("Settings", "/settings", "Configure the TFS connection, team defaults, and local repo paths.", Icons.Settings);

    /// <summary>
    /// Listed in the order the team works through them, not alphabetically — the sidebar, the
    /// dashboard cards and the command search all read this order from here.
    /// </summary>
    public static readonly IReadOnlyList<ToolEntry> Tools = new[]
    {
        new ToolEntry("Workday Calculator", "/workday", "Track your workday and see live progress toward your hours.", Icons.Clock),
        new ToolEntry("Markdown Viewer", "/markdown", "Live Markdown editor with preview, import, and export.", Icons.FileText),
        new ToolEntry("Branch Creator", "/branch-creator", "Generate standardized branches from work items and link them automatically.", Icons.GitBranch),
        new ToolEntry("TFS Insight Hub", "/insights", "Sprint analytics: capacity planning, size check, burndown, task & bug audits, task creation.", Icons.Insights),
        new ToolEntry("Code Merging Sheet", "/code-merging", "Verify every work item's commits actually reached the release branch.", Icons.Sheet),
        new ToolEntry("Branch Delete", "/branch-delete", "Bulk-clean old sprint and unattached team branches, with protected-branch guards.", Icons.Trash),
        new ToolEntry("Merge Tool", "/merge", "Find commits by work item and cherry-pick them across branches.", Icons.GitMerge),
    };

    /// <summary>Every searchable destination: dashboard, tools, then settings.</summary>
    public static IEnumerable<ToolEntry> All()
    {
        yield return Dashboard;
        foreach (var tool in Tools) yield return tool;
        yield return Settings;
    }
}
