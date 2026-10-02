using Microsoft.AspNetCore.Components;
using DevKit.Web.Models;
using DevKit.Web.Services;
using static DevKit.Web.Services.CancellationSources;

namespace DevKit.Web.Components.Pages;

/// <summary>
/// Bulk branch cleanup across repositories: the branches old sprints' work items link, team branches no
/// work item links, and anything else a search finds. What may be deleted is decided by
/// <see cref="BranchCleanupClassifier"/>; this page only lets the user choose among what it allows.
/// </summary>
public partial class BranchDelete : IDisposable
{
    /// <summary>Each repository is a paged listing plus a pull-request read, so a load is bounded.</summary>
    private const int MaxSelectableRepos = 10;
    private const int MaxTeamNameLength = 50;

    private enum LinkFilter { All, Linked, Unattached }

    private List<TfsArea> areas => PageState.TfsData.Areas;
    private List<TfsIteration> iterations => PageState.TfsData.Iterations;
    private List<TfsRepo> repos => PageState.TfsData.Repos;

    private TfsArea? selectedArea;
    private readonly HashSet<string> selectedSprints = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> selectedRepoIds = new(StringComparer.OrdinalIgnoreCase);
    private string teamName = "";

    private BranchCleanupResult result = new();
    private List<BranchCleanupRow> visible = new();
    private readonly HashSet<string> selectedKeys = new(StringComparer.Ordinal);
    private readonly List<BranchDeleteOutcome> deleteLog = new();

    private LinkFilter linkFilter = LinkFilter.All;
    private bool sprintOnly;
    private string creatorFilter = "";
    private string search = "";

    private bool loading, deleting, loaded, loadedWithSprints, stale, showConfirm;
    private BranchCleanupProgress? progress;
    private string? error;
    private CancellationTokenSource? cts;

    // Worked out once per load or delete rather than on every render of a grid that can hold thousands of rows.
    private int SprintCount { get; set; }
    private int LinkedCount { get; set; }
    private int UnattachedCount { get; set; }
    private int ProtectedCount { get; set; }
    private int VisibleDeletable { get; set; }
    private int HiddenSelection { get; set; }
    private List<GlassDropdown.DropdownOption> CreatorOptions { get; set; } = new();
    private List<BranchCleanupRow> SelectedRows { get; set; } = new();

    private bool Busy => loading || deleting;
    private bool CanLoad => selectedRepoIds.Count > 0 && !Busy;
    private bool CanDelete => selectedKeys.Count > 0 && !Busy;
    private int DeletedCount => deleteLog.Count(o => o.Deleted);

    private List<TfsRepo> AvailableRepos => selectedArea == null
        ? repos
        : repos.Where(r => string.Equals(r.Project, selectedArea.Project, StringComparison.OrdinalIgnoreCase)).ToList();

    private List<GlassDropdown.DropdownGroup> AreaGroups => AreaSprintSelect.AreaGroups(areas);
    private List<MultiSelectDropdown.Option> SprintOptions => AreaSprintSelect.SprintOptions(iterations, selectedArea);
    private List<MultiSelectDropdown.Option> RepoOptions => RepoSelect.Options(AvailableRepos, Settings.MostUsedRepoIds);

    protected override async Task OnInitializedAsync()
    {
        teamName = Settings.TeamName;
        if (!Settings.IsConfigured) return;

        await EnsureTfsDataAsync();
        selectedArea = AreaSprintSelect.Restore(Settings.DefaultAreaPath, areas);

        // No sprint is pre-selected: the default sprint is the current one, and cleaning that up is the
        // one choice that should never happen by accident.
        var defaultRepo = Settings.DefaultRepoId;
        if (!string.IsNullOrEmpty(defaultRepo) && AvailableRepos.Any(r => r.Id == defaultRepo))
            selectedRepoIds.Add(defaultRepo);
    }

    private async Task EnsureTfsDataAsync()
    {
        if (PageState.TfsData.IsLoaded) return;
        try
        {
            PageState.TfsData.Projects = await TfsApi.GetProjectsAsync();
            PageState.TfsData.Repos = await TfsApi.GetRepositoriesAsync();
            foreach (var p in PageState.TfsData.Projects)
            {
                PageState.TfsData.Areas.AddRange(await TfsApi.GetAreasAsync(p.Name));
                PageState.TfsData.Iterations.AddRange(await TfsApi.GetIterationsAsync(p.Name));
            }
            PageState.TfsData.IsLoaded = true;
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Branch Delete could not load the TFS projects and repositories");
            error = $"Could not load projects and repositories: {ex.Message}";
        }
    }

