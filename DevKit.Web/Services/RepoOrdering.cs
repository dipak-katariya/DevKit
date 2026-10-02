using DevKit.Web.Models;

namespace DevKit.Web.Services;

/// <summary>
/// Puts the repositories a user pinned as "most used" ahead of the rest, in the order they
/// pinned them. One implementation so every repository picker in the app agrees on the order.
/// </summary>
public static class RepoOrdering
{
    /// <summary>Rank of a repository that is not pinned; sorts after every pinned one.</summary>
    public const int Unpinned = int.MaxValue;

    /// <summary>
    /// Position lookup for the pinned ids. A dictionary keeps each sort comparison O(1) instead
    /// of scanning the pinned list per repository, which is what matters once a collection holds
    /// thousands of repositories. Blank and duplicate ids are dropped so a hand-edited settings
    /// file cannot produce two repositories claiming the same rank.
    /// </summary>
    public static Dictionary<string, int> BuildRanks(IEnumerable<string>? pinnedIds)
    {
        var ranks = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        if (pinnedIds is null) return ranks;

        var next = 0;
        foreach (var id in pinnedIds)
        {
            if (string.IsNullOrWhiteSpace(id)) continue;
            if (ranks.TryAdd(id.Trim(), next)) next++;
        }
        return ranks;
    }

    public static int RankOf(IReadOnlyDictionary<string, int> ranks, string? repoId) =>
        !string.IsNullOrEmpty(repoId) && ranks.TryGetValue(repoId, out var rank) ? rank : Unpinned;

    /// <summary>Pinned repositories first in pin order, then everything else by project and name.</summary>
    public static List<TfsRepo> Prioritize(IEnumerable<TfsRepo>? repos, IEnumerable<string>? pinnedIds)
    {
        if (repos is null) return new List<TfsRepo>();

        var ranks = BuildRanks(pinnedIds);
        return Sort(repos, ranks);
    }

    /// <summary>Overload for callers that already built the rank map and reuse it across lists.</summary>
    public static List<TfsRepo> Prioritize(IEnumerable<TfsRepo>? repos, IReadOnlyDictionary<string, int> ranks) =>
        repos is null ? new List<TfsRepo>() : Sort(repos, ranks);

    private static List<TfsRepo> Sort(IEnumerable<TfsRepo> repos, IReadOnlyDictionary<string, int> ranks) =>
        repos
            .Where(r => r is not null)
            .OrderBy(r => RankOf(ranks, r.Id))
            .ThenBy(r => r.Project, StringComparer.OrdinalIgnoreCase)
            .ThenBy(r => r.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
}
