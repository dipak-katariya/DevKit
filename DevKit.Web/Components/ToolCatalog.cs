namespace DevKit.Web.Components;

public record ToolEntry(string Name, string Path, string Description, string Icon);

/// <summary>
/// Single source of truth for DevKit's tools, shared by the sidebar nav,
/// the dashboard cards, and the command search.
/// </summary>
public static class ToolCatalog
{
    public static readonly ToolEntry Dashboard =
        new("Dashboard", "/", "All DevKit tools in one place.", Icons.Dashboard);

    public static readonly ToolEntry Settings =
        new("Settings", "/settings", "TFS connection, default selections and branch defaults.", Icons.Settings);

    /// <summary>
    /// Listed in the order the team works through them, not alphabetically. The sidebar, the dashboard cards and the
    /// command search all read this order from here.
    /// </summary>
    public static readonly IReadOnlyList<ToolEntry> Tools = new[]
    {
        new ToolEntry("Workday Calculator", "/workday", "Track the hours you have worked today.", Icons.Clock),
        new ToolEntry("Markdown Viewer", "/markdown", "Write Markdown with a live preview, and import or export it.", Icons.FileText),
        new ToolEntry("Branch Creator", "/branch-creator", "Create a branch for a work item and link it in TFS.", Icons.GitBranch),
        new ToolEntry("Sprint Insights", "/insights", "Capacity planning, size check, burndown, task and bug audits, and task creation for a sprint.", Icons.Insights),
        new ToolEntry("Code Merging Sheet", "/code-merging", "Check that each work item's changes reached the release branch.", Icons.Sheet),
        new ToolEntry("Branch Cleanup", "/branch-delete", "Delete old sprint branches and team branches no longer linked to work. Protected branches are never deleted.", Icons.Trash),
    };

    /// <summary>The browser tab's title for <paramref name="path"/>: the tool's name, then DevKit.</summary>
    public static string PageTitle(string path)
    {
        var trimmed = "/" + path.Trim('/');
        var tool = All().FirstOrDefault(t => string.Equals(t.Path, trimmed, StringComparison.OrdinalIgnoreCase));
        return tool is null ? "DevKit" : $"{tool.Name} · DevKit";
    }

    /// <summary>Every searchable destination: dashboard, tools, then settings.</summary>
    public static IEnumerable<ToolEntry> All()
    {
        yield return Dashboard;
        foreach (var tool in Tools) yield return tool;
        yield return Settings;
    }
}