    // ═══ SELECTION ═══

    private void OnAreaChange(ChangeEventArgs e)
    {
        var val = e.Value?.ToString();
        selectedArea = string.IsNullOrEmpty(val) ? null : AreaSprintSelect.Parse(val) ?? selectedArea;

        // Sprints and repositories are project-scoped: anything from the previous project would stay
        // selected and silently match nothing.
        if (selectedArea == null) selectedSprints.Clear();
        else
        {
            var sprints = SprintOptions.Select(o => o.Value).ToHashSet(StringComparer.OrdinalIgnoreCase);
            selectedSprints.RemoveWhere(path => !sprints.Contains(path));
        }
        var repoIds = AvailableRepos.Select(r => r.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        selectedRepoIds.RemoveWhere(id => !repoIds.Contains(id));
        MarkStale();
    }

    private void OnSprintsChanged(HashSet<string> _) => MarkStale();

    private void OnReposChanged(HashSet<string> ids)
    {
        if (ids.Count > MaxSelectableRepos)
        {
            foreach (var extra in ids.Skip(MaxSelectableRepos).ToList()) ids.Remove(extra);
            error = $"At most {MaxSelectableRepos} repositories can be loaded at once — the selection was trimmed.";
        }
        MarkStale();
    }

    /// <summary>The same setting Branch Creator names branches with, so both tools agree on the prefix.</summary>
    private void OnTeamNameChange(ChangeEventArgs e)
    {
        var value = (e.Value?.ToString() ?? "").Trim();
        teamName = value.Length > MaxTeamNameLength ? value[..MaxTeamNameLength] : value;
        Settings.TeamName = teamName;
        MarkStale();
    }

    /// <summary>What is on screen reflects the last load, not the pickers, until the user loads again.</summary>
    private void MarkStale() => stale = loaded;

    // ═══ LOAD ═══

    private async Task Load()
    {
        if (!CanLoad) return;
        CancelAndDispose(ref cts);
        cts = new CancellationTokenSource();
        var ct = cts.Token;

        var query = new BranchCleanupQuery
        {
            Repos = AvailableRepos.Where(r => selectedRepoIds.Contains(r.Id)).ToList(),
            Area = selectedArea,
            SprintPaths = selectedArea == null ? Array.Empty<string>() : selectedSprints.ToList(),
            TeamName = teamName,
            ConfiguredBranchesByRepoId = ConfiguredBranches()
        };

        loading = true;
        error = null;
        progress = null;
        showConfirm = false;
        selectedKeys.Clear();
        StateHasChanged();
        try
        {
            result = await Cleanup.LoadAsync(query, new Progress<BranchCleanupProgress>(OnProgress), ct);
            loaded = true;
            stale = false;
            loadedWithSprints = query.SprintPaths.Count > 0;
            sprintOnly = loadedWithSprints;
            RefreshStats();
            Refilter();
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            Logger.LogDebug("Branch Delete load was superseded or the page was left");
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Branch Delete load failed");
            error = $"Load failed: {ex.Message}";
        }
        finally
        {
            if (!ct.IsCancellationRequested)
            {
                loading = false;
                progress = null;
                StateHasChanged();
            }
        }
    }

    /// <summary>The QA and merging branches Settings holds for each selected repository — kept whatever they are called.</summary>
    private Dictionary<string, IReadOnlyList<string>> ConfiguredBranches() =>
        selectedRepoIds.ToDictionary(
            id => id,
            id =>
            {
                var branches = Settings.GetRepoBranches(id);
                return (IReadOnlyList<string>)new[] { branches.QaBranch, branches.MergingBranch }
                    .Where(name => !string.IsNullOrWhiteSpace(name))
                    .ToList();
            },
            StringComparer.OrdinalIgnoreCase);

    private void OnProgress(BranchCleanupProgress update)
    {
        progress = update;
        _ = InvokeAsync(StateHasChanged);
    }

    // ═══ FILTERING ═══

    private void SetLinkFilter(LinkFilter value) { linkFilter = value; Refilter(); }
    private void SetSprintOnly(bool value) { sprintOnly = value && loadedWithSprints; Refilter(); }
    private void OnCreatorChange(ChangeEventArgs e) { creatorFilter = e.Value?.ToString() ?? ""; Refilter(); }
    private void OnSearchInput(ChangeEventArgs e) { search = (e.Value?.ToString() ?? "").Trim(); Refilter(); }

    private void Refilter()
    {
        visible = result.Rows.Where(Matches).ToList();
        VisibleDeletable = visible.Count(r => r.CanDelete);
        HiddenSelection = selectedKeys.Count - visible.Count(r => selectedKeys.Contains(r.Key));
    }

    private bool Matches(BranchCleanupRow row) =>
        MatchesLink(row)
        && (!sprintOnly || row.InSelectedSprint)
        && (creatorFilter.Length == 0 || string.Equals(row.Creator, creatorFilter, StringComparison.OrdinalIgnoreCase))
        && (search.Length == 0 || MatchesSearch(row, search));

    private bool MatchesLink(BranchCleanupRow row) => linkFilter switch
    {
        LinkFilter.Linked => row.LinkState == BranchLinkState.Linked,
        LinkFilter.Unattached => row.LinkState == BranchLinkState.Unattached,
        _ => true
    };

    private static bool MatchesSearch(BranchCleanupRow row, string term) =>
        row.Branch.Contains(term, StringComparison.OrdinalIgnoreCase)
        || row.LinkedBy.Any(w => w.Id.Contains(term, StringComparison.Ordinal)
                                 || w.Title.Contains(term, StringComparison.OrdinalIgnoreCase));

    private void RefreshStats()
    {
        SprintCount = result.Rows.Count(r => r.InSelectedSprint);
        LinkedCount = result.Rows.Count(r => r.LinkState == BranchLinkState.Linked);
        UnattachedCount = result.Rows.Count(r => r.LinkState == BranchLinkState.Unattached);
        ProtectedCount = result.Rows.Count(r => !r.CanDelete);
        CreatorOptions = result.Rows
            .Select(r => r.Creator)
            .Where(c => c.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(c => c, StringComparer.OrdinalIgnoreCase)
            .Select(c => new GlassDropdown.DropdownOption { Value = c, Label = c })
            .ToList();

        if (creatorFilter.Length > 0 && !CreatorOptions.Exists(o => string.Equals(o.Value, creatorFilter, StringComparison.OrdinalIgnoreCase)))
            creatorFilter = "";
    }

    // ═══ SELECTION + DELETE ═══

    private void Toggle(BranchCleanupRow row)
    {
        if (!row.CanDelete || Busy) return;
        if (!selectedKeys.Remove(row.Key)) selectedKeys.Add(row.Key);
    }

    private void SelectAllVisible()
    {
        foreach (var row in visible.Where(r => r.CanDelete)) selectedKeys.Add(row.Key);
    }

    private void ClearSelection()
    {
        selectedKeys.Clear();
        HiddenSelection = 0;
    }

    private void OpenConfirm()
    {
        SelectedRows = result.Rows.Where(r => r.CanDelete && selectedKeys.Contains(r.Key)).ToList();
        showConfirm = SelectedRows.Count > 0;
    }

    private async Task ExecuteDelete()
    {
        showConfirm = false;
        var rows = SelectedRows;
        if (rows.Count == 0 || Busy) return;

        CancelAndDispose(ref cts);
        cts = new CancellationTokenSource();
        var ct = cts.Token;

        deleting = true;
        error = null;
        progress = new BranchCleanupProgress("Deleting branches", 0, rows.Count);
        StateHasChanged();
        try
        {
            var outcomes = await Cleanup.DeleteAsync(rows, new Progress<BranchCleanupProgress>(OnProgress), ct);
            deleteLog.InsertRange(0, outcomes);

            var deleted = outcomes.Where(o => o.Deleted)
                .Select(o => BranchCleanupRow.KeyOf(o.RepoId, o.Branch))
                .ToHashSet(StringComparer.Ordinal);
            result.Rows.RemoveAll(r => deleted.Contains(r.Key));
            selectedKeys.ExceptWith(deleted);
            RefreshStats();
            Refilter();
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            Logger.LogDebug("Branch Delete was interrupted because the page was left");
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Branch Delete failed");
            error = $"Delete failed: {ex.Message}";
        }
        finally
        {
            if (!ct.IsCancellationRequested)
            {
                deleting = false;
                progress = null;
                StateHasChanged();
            }
        }
    }

    // ═══ DISPLAY ═══

    private string RowClass(BranchCleanupRow row) =>
        !row.CanDelete ? "bdel-row-locked" : selectedKeys.Contains(row.Key) ? "selected" : "";

    private static string UnattachedHint(BranchCleanupRow row) => row.Parsed != null
        ? $"Named after work item #{row.Parsed.WorkItemId}, which does not link it (or no longer exists), and no work item in the selected sprints links it either."
        : "Carries the team prefix but no work item id, and no work item in the selected sprints links it.";

    public void Dispose() => CancelAndDispose(ref cts);
}
