using Microsoft.AspNetCore.Components;
using DevKit.Web.Models;
using DevKit.Web.Services;

namespace DevKit.Web.Components.Pages;

/// <summary>
/// State, the restored defaults and everything that shapes the rows already loaded — filtering,
/// expansion and the deferred commit reads. Choosing and loading a sprint lives in
/// CodeMergingSheet.Loading.cs.
/// </summary>
public partial class CodeMergingSheet
{
    private const int SearchDebounceMs = 220;

    private List<TfsArea> areas => PageState.TfsData.Areas;
    private List<TfsIteration> iterations => PageState.TfsData.Iterations;
    private List<TfsRepo> repos => PageState.TfsData.Repos;

    /// <summary>
    /// Every selected repository costs two pull-request pages plus a branch listing, and a
    /// project here can hold 30+ repositories. The cap keeps a stray "select all" from
    /// turning one click into a hundred requests.
    /// </summary>
    private const int MaxSelectableRepos = 10;

    private TfsArea? selectedArea;
    private readonly HashSet<string> selectedSprints = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> selectedRepoIds = new(StringComparer.OrdinalIgnoreCase);
    private bool pendingBranchLoad;

    private bool deliverableOnly;
    private bool matchByBranchOrTitle = true;

    private bool loading, verifying;

    // ─── Unlinked pull requests ───
    // Its own state and its own fetch, run only when that tab is opened, so the sheet's load
    // never pays for it.
    private const int DefaultUnlinkedDays = 15;

    private bool showUnlinked;
    private bool unlinkedSearching;
    private int unlinkedDays = DefaultUnlinkedDays;
    private UnlinkedPrBundle unlinked = new();

    private CodeMergingBundle bundle = new();
    private readonly MergingSheetFilter filter = new();

    // Recomputed only when the data or a filter changes. The insight strip and the grid all
    // read these lists, so filtering does not re-run several times per render.
    private List<RequirementMergingRow> visibleRows = new();

    /// <summary>The rows before the merge-state filter, which the strip's state chips count.</summary>
    private List<RequirementMergingRow> scopeRows = new();

    // One row per selected repository, holding its branch list and the two chosen branches.
    private List<RepoBranchSetup.RepoBranchRow> branchRows = new();
    private bool branchesLoading;
    private bool branchSetupOpen;
    private string? expandedPrKey;
    private CancellationTokenSource? searchDebounce;

    private bool CanLoad => selectedArea != null && selectedSprints.Count > 0
                            && selectedRepoIds.Count > 0 && !loading;

    private bool CanVerify =>
        bundle.Requirements.Count > 0 && MergingBranchByRepoId.Count > 0 && !verifying && !ComparingCode && !loadingRest;

    private const string VerifyAllTooltip =
        "Quick check: look for every commit — or a copy of it — on each repository's merging branch. " +
        "Reads the commits of rows not loaded yet first.";

    /// <summary>Which stage Verify All is at, since reading a large sprint's commits takes a moment of its own.</summary>
    private string VerifyAllLabel
    {
        get
        {
            if (!verifying)
            {
                return "Verify All";
            }
            return loadingRest ? "Reading commits…" : "Verifying…";
        }
    }

    private string VerifyBlockedReason
    {
        get
        {
            if (bundle.Requirements.Count == 0)
            {
                return "Load the sheet first.";
            }
            return MergingBranchByRepoId.Count == 0
                ? "Set a merging target branch for at least one repository under Branch setup."
                : "Wait for the load or check that is running to finish.";
        }
    }

    private List<TfsRepo> AvailableRepos => selectedArea == null
        ? repos
        : repos.Where(r => string.Equals(r.Project, selectedArea.Project, StringComparison.OrdinalIgnoreCase)).ToList();

    private List<TfsRepo> SelectedRepos =>
        AvailableRepos.Where(r => selectedRepoIds.Contains(r.Id)).ToList();

