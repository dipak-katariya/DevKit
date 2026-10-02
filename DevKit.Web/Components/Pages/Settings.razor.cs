using Microsoft.AspNetCore.Components;
using DevKit.Web.Models;
using DevKit.Web.Services;

namespace DevKit.Web.Components.Pages;

/// <summary>The settings form: connection, defaults, repositories and the local repository scan.</summary>
public partial class Settings
{
    private string tfsUrl = "";
    private string tfsPat = "";
    private string defaultProjectPath = "";
    private string defaultAreaPath = "";
    private string defaultSprint = "";
    private string defaultRepoId = "";
    private string teamName = "";
    private int mostUsedCount;
    private List<string> mostUsedRepoIds = new();
    private HashSet<string> mostUsedSelection = new(StringComparer.OrdinalIgnoreCase);
    private string mostUsedMsg = "";
    private bool saving, testing;
    private string statusMsg = "";
    private bool statusOk;
    private string scanMsg = "";
    private bool scanOk;
    private List<TfsRepo> repos = new();
    private List<TfsArea> areas = new();
    private List<TfsIteration> iterations = new();
    private List<LocalRepoInfo> localRepos = new();

    protected override async Task OnInitializedAsync()
    {
        tfsUrl = SettingsSvc.Tfs.Url;
        tfsPat = SettingsSvc.Tfs.Pat;
        defaultProjectPath = SettingsSvc.DefaultProjectPath;
        defaultAreaPath = SettingsSvc.DefaultAreaPath;
        defaultSprint = SettingsSvc.DefaultSprint;
        defaultRepoId = SettingsSvc.DefaultRepoId;
        teamName = SettingsSvc.TeamName;
        mostUsedCount = SettingsSvc.MostUsedRepoCount;
        SyncMostUsedFromSettings();

        if (SettingsSvc.IsConfigured)
        {
            try
            {
                var projects = await TfsApi.GetProjectsAsync();
                repos = await TfsApi.GetRepositoriesAsync();
                foreach (var p in projects)
                {
                    areas.AddRange(await TfsApi.GetAreasAsync(p.Name));
                    iterations.AddRange(await TfsApi.GetIterationsAsync(p.Name));
                }
            }
            catch (Exception ex)
            {
                Logger.LogWarning(ex, "Could not load TFS metadata for the settings page");
            }
        }

        // Scan local repos (after TFS repos loaded for matching)
        localRepos = SettingsSvc.ScanLocalRepos(repos.Count > 0 ? repos : null);
    }

    private async Task SaveAndConnect()
    {
        if (!TryValidateTfsUrl(tfsUrl, out var url, out var urlError))
        { statusMsg = $"✕ {urlError}"; statusOk = false; return; }
        if (string.IsNullOrWhiteSpace(tfsPat))
        { statusMsg = "✕ Personal Access Token is required."; statusOk = false; return; }

        saving = true;
        statusMsg = "";
        StateHasChanged();
        try
        {
            SettingsSvc.SaveTfs(url, tfsPat.Trim());
            await LoadRepos();
            statusMsg = $"✓ Saved & connected — {repos.Count} repositories found.{InsecureUrlWarning(url)}";
            statusOk = true;
        }
        catch (Exception ex)
        {
            statusMsg = $"✕ {ex.Message}";
            statusOk = false;
        }
        saving = false;
    }

    private async Task TestConnection()
    {
        if (!TryValidateTfsUrl(tfsUrl, out var url, out var urlError))
        { statusMsg = $"✕ {urlError}"; statusOk = false; return; }
        if (string.IsNullOrWhiteSpace(tfsPat))
        { statusMsg = "✕ Personal Access Token is required."; statusOk = false; return; }

        testing = true;
        statusMsg = "";
        StateHasChanged();
        try
        {
            // Temporarily apply settings for the test
            SettingsSvc.SaveTfs(url, tfsPat.Trim());
            var projects = await TfsApi.GetProjectsAsync();
            repos = await TfsApi.GetRepositoriesAsync();
            statusMsg = $"✓ Connected — {projects.Count} project(s), {repos.Count} repo(s).{InsecureUrlWarning(url)}";
            statusOk = true;
        }
        catch (Exception ex)
        {
            statusMsg = $"✕ Connection failed: {ex.Message}";
            statusOk = false;
        }
        testing = false;
    }

