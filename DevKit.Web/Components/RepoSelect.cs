using DevKit.Web.Models;
using DevKit.Web.Services;

namespace DevKit.Web.Components;

/// <summary>
/// Builds the dropdown data for repository pickers. Kept in one place so the "most used first"
/// ordering and the label format stay identical on every page that offers a repository.
/// </summary>
public static class RepoSelect
{
    public const string MostUsedGroupLabel = "⭐ Most used";
    private const string ProjectGroupPrefix = "\U0001F4C1 ";

    /// <summary>"Project — Name". Unchanged from the per-page lists this replaces.</summary>
    public static string Label(TfsRepo repo) => $"{repo.Project} — {repo.Name}";

    /// <summary>
    /// A "most used" group first, then one group per project holding everything else. Pinned
    /// repositories are not repeated in their project group — a value appearing twice would
    /// highlight two rows as selected.
    /// </summary>
    public static List<GlassDropdown.DropdownGroup> Groups(IEnumerable<TfsRepo>? repos, IEnumerable<string>? pinnedIds)
    {
        var ranks = RepoOrdering.BuildRanks(pinnedIds);
        var ordered = RepoOrdering.Prioritize(repos, ranks);
        var groups = new List<GlassDropdown.DropdownGroup>();

        var pinned = ordered.Where(r => RepoOrdering.RankOf(ranks, r.Id) != RepoOrdering.Unpinned).ToList();
        if (pinned.Count > 0)
            groups.Add(new GlassDropdown.DropdownGroup { Label = MostUsedGroupLabel, Options = ToOptions(pinned) });

        foreach (var byProject in ordered
                     .Where(r => RepoOrdering.RankOf(ranks, r.Id) == RepoOrdering.Unpinned)
                     .GroupBy(r => r.Project, StringComparer.OrdinalIgnoreCase))
        {
            groups.Add(new GlassDropdown.DropdownGroup
            {
                Label = ProjectGroupPrefix + byProject.Key,
                Options = ToOptions(byProject)
            });
        }

        return groups;
    }

    /// <summary>Flat, prioritized options for the multi-select picker, which has no groups.</summary>
    public static List<MultiSelectDropdown.Option> Options(
        IEnumerable<TfsRepo>? repos,
        IEnumerable<string>? pinnedIds,
        Func<TfsRepo, string>? hint = null)
    {
        return RepoOrdering.Prioritize(repos, pinnedIds)
            .Select(r => new MultiSelectDropdown.Option
            {
                Value = r.Id,
                Label = r.Name,
                Hint = hint?.Invoke(r) ?? Label(r)
            })
            .ToList();
    }

    private static List<GlassDropdown.DropdownOption> ToOptions(IEnumerable<TfsRepo> repos) =>
        repos.Select(r => new GlassDropdown.DropdownOption { Value = r.Id, Label = Label(r) }).ToList();
}