    /// <summary>Branch each repository's pull requests must target to appear in the sheet.</summary>
    private Dictionary<string, string> QaBranchByRepoId => branchRows
        .Where(r => !string.IsNullOrWhiteSpace(r.QaBranch))
        .ToDictionary(r => r.Repo.Id, r => r.QaBranch, StringComparer.OrdinalIgnoreCase);

    /// <summary>Branch each repository's commits must reach; repositories without one are not verified.</summary>
    private Dictionary<string, string> MergingBranchByRepoId => branchRows
        .Where(r => !string.IsNullOrWhiteSpace(r.MergingBranch))
        .ToDictionary(r => r.Repo.Id, r => r.MergingBranch, StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// How far back the branch scan needs to reach: the earliest selected sprint's start, less
    /// a buffer. Null when no selected sprint is dated, which scans the full window instead.
    /// </summary>
    private DateTime? ScanFrom
    {
        get
        {
            var starts = iterations
                .Where(it => selectedSprints.Contains(it.Path) && it.StartDate.HasValue)
                .Select(it => it.StartDate!.Value)
                .ToList();
            return starts.Count == 0 ? null : MergeVerificationService.LookbackFrom(starts.Min());
        }
    }

    private static string Marked(string value) => string.IsNullOrEmpty(value) ? "" : "cm-col-active";

    protected override async Task OnInitializedAsync()
    {
        if (!Settings.IsConfigured) return;

        if (!PageState.TfsData.IsLoaded)
        {
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
                bundle.ErrorMessage = $"Failed to load TFS metadata: {ex.Message}";
            }
        }

        RestoreDefaults();
    }

    /// <summary>
    /// Applies the defaults from Settings. Each value is checked against the metadata that
    /// actually loaded — a renamed or deleted area would otherwise leave the dropdown showing
    /// a raw "name|||path|||project" string for something that no longer exists.
    /// </summary>
    private void RestoreDefaults()
    {
        selectedArea = AreaSprintSelect.Restore(Settings.DefaultAreaPath, areas);
        RestoreSprints();

        // Folds any older name-keyed / single-branch configuration into the id-keyed map
        // before the rows are built, so an upgrade keeps the branches already configured.
        Settings.MigrateRepoBranches(repos);

        RestoreRepoIds();
        RebuildBranchRows();

        // Branches are fetched after the first paint so the toolbar renders immediately.
        pendingBranchLoad = selectedRepoIds.Count > 0;
    }

    /// <summary>The configured sprint is pre-selected; more can be added from the dropdown.</summary>
    private void RestoreSprints()
    {
        var saved = Settings.DefaultSprint;
        if (string.IsNullOrEmpty(saved)) return;
        if (iterations.Any(i => string.Equals(i.Path, saved, StringComparison.OrdinalIgnoreCase)))
            selectedSprints.Add(saved);
    }

    /// <summary>
    /// Repositories picked in this tool win; otherwise the app-wide Default Repository from
    /// Settings is used, so the sheet opens ready to load on a machine that has never used it.
    /// </summary>
    private void RestoreRepoIds()
    {
        selectedRepoIds.Clear();
        foreach (var id in Settings.CodeMergingRepoIds)
            if (!string.IsNullOrEmpty(id) && AvailableRepos.Any(r => r.Id == id)) selectedRepoIds.Add(id);

        if (selectedRepoIds.Count > 0) return;

        var fallback = Settings.DefaultRepoId;
        if (!string.IsNullOrEmpty(fallback) && AvailableRepos.Any(r => r.Id == fallback))
            selectedRepoIds.Add(fallback);
    }

