using DevKit.Web.Models;
using DevKit.Web.Services;

namespace DevKit.Web.Components;

/// <summary>
/// Dropdown data for the area and sprint pickers every TFS tool shows. Kept in one place, like
/// <see cref="RepoSelect"/>, so the value format and the labels are identical on every page.
/// </summary>
public static class AreaSprintSelect
{
    private const string Separator = "|||";
    private const string ProjectGroupPrefix = "\U0001F4C1 ";

    /// <summary>The dropdown value for an area: name, path and project, which together identify it.</summary>
    public static string Serialize(TfsArea area) => $"{area.Name}{Separator}{area.Path}{Separator}{area.Project}";

    /// <summary>The area a dropdown value stands for; null when the value is empty or malformed.</summary>
    public static TfsArea? Parse(string? value)
    {
        var parts = (value ?? "").Split(Separator);
        return parts.Length == 3 ? new TfsArea { Name = parts[0], Path = parts[1], Project = parts[2] } : null;
    }

    /// <summary>
    /// The saved default area, but only while it still exists among the areas that loaded — a renamed or
    /// deleted area would otherwise leave the dropdown showing a raw "name|||path|||project" value.
    /// </summary>
    public static TfsArea? Restore(string? saved, IEnumerable<TfsArea> areas)
    {
        var area = Parse(saved);
        return area != null && areas.Any(a => string.Equals(a.Path, area.Path, StringComparison.OrdinalIgnoreCase)
                                              && string.Equals(a.Project, area.Project, StringComparison.OrdinalIgnoreCase))
            ? area
            : null;
    }

    public static List<GlassDropdown.DropdownGroup> AreaGroups(IEnumerable<TfsArea> areas) => areas
        .GroupBy(a => a.Project)
        .Select(grp => new GlassDropdown.DropdownGroup
        {
            Label = ProjectGroupPrefix + grp.Key,
            Options = grp.Select(a => new GlassDropdown.DropdownOption
            {
                Value = Serialize(a),
                Label = grp.Key + " — " + TfsApiService.AreaDisplayPath(a.Path)
            }).ToList()
        }).ToList();

    public static List<GlassDropdown.DropdownGroup> SprintGroups(IEnumerable<TfsIteration> iterations) => iterations
        .GroupBy(i => i.Project)
        .Select(grp => new GlassDropdown.DropdownGroup
        {
            Label = ProjectGroupPrefix + grp.Key,
            Options = grp.Select(it => new GlassDropdown.DropdownOption { Value = it.Path, Label = grp.Key + " — " + it.Name }).ToList()
        }).ToList();

    /// <summary>
    /// Sprints for a multi-select, scoped to the area's project. Every project reuses the same sprint
    /// names (PI-2026-5_1 exists in three of them), so an unscoped list lets a sprint from another
    /// project be picked, which silently returns nothing.
    /// </summary>
    public static List<MultiSelectDropdown.Option> SprintOptions(IEnumerable<TfsIteration> iterations, TfsArea? area) => iterations
        .Where(it => area == null || string.Equals(it.Project, area.Project, StringComparison.OrdinalIgnoreCase))
        .Select(it => new MultiSelectDropdown.Option { Value = it.Path, Label = it.Name, Hint = $"{it.Project} — {it.Path}" })
        .ToList();
}
