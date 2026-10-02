using Microsoft.AspNetCore.Components;
using DevKit.Web.Models;
using DevKit.Web.Services;
using static DevKit.Web.Services.CancellationSources;

namespace DevKit.Web.Components.Pages;

/// <summary>
/// State, selections and the work-item grid for the Branch Creator. The branch workflow itself
/// — existing links, validation, creating and linking — lives in BranchCreator.Branches.cs.
/// </summary>
public partial class BranchCreator
{
    // Data (shared)
    private List<TfsArea> areas => PageState.TfsData.Areas;
    private List<TfsIteration> iterations => PageState.TfsData.Iterations;
    private List<TfsRepo> repos => PageState.TfsData.Repos;

    // Page state (preserved across navigation)
    private BranchCreatorState S => PageState.BranchCreator;
    private List<WorkItem> workItems { get => S.WorkItems; set => S.WorkItems = value; }
    private List<WorkItem> filtered { get => S.Filtered; set => S.Filtered = value; }
    private IReadOnlyList<string> branches { get => S.Branches; set => S.Branches = value; }
    private TfsArea? selectedArea { get => S.SelectedArea; set => S.SelectedArea = value; }
    private string? selectedSprint { get => S.SelectedSprint; set => S.SelectedSprint = value; }
    private TfsRepo? selectedRepo { get => S.SelectedRepo; set => S.SelectedRepo = value; }
    private WorkItem? selectedItem { get => S.SelectedItem; set => S.SelectedItem = value; }
    private string branchName { get => S.BranchName; set => S.BranchName = value; }
    private string prName { get => S.PrName; set => S.PrName = value; }
    private string baseBranch { get => S.BaseBranch; set => S.BaseBranch = value; }
    private List<WorkItemBranchLink> existingBranches { get => S.ExistingBranches; set => S.ExistingBranches = value; }
    private string searchQ { get => S.SearchQ; set => S.SearchQ = value; }
    private string typeFilter { get => S.TypeFilter; set => S.TypeFilter = value; }
    private string assigneeFilter { get => S.AssigneeFilter; set => S.AssigneeFilter = value; }
    private string sortCol { get => S.SortCol; set => S.SortCol = value; }
    private bool sortAsc { get => S.SortAsc; set => S.SortAsc = value; }
    private string resultMsg { get => S.ResultMsg; set => S.ResultMsg = value; }
    private bool resultOk { get => S.ResultOk; set => S.ResultOk = value; }

    private bool loadingExisting, loading, creating, checking, loadingBranches, unlinking;

    /// <summary>The stale link whose row is currently expanded into a remove/cancel confirm.</summary>
    private WorkItemBranchLink? unlinkCandidate;

    private string teamName = "";
    private string existingErr = "";
    private string branchLoadErr = "";

    // Superseded work is cancelled rather than left to finish and overwrite newer state: picking
    // a second repository while the first is still listing would otherwise race.
    private CancellationTokenSource? branchLoadCts;
    private CancellationTokenSource? linkLoadCts;

    // Default base branch: "main" everywhere, except the CasepointARA repo which
    // targets the .NET 8 migration line. Both yield to the repository's QA branch from Settings.
    private const string AraRepoName = "CasepointARA";
    private const string AraDefaultBaseBranch = "dev-qa-net8";
    private const string DefaultBaseBranch = "main";
    private static readonly string[] FallbackBaseBranches = { DefaultBaseBranch, "develop", "master" };

    private static readonly char[] InvalidBranchChars = { ' ', '\t', '~', '^', ':', '?', '*', '[', '\\' };

