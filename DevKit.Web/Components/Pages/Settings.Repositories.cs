using DevKit.Web.Models;
using DevKit.Web.Services;
using Microsoft.AspNetCore.Components;

namespace DevKit.Web.Components.Pages;

/// <summary>The repository parts of the settings form: default branches for code merging, and the most-used repositories.</summary>
public sealed partial class Settings
{

    private string repoBranchFilter = "";

    /// <summary>
    /// Repositories shown in the branch table: the most-used ones first, in the order they were pinned, then the rest
    /// by project and name. Unfiltered it is every repository across every project, long enough to need the filter.
    /// </summary>
    private List<TfsRepo> BranchRepos => RepoOrdering.Prioritize(
        repos.Where(r => string.IsNullOrWhiteSpace(repoBranchFilter)
                         || r.Name.Contains(repoBranchFilter, StringComparison.OrdinalIgnoreCase)
                         || r.Project.Contains(repoBranchFilter, StringComparison.OrdinalIgnoreCase)),
        mostUsedRepoIds);

    /// <summary>
    /// What an empty picker means: the automatic branch, named once the repository's branches have loaded.
    /// </summary>
    private string AutomaticBranch(TfsRepo repo, Func<IReadOnlyCollection<string>, string?, string> rule, string beforeLoad)
    {
        var branches = BranchesFor(repo.Id);
        return branches.Count == 0 ? $"Automatic ({beforeLoad})" : $"Automatic: {rule(branches, repo.DefaultBranch)}";
    }

    /// <summary>A repository's branches as the form holds them.</summary>
    private RepoBranchDefaults BranchesOf(string repoId) =>
        repoBranches.GetValueOrDefault(repoId) ?? new RepoBranchDefaults();

    private int ConfiguredBranchCount => repos.Count(r => !BranchesOf(r.Id).IsEmpty);

    private void OnRepoBranchFilter(ChangeEventArgs e) => repoBranchFilter = e.Value?.ToString() ?? "";

    private void OnQaBranchChange(string repoId, string branch) =>
        EditBranches(repoId, entry => entry.QaBranch = (branch ?? "").Trim());

    private void OnMergingBranchChange(string repoId, string branch) =>
        EditBranches(repoId, entry => entry.MergingBranch = (branch ?? "").Trim());

    private void EditBranches(string repoId, Action<RepoBranchDefaults> edit)
    {
        if (!repoBranches.TryGetValue(repoId, out var entry))
        {
            entry = repoBranches[repoId] = new RepoBranchDefaults();
        }

        edit(entry);
    }

    // Branch lists are fetched per repository the first time one of its pickers is opened.
    // A collection has many repositories and each listing is several paged requests, so loading
    // them up front would cost far more than anyone configuring one repository needs.
    // BranchCacheService holds the results, shared with every other page in this circuit.
    private string? loadingBranchRepoId;

    private IReadOnlyList<string> BranchesFor(string repoId) =>
        BranchCache.Peek(repoId) ?? Array.Empty<string>();

    private async Task LoadBranchesFor(TfsRepo repo)
    {
        if (BranchCache.Peek(repo.Id) != null || loadingBranchRepoId == repo.Id)
        {
            return;
        }

        loadingBranchRepoId = repo.Id;
        StateHasChanged();
        try
        {
            // Only this page's wait stops when it closes; the shared listing goes on for any other page.
            await BranchCache.GetAsync(repo, page.Token);
        }
        catch (OperationCanceledException ex) when (page.Token.IsCancellationRequested)
        {
            Logger.LogDebug(ex, "The page was left while the branches of {Repo} were loading", repo.Name);
        }
        catch (Exception ex)
        {
            if (!TfsErrors.IsLogged(ex))
            {
                Logger.LogError(ex, "Could not load branches for repo {Repo}", repo.Name);
            }
        }
        finally
        {
            loadingBranchRepoId = null;
            StateHasChanged();
        }
    }


    private static readonly int[] MostUsedCountChoices = { 0, 3, 5, 8, 10 };

    private static List<GlassDropdown.DropdownOption> MostUsedCountOptions() => MostUsedCountChoices
        .Select(n => new GlassDropdown.DropdownOption
        {
            Value = n.ToString(),
            Label = n == 0 ? "Off" : $"Top {n}"
        }).ToList();

    /// <summary>A lower count trims the pinned list, as Settings keeps it, so the two never disagree.</summary>
    private void OnMostUsedCountChange(ChangeEventArgs e)
    {
        mostUsedCount = int.TryParse(e.Value?.ToString(), out var n) ? Math.Clamp(n, 0, SettingsService.MaxMostUsedRepoCount) : 0;
        mostUsedMsg = "";
        SetMostUsed(mostUsedRepoIds.Take(mostUsedCount));
    }

    /// <summary>
    /// The multi-select works with an unordered set, but pin order is the display order. Existing
    /// pins keep their position, newly ticked repositories are appended, and unticked ones drop out.
    /// </summary>
    private void OnMostUsedReposChanged(HashSet<string> selected)
    {
        var ordered = mostUsedRepoIds.Where(selected.Contains).ToList();
        ordered.AddRange(selected.Except(ordered, StringComparer.OrdinalIgnoreCase).ToList());

        mostUsedMsg = ordered.Count > mostUsedCount
            ? $"Only the first {mostUsedCount} were kept. Raise the count above, or unpin one first."
            : "";

        SetMostUsed(ordered.Take(mostUsedCount));
    }

    private void UnpinMostUsedRepo(string repoId)
    {
        mostUsedMsg = "";
        SetMostUsed(mostUsedRepoIds.Where(id => id != repoId));
    }

    private void SetMostUsed(IEnumerable<string> repoIds)
    {
        mostUsedRepoIds = repoIds.ToList();
        mostUsedSelection = new HashSet<string>(mostUsedRepoIds, StringComparer.OrdinalIgnoreCase);
    }

    private string MostUsedRepoLabel(string repoId)
    {
        var repo = repos.FirstOrDefault(r => r.Id == repoId);
        return repo != null ? repo.Name : "(unknown repository)";
    }

    private List<MultiSelectDropdown.Option> AllRepoOptions => RepoSelect.Options(repos, mostUsedRepoIds);
}