    private static bool TryValidateTfsUrl(string raw, out string normalized, out string error)
    {
        normalized = (raw ?? "").Trim();
        error = "";
        if (string.IsNullOrWhiteSpace(normalized))
        {
            error = "Server URL is required.";
            return false;
        }
        if (!Uri.TryCreate(normalized, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            error = "Enter a valid http(s) URL, e.g. https://tfs.example.com/tfs/Collection.";
            return false;
        }
        return true;
    }

    // PAT is sent as HTTP Basic auth; over plain http to a remote host it travels unencrypted.
    private static string InsecureUrlWarning(string url)
        => Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttp && !uri.IsLoopback
            ? "  ⚠ http detected — your PAT is sent unencrypted; prefer https."
            : "";

    private async Task LoadRepos()
    {
        repos = await TfsApi.GetRepositoriesAsync();
    }

    private void ScanAndApply()
    {
        var basePath = defaultProjectPath.Trim().TrimEnd('\\');
        SettingsSvc.DefaultProjectPath = basePath;

        // Scan for .git folders and read remote URLs
        localRepos = SettingsSvc.ScanLocalRepos(repos.Count > 0 ? repos : null);

        if (localRepos.Count == 0)
        {
            scanMsg = $"⚠ No git repositories found in {basePath}";
            scanOk = false;
        }
        else
        {
            var matched = localRepos.Count(r => r.IsMapped);
            var unmatched = localRepos.Count - matched;
            // Auto-save mapped paths
            if (repos.Count > 0) SettingsSvc.AutoMapLocalRepos(repos);
            scanMsg = $"✓ Found {localRepos.Count} local repo(s) — {matched} mapped to TFS" + (unmatched > 0 ? $", {unmatched} unmatched." : ".");
            scanOk = true;
        }
        StateHasChanged();
    }

    // ─── Team Name ───
    private void OnTeamNameChange(ChangeEventArgs e)
    {
        teamName = e.Value?.ToString()?.Trim() ?? "";
        SettingsSvc.TeamName = teamName;
    }

    // ─── Default Selection Handlers ───
    private void OnDefaultAreaChange(ChangeEventArgs e)
    {
        defaultAreaPath = e.Value?.ToString() ?? "";
        SettingsSvc.DefaultAreaPath = defaultAreaPath;
    }

    private void OnDefaultSprintChange(ChangeEventArgs e)
    {
        defaultSprint = e.Value?.ToString() ?? "";
        SettingsSvc.DefaultSprint = defaultSprint;
    }

    // ─── Code merging branch defaults ───

    private string repoBranchFilter = "";

    /// <summary>
    /// Repositories shown in the branch table. Unfiltered it is every repository across every
    /// project, which is long enough to need the filter rather than a scroll.
    /// </summary>
    private List<TfsRepo> BranchRepos => repos
        .Where(r => string.IsNullOrWhiteSpace(repoBranchFilter)
                    || r.Name.Contains(repoBranchFilter, StringComparison.OrdinalIgnoreCase)
                    || r.Project.Contains(repoBranchFilter, StringComparison.OrdinalIgnoreCase))
        .OrderBy(r => r.Project, StringComparer.OrdinalIgnoreCase)
        .ThenBy(r => r.Name, StringComparer.OrdinalIgnoreCase)
        .ToList();

    private int ConfiguredBranchCount => repos.Count(r => !SettingsSvc.GetRepoBranches(r.Id).IsEmpty);

    private void OnRepoBranchFilter(ChangeEventArgs e) => repoBranchFilter = e.Value?.ToString() ?? "";

    private void OnQaBranchChange(string repoId, string branch) =>
        SettingsSvc.SetRepoQaBranch(repoId, branch ?? "");

    private void OnMergingBranchChange(string repoId, string branch) =>
        SettingsSvc.SetRepoMergingBranch(repoId, branch ?? "");

    // Branch lists are fetched per repository the first time one of its pickers is opened.
    // There are ~50 repositories here and each listing is several paged requests, so loading
    // them up front would cost far more than anyone configuring one repository needs.
    // BranchCacheService holds the results, shared with every other page in this circuit.
    private string? loadingBranchRepoId;

    private IReadOnlyList<string> BranchesFor(string repoId) =>
        BranchCache.Peek(repoId) ?? Array.Empty<string>();

    private async Task LoadBranchesFor(TfsRepo repo)
    {
        if (BranchCache.Peek(repo.Id) != null || loadingBranchRepoId == repo.Id) return;

        loadingBranchRepoId = repo.Id;
        StateHasChanged();
        try
        {
            await BranchCache.GetAsync(repo);
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Could not load branches for repo {Repo}", repo.Name);
        }
        finally
        {
            loadingBranchRepoId = null;
            StateHasChanged();
        }
    }

    private void OnDefaultRepoChange(ChangeEventArgs e)
    {
        defaultRepoId = e.Value?.ToString() ?? "";
        SettingsSvc.DefaultRepoId = defaultRepoId;
    }

    // ─── Most-used repositories ───

    private static readonly int[] MostUsedCountChoices = { 0, 3, 5, 8, 10 };

    private static List<GlassDropdown.DropdownOption> MostUsedCountOptions => MostUsedCountChoices
        .Select(n => new GlassDropdown.DropdownOption
        {
            Value = n.ToString(),
            Label = n == 0 ? "— Off —" : $"Top {n}"
        }).ToList();

    private void OnMostUsedCountChange(ChangeEventArgs e)
    {
        mostUsedCount = int.TryParse(e.Value?.ToString(), out var n) ? n : 0;
        mostUsedMsg = "";

        // The service trims the pinned list to the new count, so read it back rather than
        // trimming here as well and risking the two drifting apart.
        SettingsSvc.MostUsedRepoCount = mostUsedCount;
        SyncMostUsedFromSettings();
    }

    /// <summary>
    /// The multi-select works with an unordered set, but pin order is the display order. Existing
    /// pins keep their position, newly ticked repositories are appended, and unticked ones drop out.
    /// </summary>
    private void OnMostUsedReposChanged(HashSet<string> selected)
    {
        var ordered = mostUsedRepoIds.Where(selected.Contains).ToList();
        var already = new HashSet<string>(ordered, StringComparer.OrdinalIgnoreCase);
        foreach (var id in selected)
            if (already.Add(id)) ordered.Add(id);

        mostUsedMsg = ordered.Count > mostUsedCount
            ? $"Only the first {mostUsedCount} were kept. Raise the count above, or unpin one first."
            : "";

        SettingsSvc.SetMostUsedRepoIds(ordered);
        SyncMostUsedFromSettings();
    }

    private void UnpinMostUsedRepo(string repoId)
    {
        mostUsedMsg = "";
        SettingsSvc.SetMostUsedRepoIds(mostUsedRepoIds.Where(id => id != repoId));
        SyncMostUsedFromSettings();
    }

    /// <summary>Settings is the single source of truth; the page mirrors it after every write.</summary>
    private void SyncMostUsedFromSettings()
    {
        mostUsedRepoIds = SettingsSvc.MostUsedRepoIds.ToList();
        mostUsedSelection = new HashSet<string>(mostUsedRepoIds, StringComparer.OrdinalIgnoreCase);
    }

    private string MostUsedRepoLabel(string repoId)
    {
        var repo = repos.FirstOrDefault(r => r.Id == repoId);
        return repo != null ? repo.Name : "(unknown repository)";
    }

    private List<MultiSelectDropdown.Option> AllRepoOptions => RepoSelect.Options(repos, mostUsedRepoIds);

    // ─── Dropdown Data ───
    private List<GlassDropdown.DropdownGroup> AreaGroups => AreaSprintSelect.AreaGroups(areas);

    private List<GlassDropdown.DropdownGroup> SprintGroups => AreaSprintSelect.SprintGroups(iterations);

    private List<GlassDropdown.DropdownGroup> RepoGroups => RepoSelect.Groups(repos, mostUsedRepoIds);

    private static string ExtractShortRemote(string url)
    {
        var idx = url.LastIndexOf("/_git/", StringComparison.OrdinalIgnoreCase);
        if (idx >= 0) return "…/_git/" + url[(idx + 6)..];
        var lastSlash = url.LastIndexOf('/');
        return lastSlash >= 0 ? "…/" + url[(lastSlash + 1)..] : url;
    }
}