    private static readonly (Func<string, bool> Fails, string Message)[] BranchNameRules =
    {
        (n => n.Length > BranchNaming.MaxLength, $"Must be {BranchNaming.MaxLength} characters or fewer."),
        (n => n.IndexOfAny(InvalidBranchChars) >= 0, @"Cannot contain spaces or any of  ~ ^ : ? * [ \"),
        (n => n.Any(char.IsControl), "Cannot contain control characters."),
        (n => n.StartsWith('/') || n.EndsWith('/') || n.Contains("//", StringComparison.Ordinal),
            @"Cannot start or end with ""/"", or contain ""//""."),
        (n => n.Contains("..", StringComparison.Ordinal) || n.Contains("@{", StringComparison.Ordinal),
            @"Cannot contain "".."" or ""@{""."),
        (n => n.StartsWith('-') || n.EndsWith('.') || n.EndsWith(".lock", StringComparison.OrdinalIgnoreCase),
            @"Cannot start with ""-"" or end with ""."" or "".lock"".")
    };

    protected override async Task OnInitializedAsync()
    {
        teamName = Settings.TeamName;

        if (!Settings.IsConfigured) return;

        if (!PageState.TfsData.IsLoaded) await LoadSharedTfsDataAsync();

        if (S.HasState) return;
        S.HasState = true;

        ApplyAreaAndSprintDefaults();

        if (!string.IsNullOrEmpty(Settings.DefaultRepoId))
        {
            var repo = repos.FirstOrDefault(r => r.Id == Settings.DefaultRepoId);
            if (repo != null) await SelectRepo(repo);
        }
    }

    private async Task LoadSharedTfsDataAsync()
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
            Logger.LogError(ex, "Could not load projects, repositories and classification nodes from TFS");
        }
    }

    private void ApplyAreaAndSprintDefaults()
    {
        selectedArea = AreaSprintSelect.Parse(Settings.DefaultAreaPath) ?? selectedArea;
        if (!string.IsNullOrEmpty(Settings.DefaultSprint))
            selectedSprint = Settings.DefaultSprint;
    }

    public void Dispose()
    {
        CancelAndDispose(ref branchLoadCts);
        CancelAndDispose(ref linkLoadCts);
    }


    // ─── Team Name (persisted to settings) ───
    private void OnTeamNameChange(ChangeEventArgs e)
    {
        teamName = e.Value?.ToString()?.Trim() ?? "";
        Settings.TeamName = teamName;
        if (selectedItem != null) RegenerateBranchName(selectedItem);
    }

    // ─── Selections ───
    private void OnAreaChange(ChangeEventArgs e)
    {
        var val = e.Value?.ToString();
        selectedArea = string.IsNullOrEmpty(val) ? null : AreaSprintSelect.Parse(val) ?? selectedArea;
    }
    private void OnSprintChange(ChangeEventArgs e) => selectedSprint = e.Value?.ToString();

    private Task OnRepoChange(ChangeEventArgs e)
    {
        var id = e.Value?.ToString();
        var repo = repos.FirstOrDefault(r => r.Id == id);
        if (repo == null) return Task.CompletedTask;

        // Clearing the last result belongs to the deliberate change, not to SelectRepo itself,
        // which also runs for the startup default and must not blank a message it did not cause.
        resultMsg = "";
        return SelectRepo(repo);
    }

    private void OnBaseBranchChange(string branch) => baseBranch = branch ?? "";

    /// <summary>
    /// Switches repository, then loads that repository's branches and pre-selects its base branch.
    /// The branch list is always the selected repository's — no other repository's refs appear.
    /// </summary>
    private async Task SelectRepo(TfsRepo repo)
    {
        if (selectedRepo?.Id == repo.Id && branches.Count > 0) return;

        selectedRepo = repo;
        branches = Array.Empty<string>();
        baseBranch = "";
        branchLoadErr = "";

        CancelAndDispose(ref branchLoadCts);
        branchLoadCts = new CancellationTokenSource();
        var ct = branchLoadCts.Token;

        loadingBranches = true;
        StateHasChanged();
        try
        {
            var loaded = await BranchCache.GetAsync(repo, ct);
            if (ct.IsCancellationRequested) return;

            branches = loaded;
            baseBranch = PickDefaultBaseBranch(repo, loaded, Settings.GetRepoBranches(repo.Id).QaBranch);

            // Staleness depends on this list, so the verdicts are refreshed alongside it.
            MarkMissingBranches();
        }
        catch (OperationCanceledException)
        {
            return; // A newer selection owns the form now.
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Could not load branches for repository {Repo}", repo.Name);
            branchLoadErr = ex is TimeoutException ? ex.Message : $"Branch list unavailable: {ex.Message}";
            baseBranch = PickDefaultBaseBranch(repo, Array.Empty<string>(), Settings.GetRepoBranches(repo.Id).QaBranch);
        }
        finally
        {
            if (!ct.IsCancellationRequested)
            {
                loadingBranches = false;
                StateHasChanged();
            }
        }
    }

    private async Task LoadWorkItems()
    {
        if (selectedArea == null) return;
        loading = true; workItems.Clear(); filtered.Clear(); selectedItem = null; resultMsg = "";
        StateHasChanged();
        try
        {
            var areaPath = TfsApiService.ToWiqlPath(selectedArea.Path);
            var iterPath = !string.IsNullOrEmpty(selectedSprint) ? TfsApiService.ToWiqlPath(selectedSprint) : null;
            workItems = await TfsApi.LoadWorkItemsAsync(selectedArea.Project, areaPath, iterPath);
            ApplyFilter();
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Work-item load failed for area {Area}", selectedArea.Path);
            resultMsg = $"<span style='color:var(--red);'>✕ Load failed: {Enc(ex.Message)}</span>";
            resultOk = false;
        }
        loading = false;
    }

    // ─── Filtering ───
    private void OnSearch(ChangeEventArgs e) { searchQ = (e.Value?.ToString() ?? "").ToLowerInvariant(); ApplyFilter(); }
    private void OnTypeFilter(ChangeEventArgs e) { typeFilter = e.Value?.ToString() ?? ""; ApplyFilter(); }
    private void OnAssigneeFilter(ChangeEventArgs e) { assigneeFilter = e.Value?.ToString() ?? ""; ApplyFilter(); }
    private void ApplyFilter()
    {
        filtered = workItems.Where(w =>
            (string.IsNullOrEmpty(searchQ) || w.Id.Contains(searchQ) || w.Title.ToLowerInvariant().Contains(searchQ) || w.Assigned.ToLowerInvariant().Contains(searchQ))
            && (string.IsNullOrEmpty(typeFilter) || w.Type == typeFilter)
            && (string.IsNullOrEmpty(assigneeFilter) || w.Assigned == assigneeFilter)
        ).ToList();
        ApplySort();
    }
    private void Sort(string col) { if (sortCol == col) sortAsc = !sortAsc; else { sortCol = col; sortAsc = true; } ApplySort(); }
    private void ApplySort()
    {
        filtered = sortCol switch
        {
            "id" => sortAsc ? filtered.OrderBy(w => ParseId(w.Id)).ToList() : filtered.OrderByDescending(w => ParseId(w.Id)).ToList(),
            "title" => sortAsc ? filtered.OrderBy(w => w.Title).ToList() : filtered.OrderByDescending(w => w.Title).ToList(),
            "state" => sortAsc ? filtered.OrderBy(w => w.State).ToList() : filtered.OrderByDescending(w => w.State).ToList(),
            "assigned" => sortAsc ? filtered.OrderBy(w => w.Assigned).ToList() : filtered.OrderByDescending(w => w.Assigned).ToList(),
            _ => filtered
        };
    }

    // ─── Dropdown Data ───
    private List<GlassDropdown.DropdownGroup> AreaGroups => AreaSprintSelect.AreaGroups(areas);
    private List<GlassDropdown.DropdownGroup> SprintGroups => AreaSprintSelect.SprintGroups(iterations);
    private List<GlassDropdown.DropdownGroup> RepoGroups => RepoSelect.Groups(repos, Settings.MostUsedRepoIds);
    private List<GlassDropdown.DropdownOption> TypeOptions => workItems.Select(w => w.Type).Distinct().OrderBy(t => t)
        .Select(t => new GlassDropdown.DropdownOption { Value = t, Label = t }).ToList();
    private List<GlassDropdown.DropdownOption> AssigneeOptions => workItems.Select(w => w.Assigned).Where(a => a != "-").Distinct().OrderBy(a => a)
        .Select(a => new GlassDropdown.DropdownOption { Value = a, Label = a }).ToList();

    private string BaseBranchHint
    {
        get
        {
            if (selectedRepo == null) return "pick a repository";
            if (loadingBranches) return "loading…";
            var qa = Settings.GetRepoBranches(selectedRepo.Id).QaBranch;
            return string.IsNullOrWhiteSpace(qa)
                ? $"{branches.Count} branches"
                : $"{branches.Count} branches · QA default {qa}";
        }
    }

    // ─── Helpers ───

    /// <summary>
    /// The repository's QA branch from Settings wins, then the repo-specific default, then common
    /// names — so a missing preferred branch never leaves the picker empty. Candidates are probed
    /// against a case-insensitive index because a repository can hold tens of thousands of
    /// branches and scanning the list once per candidate would walk it several times over.
    /// </summary>
    private static string PickDefaultBaseBranch(TfsRepo repo, IReadOnlyList<string> branches, string? preferred)
    {
        var candidates = BaseBranchCandidates(repo, preferred).ToList();
        if (branches.Count == 0) return candidates[0];

        var index = new Dictionary<string, string>(branches.Count, StringComparer.OrdinalIgnoreCase);
        foreach (var b in branches) index.TryAdd(b, b);

        foreach (var candidate in candidates)
            if (index.TryGetValue(candidate, out var actual)) return actual;

        return branches[0];
    }

    private static IEnumerable<string> BaseBranchCandidates(TfsRepo repo, string? preferred)
    {
        if (!string.IsNullOrWhiteSpace(preferred)) yield return preferred.Trim();
        if (string.Equals(repo.Name, AraRepoName, StringComparison.OrdinalIgnoreCase)) yield return AraDefaultBaseBranch;
        foreach (var fallback in FallbackBaseBranches) yield return fallback;
    }

    private static int ParseId(string id) => int.TryParse(id, out var n) ? n : 0;

    private static string Enc(string s) => System.Net.WebUtility.HtmlEncode(s);
}