    /// <summary>
    /// Rebuilds one branch row per selected repository, seeding each from Settings. Rows that
    /// already exist are kept so a branch list that has loaded, or an edit not yet saved, is
    /// not thrown away when the selection changes.
    /// </summary>
    private void RebuildBranchRows()
    {
        var existing = branchRows.ToDictionary(r => r.Repo.Id, StringComparer.OrdinalIgnoreCase);

        branchRows = SelectedRepos.Select(repo =>
        {
            var saved = Settings.GetRepoBranches(repo.Id);
            if (existing.TryGetValue(repo.Id, out var row))
            {
                row.Repo = repo;
                row.SavedQaBranch = saved.QaBranch;
                row.SavedMergingBranch = saved.MergingBranch;
                return row;
            }

            return new RepoBranchSetup.RepoBranchRow
            {
                Repo = repo,
                QaBranch = saved.QaBranch,
                MergingBranch = saved.MergingBranch,
                SavedQaBranch = saved.QaBranch,
                SavedMergingBranch = saved.MergingBranch
            };
        }).ToList();
    }

    /// <summary>
    /// Loads the restored repository's branches after a paint. Deliberately not gated on
    /// firstRender: OnInitializedAsync awaits the TFS metadata, so the component is first
    /// rendered before the defaults exist, and the only renders left afterwards are
    /// firstRender=false ones. The one-shot flag is what keeps this from repeating.
    /// </summary>
    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (!pendingBranchLoad) return;

