using Microsoft.AspNetCore.Components;
using DevKit.Web.Models;
using DevKit.Web.Services;

namespace DevKit.Web.Components.Pages;

/// <summary>Cherry-picking a work item's commits into a target branch across local repositories.</summary>
public partial class MergeTool
{
    // Data (shared)
    private List<TfsProject> projects => PageState.TfsData.Projects;
    private List<TfsArea> areas => PageState.TfsData.Areas;
    private List<TfsIteration> iterations => PageState.TfsData.Iterations;
    private List<TfsRepo> repos => PageState.TfsData.Repos;

    // Page state (preserved across navigation)
    private MergeToolState M => PageState.MergeTool;
    private List<WorkItem> workItems { get => M.WorkItems; set => M.WorkItems = value; }
    private List<WorkItem> filtered { get => M.Filtered; set => M.Filtered = value; }
    private TfsArea? selectedArea { get => M.SelectedArea; set => M.SelectedArea = value; }
    private string? selectedSprint { get => M.SelectedSprint; set => M.SelectedSprint = value; }
    private string? selectedItemId { get => M.SelectedItemId; set => M.SelectedItemId = value; }
    private string selectedLocalRepoPath { get => M.SelectedLocalRepoPath; set => M.SelectedLocalRepoPath = value; }
    private string selectedLocalRepoName { get => M.SelectedLocalRepoName; set => M.SelectedLocalRepoName = value; }
    private string globalBaseBranch { get => M.GlobalBaseBranch; set => M.GlobalBaseBranch = value; }
    private List<string> globalBranches { get => M.GlobalBranches; set => M.GlobalBranches = value; }
    private Dictionary<string, RepoPrData> repoPRs { get => M.RepoPRs; set => M.RepoPRs = value; }
    private Dictionary<string, CherryPickHistoryEntry> cherryHistory { get => M.CherryHistory; set => M.CherryHistory = value; }
    private bool prSortDesc { get => M.PrSortDesc; set => M.PrSortDesc = value; }
    private string searchQ { get => M.SearchQ; set => M.SearchQ = value; }
    private string typeFilter { get => M.TypeFilter; set => M.TypeFilter = value; }
    private string assigneeFilter { get => M.AssigneeFilter; set => M.AssigneeFilter = value; }
    private string sortCol { get => M.SortCol; set => M.SortCol = value; }
    private bool sortAsc { get => M.SortAsc; set => M.SortAsc = value; }
    private HashSet<string> collapsedProjects { get => M.CollapsedProjects; set => M.CollapsedProjects = value; }
    private HashSet<string> collapsedPRs { get => M.CollapsedPRs; set => M.CollapsedPRs = value; }

    // Non-preserved (transient)
    private bool loading, loadingPRs, cherryPicking;
    private List<GlassDropdown.DropdownOption> localRepoOptions = new();

    // Modal
    private bool showModal;
    private string modalTitle = "", modalBody = "";
    private string? conflictRepoPath;
    private string? conflictSha;
    private string? conflictRepoId;
    private string? conflictBranch;

    protected override async Task OnInitializedAsync()
    {
        if (!Settings.IsConfigured) return;

        // Load shared TFS data once
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
            catch { }
        }

        // Always rebuild local repo options (lightweight disk scan)
        BuildLocalRepoOptions();

        // If state already exists, just return (preserve it)
        if (M.HasState) return;
        M.HasState = true;