        pendingBranchLoad = false;
        await LoadBranchesForSelectedRepos();
        StateHasChanged();
    }

    // ═══ FILTERING ═══

    private void Refilter()
    {
        visibleRows = filter.Apply(bundle.Requirements);
        scopeRows = filter.ApplyAllButMergeState(bundle.Requirements);
    }

    /// <summary>Debounced so a long sprint is not re-filtered on every keystroke.</summary>
    private async Task OnSearchInput(ChangeEventArgs e)
    {
        filter.Search = e.Value?.ToString() ?? "";

        searchDebounce?.Cancel();
        searchDebounce?.Dispose();
        searchDebounce = new CancellationTokenSource();
        var token = searchDebounce.Token;
        try
        {
            await Task.Delay(SearchDebounceMs, token);
            Refilter();
            StateHasChanged();
        }
        catch (OperationCanceledException)
        {
            // Superseded by a later keystroke.
        }
    }

    private void OnMemberChange(ChangeEventArgs e) { filter.Member = e.Value?.ToString() ?? ""; Refilter(); }
    private void OnAuthorChange(ChangeEventArgs e) { filter.Author = e.Value?.ToString() ?? ""; Refilter(); }
    private void OnMergedChange(ChangeEventArgs e) => SetMergeState(e.Value?.ToString() ?? "");

    /// <summary>The Merge State dropdown and the strip's chips both set the one filter, so they always agree.</summary>
    private void SetMergeState(string state)
    {
        filter.MergeState = state;
        Refilter();
    }

    private void OnCommitWiseChanged(ChangeEventArgs e)
    {
        filter.CommitWise = e.Value is bool b && b;
        Refilter();
    }

    /// <summary>
    /// Pull requests held back across the whole sheet for targeting something other than their
    /// repository's QA branch. Shown on the toggle so the option is only prominent when there
    /// is actually something behind it.
    /// </summary>
    private int OffQaPrTotal => bundle.Requirements.Sum(r => r.PrsOffQaBranch);

    private void OnShowAllPrsChanged(ChangeEventArgs e)
    {
        filter.ShowAllPrs = e.Value is bool b && b;
        Refilter();
    }

    // ═══ DEFERRED COMMIT LOADING ═══

    private bool loadingRest;

    private int PendingRowCount => bundle.Requirements.Count(r => !r.CommitsLoaded);

    private int LoadedRowCount => bundle.Requirements.Count - PendingRowCount;

    private string PendingTooltip =>
        $"Commits are loaded for the first {LoadedRowCount} work item(s). The other {PendingRowCount} load when " +
        "you open them — or load them all now.";

    /// <summary>
    /// Fills in every deferred row. Bounded concurrency rather than one big WhenAll: the rest
    /// of a large sprint can be hundreds of pull requests, and the throttle is shared with
    /// whatever else the page is doing. The rows are sorted and filtered again once, at the end,
    /// rather than reshuffling under the reader after every chunk.
    /// </summary>
    private async Task LoadRemainingCommits()
    {
        if (loadingRest)
        {
            return;
        }
        loadingRest = true;
        try
        {
            foreach (var chunk in bundle.Requirements.Where(r => !r.CommitsLoaded).Chunk(5))
            {
                await Task.WhenAll(chunk.Select(r => Merging.LoadRowCommitsAsync(r)));
                StateHasChanged();
            }
        }
        finally
        {
            loadingRest = false;
            Refilter();
        }
    }

    // ═══ EXPANSION ═══

    private async Task ToggleRow(RequirementMergingRow row)
    {
        row.IsExpanded = !row.IsExpanded;
        if (!row.IsExpanded)
        {
            expandedPrKey = null;
            return;
        }

        // Rows past the eager batch have no commits yet; read them before verifying, or the
        // check would run against an empty list and report the row as having nothing to merge.
        await LoadRowCommits(row);

        // Opening a row refreshes its merge status, so the sheet stays current without a reload.
        await ReverifyRow(row);
    }

    /// <summary>
    /// Reads one deferred row's commits, with the spinner in its Commits cell meanwhile. Their dates
    /// can move the row in the commit-wise order, so the rows are sorted and filtered again after.
    /// </summary>
    private async Task LoadRowCommits(RequirementMergingRow row)
    {
        if (row.CommitsLoaded)
        {
            return;
        }

        row.IsLoadingCommits = true;
        StateHasChanged();
        try
        {
            await Merging.LoadRowCommitsAsync(row);
        }
        finally
        {
            row.IsLoadingCommits = false;
        }
        Refilter();
    }

    // Only one pull request's commits are on screen at a time — a single nullable key is
    // simpler state than a dictionary of booleans, and keeps the rendered tree small.
    private void TogglePr(string key) => expandedPrKey = expandedPrKey == key ? null : key;

    // ═══ DROPDOWN OPTIONS ═══

    private List<GlassDropdown.DropdownGroup> AreaGroups => AreaSprintSelect.AreaGroups(areas);

    private List<MultiSelectDropdown.Option> SprintOptions => AreaSprintSelect.SprintOptions(iterations, selectedArea);

    private List<GlassDropdown.DropdownOption> MemberOptions => bundle.Requirements
        .Select(r => r.AssignedTo).Where(a => !string.IsNullOrEmpty(a)).Distinct()
        .OrderBy(a => a, StringComparer.OrdinalIgnoreCase)
        .Select(a => new GlassDropdown.DropdownOption { Value = a, Label = a }).ToList();

    private List<GlassDropdown.DropdownOption> AuthorOptions => bundle.AllPullRequests
        .Select(p => p.CreatedBy).Where(a => !string.IsNullOrEmpty(a)).Distinct()
        .OrderBy(a => a, StringComparer.OrdinalIgnoreCase)
        .Select(a => new GlassDropdown.DropdownOption { Value = a, Label = a }).ToList();

    private static readonly List<GlassDropdown.DropdownOption> MergedOptions = new()
    {
        new() { Value = MergingSheetFilter.Merged, Label = "Fully merged" },
        new() { Value = MergingSheetFilter.Partial, Label = "Partially merged" },
        new() { Value = MergingSheetFilter.NotMerged, Label = "Not merged" },
        new() { Value = MergingSheetFilter.RolledBack, Label = "Rolled back" },
        new() { Value = MergingSheetFilter.CodeIssues, Label = "Code differs or not compared" },
        new() { Value = MergingSheetFilter.Unchecked, Label = "Not checked" },
        new() { Value = MergingSheetFilter.NoPrs, Label = "No pull requests" }
    };

    public void Dispose()
    {
        searchDebounce?.Cancel();
        searchDebounce?.Dispose();

        // Leaving the page ends its comparison: nothing would be left to show the result.
        CancellationSources.CancelAndDispose(ref codeRun);
    }
}