        // Apply defaults from settings
        selectedArea = AreaSprintSelect.Parse(Settings.DefaultAreaPath) ?? selectedArea;
        if (!string.IsNullOrEmpty(Settings.DefaultSprint))
            selectedSprint = Settings.DefaultSprint;
    }

    public void Dispose() { /* State lives in PageStateService */ }

    private List<LocalRepoInfo> scannedLocalRepos = new();

    private void BuildLocalRepoOptions()
    {
        scannedLocalRepos = Settings.ScanLocalRepos(repos.Count > 0 ? repos : null);
        localRepoOptions = scannedLocalRepos.Select(lr =>
            new GlassDropdown.DropdownOption { Value = lr.LocalPath, Label = lr.DisplayLabel }
        ).ToList();
    }

    // ─── Area / Sprint ───
    private void OnAreaChange(ChangeEventArgs e)
    {
        var val = e.Value?.ToString();
        selectedArea = string.IsNullOrEmpty(val) ? null : AreaSprintSelect.Parse(val) ?? selectedArea;
    }
    private void OnSprintChange(ChangeEventArgs e) => selectedSprint = e.Value?.ToString();

    // ─── Local Repo & Branch ───
    private async void OnLocalRepoChange(ChangeEventArgs e)
    {
        selectedLocalRepoPath = e.Value?.ToString() ?? "";
        selectedLocalRepoName = string.IsNullOrEmpty(selectedLocalRepoPath) ? "" : System.IO.Path.GetFileName(selectedLocalRepoPath);
        globalBaseBranch = "";
        globalBranches.Clear();

        if (!string.IsNullOrEmpty(selectedLocalRepoPath))
        {
            // Find the scanned local repo info (has TFS match via git remote URL)
            var localInfo = scannedLocalRepos.FirstOrDefault(r => r.LocalPath == selectedLocalRepoPath);

            if (localInfo?.IsMapped == true)
            {
                // Matched to TFS — load branches through the shared cache, which pages the refs
                // endpoint instead of asking for every ref in one request.
                var tfsRepo = repos.FirstOrDefault(r => r.Id == localInfo.TfsRepoId);
                if (tfsRepo != null)
                {
                    try { globalBranches = (await BranchCache.GetAsync(tfsRepo)).ToList(); }
                    catch (Exception ex) { Logger.LogWarning(ex, "Could not load branches for {Repo}", tfsRepo.Name); }
                }

                globalBaseBranch = globalBranches.Contains("develop") ? "develop" : globalBranches.FirstOrDefault() ?? "";
                Settings.SetBaseBranch(localInfo.TfsRepoId!, globalBaseBranch);
            }
            else
            {
                // No TFS match — read branches from local git
                try
                {
                    var result = await GitCmd.RunGitCommand(selectedLocalRepoPath, "branch -r --format=%(refname:short)");
                    globalBranches = result.Split('\n', StringSplitOptions.RemoveEmptyEntries)
                        .Select(b => b.Trim().Replace("origin/", ""))
                        .Where(b => !string.IsNullOrEmpty(b) && b != "HEAD")
                        .Distinct().OrderBy(b => b).ToList();
                    globalBaseBranch = globalBranches.Contains("develop") ? "develop" : globalBranches.FirstOrDefault() ?? "";
                }
                catch { }
            }
        }
        StateHasChanged();
    }

    private void OnGlobalBranchChange(ChangeEventArgs e)
    {
        globalBaseBranch = e.Value?.ToString() ?? "";
        var localInfo = scannedLocalRepos.FirstOrDefault(r => r.LocalPath == selectedLocalRepoPath);
        if (localInfo?.IsMapped == true)
            Settings.SetBaseBranch(localInfo.TfsRepoId!, globalBaseBranch);
    }

    // ─── Load Work Items ───
    private async Task LoadWorkItems()
    {
        if (selectedArea == null) return;
        loading = true; workItems.Clear(); filtered.Clear(); selectedItemId = null; repoPRs.Clear();
        StateHasChanged();
        try
        {
            var areaPath = TfsApiService.ToWiqlPath(selectedArea.Path);
            var iterPath = !string.IsNullOrEmpty(selectedSprint) ? TfsApiService.ToWiqlPath(selectedSprint) : null;
            workItems = await TfsApi.LoadWorkItemsAsync(selectedArea.Project, areaPath, iterPath);
            ApplyFilter();
        }
        catch { }
        loading = false;
    }

    // ─── Item Selection — single ───
    private async Task SelectItem(string id)
    {
        selectedItemId = selectedItemId == id ? null : id;
        if (!string.IsNullOrEmpty(selectedItemId))
        {
            await LoadPRs();
            return;
        }

        M.PrSearch++;
        loadingPRs = false;
        repoPRs.Clear();
    }

    // ─── PR Sort ───
    private void TogglePrSort() => prSortDesc = !prSortDesc;

    // ─── Load PRs ───
    // Callers await the search, so the panel redraws when it ends: started and forgotten, it left the
    // spinner up until the next click. Each search takes a number, and one a newer search has overtaken
    // stops writing, so picking another work item mid-search never mixes two items' pull requests.
    private async Task LoadPRs()
    {
        if (string.IsNullOrEmpty(selectedItemId)) return;

        var search = ++M.PrSearch;
        var workItemId = selectedItemId;
        loadingPRs = true; repoPRs.Clear(); StateHasChanged();
        foreach (var repo in repos)
        {
            var found = await FindRepoPrs(repo, workItemId);
            if (search != M.PrSearch)
            {
                return;
            }
            if (found != null)
            {
                repoPRs[repo.Id] = found;
            }
        }
        loadingPRs = false;
    }

    private async Task<RepoPrData?> FindRepoPrs(TfsRepo repo, string workItemId)
    {
        var matched = (await TfsApi.GetPullRequestsAsync(repo.Project, repo.Id))
            .Where(pr => MentionsWorkItem(pr, workItemId))
            .ToList();
        if (matched.Count == 0)
        {
            return null;
        }

        foreach (var pr in matched)
        {
            pr.Commits = await TfsApi.GetPrCommitsAsync(repo.Project, repo.Id, pr.PullRequestId);
        }

        // Warms the shared branch cache so picking this repository below is instant. Not awaited: the
        // search does not need the branches, and the cache logs a listing that fails.
        _ = BranchCache.GetAsync(repo);
        return new RepoPrData { Repo = repo, Prs = matched };
    }

    private static bool MentionsWorkItem(TfsPullRequest pr, string workItemId) =>
        $"{pr.Title} {pr.Description} {pr.SourceRefName}".Contains(workItemId, StringComparison.OrdinalIgnoreCase);

    // ─── Collapse ───
    private void ToggleProject(string id) { if (!collapsedProjects.Remove(id)) collapsedProjects.Add(id); }
    private void TogglePR(string key) { if (!collapsedPRs.Remove(key)) collapsedPRs.Add(key); }

    // ─── Cherry-Pick ───
    private async Task DoCherryPick(string sha, string repoId, string branch, string localPath, string msg)
    {
        if (string.IsNullOrEmpty(branch) || string.IsNullOrEmpty(localPath)) return;
        cherryPicking = true; StateHasChanged();

        var result = await GitCmd.CherryPickAsync(localPath, branch, sha);
        var key = $"{sha}-{repoId}";
        cherryHistory[key] = new CherryPickHistoryEntry { Status = result.Status, Branch = branch, Timestamp = DateTime.Now };

        cherryPicking = false;

        switch (result.Status)
        {
            case CherryPickStatus.Done:
                ShowToast($"✅ {sha[..8]} cherry-picked into {branch}", "s");
                break;
            case CherryPickStatus.Exists:
                ShowToast($"ℹ️ {sha[..8]} already exists in {branch}", "w");
                break;
            case CherryPickStatus.Conflict:
                ShowConflictModal(sha, repoId, branch, localPath, msg, result);
                break;
            default:
                ShowToast($"❌ Cherry-pick failed: {result.ErrorOutput}", "e");
                break;
        }
        StateHasChanged();
    }

    private void ShowConflictModal(string sha, string repoId, string branch, string localPath, string msg, CherryPickResult result)
    {
        var shortSha = sha.Length >= 12 ? sha[..12] : sha;
        var encMsg = System.Net.WebUtility.HtmlEncode(msg);
        modalTitle = "⚠ Merge Conflict Detected";
        conflictRepoPath = localPath;
        conflictSha = sha;
        conflictRepoId = repoId;
        conflictBranch = branch;

        var sb = new System.Text.StringBuilder();
        sb.Append("<p style='font-size:.76rem;'><strong>Commit:</strong> <code>").Append(shortSha).Append("</code></p>");
        sb.Append("<p style='font-size:.76rem;'><strong>Message:</strong> ").Append(encMsg).Append("</p>");
        sb.Append("<p style='font-size:.76rem;'><strong>Target:</strong> <code style='color:var(--cyan);'>").Append(branch).Append("</code></p>");
        sb.Append("<p style='font-size:.76rem;'><strong>Repo:</strong> <code>").Append(System.Net.WebUtility.HtmlEncode(localPath)).Append("</code></p>");
        sb.Append("<div style='margin:12px 0;padding:10px 14px;background:rgba(248,113,113,.06);border:1px solid rgba(248,113,113,.12);border-radius:8px;font-size:.74rem;line-height:1.6;'>");
        sb.Append("<strong>Conflicts detected!</strong> The cherry-pick left the working tree in a conflict state.<br/>");
        sb.Append("Click <strong>Open in Visual Studio</strong> to resolve conflicts visually, then commit manually.<br/>");
        sb.Append("After resolving, click <strong>Mark Resolved</strong>.");
        sb.Append("</div>");
        if (!string.IsNullOrEmpty(result.ErrorOutput))
            sb.Append("<pre class='code-block' style='font-size:.64rem;max-height:120px;overflow:auto;'>").Append(System.Net.WebUtility.HtmlEncode(result.ErrorOutput)).Append("</pre>");
        modalBody = sb.ToString();
        showModal = true;
    }

    private void OpenInVisualStudio()
    {
        if (string.IsNullOrEmpty(conflictRepoPath)) return;
        try
        {
            // Find .sln file in repo path, or open the folder
            var slnFiles = Directory.GetFiles(conflictRepoPath, "*.sln", SearchOption.TopDirectoryOnly);
            var target = slnFiles.Length > 0 ? slnFiles[0] : conflictRepoPath;
            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = $"/c start devenv \"{target}\"",
                UseShellExecute = false,
                CreateNoWindow = true
            };
            System.Diagnostics.Process.Start(psi);
        }
        catch
        {
            // Fallback: try opening folder in explorer
            try
            {
                System.Diagnostics.Process.Start("explorer.exe", conflictRepoPath);
            }
            catch { }
        }
    }

    private void MarkConflictResolved()
    {
        if (conflictSha != null && conflictRepoId != null && conflictBranch != null)
        {
            var key = $"{conflictSha}-{conflictRepoId}";
            cherryHistory[key] = new CherryPickHistoryEntry { Status = CherryPickStatus.Resolved, Branch = conflictBranch, Timestamp = DateTime.Now };
        }
        conflictRepoPath = null;
        conflictSha = null;
        conflictRepoId = null;
        conflictBranch = null;
        showModal = false;
    }

    private void CloseModal() { showModal = false; }

    // ─── Toast (simple) ───
    private string? toastMsg; private string toastType = "i";
    private void ShowToast(string msg, string type) { toastMsg = msg; toastType = type; }

    // ─── History ───
    private CherryPickHistoryEntry? GetHistory(string key) => cherryHistory.TryGetValue(key, out var h) ? h : null;

    // ─── Filtering ───
    private void OnSearch(ChangeEventArgs e) { searchQ = (e.Value?.ToString() ?? "").ToLower(); ApplyFilter(); }
    private void OnTypeFilter(ChangeEventArgs e) { typeFilter = e.Value?.ToString() ?? ""; ApplyFilter(); }
    private void OnAssigneeFilter(ChangeEventArgs e) { assigneeFilter = e.Value?.ToString() ?? ""; ApplyFilter(); }
    private void ApplyFilter()
    {
        filtered = workItems.Where(w =>
            (string.IsNullOrEmpty(searchQ) || w.Id.Contains(searchQ) || w.Title.ToLower().Contains(searchQ) || w.Assigned.ToLower().Contains(searchQ))
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
            "id" => sortAsc ? filtered.OrderBy(w => int.TryParse(w.Id, out var n) ? n : 0).ToList() : filtered.OrderByDescending(w => int.TryParse(w.Id, out var n) ? n : 0).ToList(),
            "title" => sortAsc ? filtered.OrderBy(w => w.Title).ToList() : filtered.OrderByDescending(w => w.Title).ToList(),
            "state" => sortAsc ? filtered.OrderBy(w => w.State).ToList() : filtered.OrderByDescending(w => w.State).ToList(),
            "assigned" => sortAsc ? filtered.OrderBy(w => w.Assigned).ToList() : filtered.OrderByDescending(w => w.Assigned).ToList(),
            _ => filtered
        };
    }

    // ─── Dropdown Data ───
    private List<GlassDropdown.DropdownGroup> AreaGroups => AreaSprintSelect.AreaGroups(areas);

    private List<GlassDropdown.DropdownGroup> SprintGroups => AreaSprintSelect.SprintGroups(iterations);

    private List<GlassDropdown.DropdownOption> GlobalBranchOptions => globalBranches
        .Select(b => new GlassDropdown.DropdownOption { Value = b, Label = b }).ToList();

    private List<GlassDropdown.DropdownOption> TypeOptions => workItems.Select(w => w.Type).Distinct().OrderBy(t => t)
        .Select(t => new GlassDropdown.DropdownOption { Value = t, Label = t }).ToList();

    private List<GlassDropdown.DropdownOption> AssigneeOptions => workItems.Select(w => w.Assigned).Where(a => a != "-").Distinct().OrderBy(a => a)
        .Select(a => new GlassDropdown.DropdownOption { Value = a, Label = a }).ToList();

    private WorkItem? GetSelectedItem() => workItems.FirstOrDefault(w => w.Id == selectedItemId);

    // ─── Helpers ───
    private static string TypeCls(string t) => t.ToLower() switch { "bug" => "bug", "change request" => "cr", _ => "" };
    private static string StateCls(string s) { var l = s.ToLower(); if (l.Contains("active") || l.Contains("progress")) return "state-active"; if (l.Contains("resolve") || l.Contains("done")) return "state-resolved"; if (l.Contains("closed")) return "state-closed"; return "state-new"; }
    private static string CpBadgeCls(CherryPickStatus s) => s switch { CherryPickStatus.Done => "cp-done", CherryPickStatus.Exists => "cp-exists", CherryPickStatus.Conflict => "cp-conflict", CherryPickStatus.Resolving => "cp-resolving", CherryPickStatus.Resolved => "cp-done", _ => "" };
    private static string CpBadgeText(CherryPickStatus s) => s switch { CherryPickStatus.Done => "✅ Done", CherryPickStatus.Exists => "⚠️ Exists", CherryPickStatus.Conflict => "❌ Conflict", CherryPickStatus.Resolving => "⚡ Resolving", CherryPickStatus.Resolved => "✅ Resolved", _ => "" };
}
