using System.Globalization;
using System.Text;
using DevKit.Web.Models;
using DevKit.Web.Services;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.JSInterop;

namespace DevKit.Web.Components.Pages;

/// <summary>The settings form: connection, defaults and repositories.</summary>
public sealed partial class Settings : IDisposable
{
    // The connection checks and branch lists stop when the page closes. Closing only ends the page's wait for the
    // shared metadata: that load goes on for the next page.
    private readonly PageLifetime page = new();

    private string tfsUrl = "";
    private TfsAuthMode tfsAuth;
    // Only ever what the user types: the stored PAT is never sent to the browser.
    private string tfsPat = "";
    private bool hasStoredPat;
    private string defaultAreaPath = "";
    private string defaultSprint = "";
    private bool defaultSprintIsCurrent;
    private string defaultRepoId = "";
    private string teamName = "";
    private int mostUsedCount;
    private List<string> mostUsedRepoIds = new();
    private HashSet<string> mostUsedSelection = new(StringComparer.OrdinalIgnoreCase);
    private string mostUsedMsg = "";
    private bool saving, testing;
    private string statusMsg = "";
    private bool statusOk;

    // The circuit's shared lists, so what Settings reloads every other page sees too.
    private IReadOnlyList<TfsRepo> repos => PageState.TfsData.Repos;
    private IReadOnlyList<TfsArea> areas => PageState.TfsData.Areas;
    private IReadOnlyList<TfsIteration> iterations => PageState.TfsData.Iterations;

    // Why the pickers below are short; the lists that did load stay usable.
    private string? metadataError;

    protected override async Task OnInitializedAsync()
    {
        tfsUrl = SettingsSvc.FixedTfsUrl ?? SettingsSvc.Tfs.Url;
        tfsAuth = SettingsSvc.IsConfigured ? SettingsSvc.Tfs.Auth : FirstSignIn;
        hasStoredPat = SettingsSvc.HasStoredPat;
        ReadPreferences();

        if (!SettingsSvc.IsConfigured)
        {
            return;
        }

        // Every visit refreshes the lists, so newly created areas and sprints show up; a refresh that fails keeps
        // the complete lists already loaded.
        var load = await PageState.TfsData.ReloadAsync(page.Token);
        if (!load.Cancelled)
        {
            metadataError = load.ErrorMessage;
        }
    }

    /// <summary>Fills the form's preferences from Settings, at start and after an import.</summary>
    private void ReadPreferences()
    {
        defaultAreaPath = SettingsSvc.DefaultAreaPath;
        defaultSprint = SettingsSvc.DefaultSprint;
        defaultSprintIsCurrent = SettingsSvc.DefaultSprintIsCurrent;
        defaultRepoId = SettingsSvc.DefaultRepoId;
        teamName = SettingsSvc.TeamName;
        mostUsedCount = SettingsSvc.MostUsedRepoCount;
        SyncMostUsedFromSettings();
    }

    // ─── Export and import ───
    private string transferMsg = "";
    private bool transferOk;

    /// <summary>Hands the settings file to the browser to save; it holds no TFS connection or token.</summary>
    private async Task ExportSettings()
    {
        var file = Encoding.UTF8.GetBytes(SettingsSvc.ExportSettings());
        try
        {
            await JS.InvokeVoidAsync("devKit.downloadFile",
                $"devkit-settings-{DateTime.Today.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}.json",
                "application/json", Convert.ToBase64String(file));
            ShowTransfer("Settings exported. The file does not include your TFS connection or token.", ok: true);
        }
        catch (JSDisconnectedException ex)
        {
            Logger.LogDebug(ex, "The page closed before the exported settings reached the browser");
        }
        catch (JSException ex)
        {
            Logger.LogError(ex, "The browser could not save the exported settings file");
            ShowTransfer("The browser could not save the file. Try again.", ok: false);
        }
    }

    /// <summary>Loads a settings file the person picked; the TFS connection is never part of it.</summary>
    private async Task ImportSettings(InputFileChangeEventArgs e)
    {
        try
        {
            await using var stream = e.File.OpenReadStream(SettingsService.MaxImportBytes, page.Token);
            using var reader = new StreamReader(stream, Encoding.UTF8);
            var result = SettingsSvc.ImportSettings(await reader.ReadToEndAsync(page.Token));
            if (result.Imported)
            {
                ReadPreferences();
            }

            ShowTransfer(result.Message, result.Imported);
        }
        catch (OperationCanceledException ex) when (page.Token.IsCancellationRequested)
        {
            Logger.LogDebug(ex, "The page was left while a settings file was being read");
        }
        catch (Exception ex) when (ex is IOException or JSException)
        {
            Logger.LogError(ex, "A settings file chosen for import could not be read");
            ShowTransfer($"The file could not be read. A DevKit settings file is at most {SettingsService.MaxImportBytes / 1024} KB.", ok: false);
        }
    }

    private void ShowTransfer(string message, bool ok)
    {
        transferMsg = message;
        transferOk = ok;
    }

    /// <summary>A connection not set up yet starts on Windows sign-in where it is allowed, since it needs no token.</summary>
    private TfsAuthMode FirstSignIn => SettingsSvc.AllowsWindowsSignIn ? TfsAuthMode.Windows : TfsAuthMode.Pat;

    /// <summary>The Windows account DevKit runs as, which is the one Windows sign-in presents to TFS.</summary>
    private static string WindowsAccount => $@"{Environment.UserDomainName}\{Environment.UserName}";

    /// <summary>Switches how the form signs in. A token typed for the other sign-in is not kept.</summary>
    private void SetSignIn(TfsAuthMode auth)
    {
        tfsAuth = auth;
        tfsPat = "";
        ShowStatus("", ok: false);
    }

    /// <summary>What the form asks TFS with, for the sign-in chosen above it.</summary>
    private TfsConnectionForm.Candidate FormCandidate() => tfsAuth == TfsAuthMode.Windows
        ? TfsConnectionForm.ChooseWindowsSignIn(tfsUrl)
        : TfsConnectionForm.Choose(tfsUrl, tfsPat, SettingsSvc);

    /// <summary>Names the account TFS ran the check as, when TFS said, so the status shows who DevKit signed in as.</summary>
    private static string SignedInAs(TfsConnectionCheck check) =>
        check.Account.Length == 0 ? "" : $" as {check.Account}";

    /// <summary>
    /// Checks the server and sign-in first and saves them only when TFS accepts them, so a mistyped token never
    /// replaces a working one. The status says what TFS answered, or why the settings were not saved.
    /// </summary>
    private async Task SaveAndConnect()
    {
        var (url, pat, error, auth) = FormCandidate();
        if (error.Length > 0)
        {
            ShowStatus($"✕ {error}", ok: false);
            return;
        }

        saving = true;
        ShowStatus("", ok: false);
        StateHasChanged();
        try
        {
            // Nothing is saved until TFS has answered, so leaving before then saves nothing.
            var check = await TfsApi.TestConnectionAsync(url, auth, pat, page.Token);
            if (!SettingsSvc.SaveTfs(url, pat, auth))
            {
                ShowStatus($"✕ TFS accepted the sign-in, but it was not saved. {SettingsSvc.LastSaveError}", ok: false);
                return;
            }

            Logger.LogInformation("TFS connection saved; DevKit signs in with {SignIn}", auth);
            tfsPat = "";
            hasStoredPat = SettingsSvc.HasStoredPat;

            // The lists belonged to the server just replaced, so every page of the circuit now reads the new one's.
            // A list that fails to load is shown in the notice: the settings are saved either way.
            PageState.TfsData.Invalidate();
            var load = await PageState.TfsData.EnsureLoadedAsync(page.Token);
            if (load.Cancelled)
            {
                return;
            }
            metadataError = load.ErrorMessage;
            ShowStatus($"✓ Saved and connected{SignedInAs(check)}. Found {check.ProjectCount} project(s) and {repos.Count} repositories.{TfsConnectionForm.InsecureUrlWarning(url, auth)}", ok: true);
        }
        catch (OperationCanceledException ex) when (page.Token.IsCancellationRequested)
        {
            Logger.LogDebug(ex, "Save & Connect stopped because the page was left before TFS answered; nothing was saved");
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "The TFS connection check with {SignIn} sign-in failed, so the connection was not saved", auth);
            ShowStatus($"✕ Not saved: {TfsErrors.Describe(ex)}", ok: false);
        }
        finally
        {
            saving = false;
        }
    }

    /// <summary>Tries the server and sign-in in the form without saving anything.</summary>
    private async Task TestConnection()
    {
        var (url, pat, error, auth) = FormCandidate();
        if (error.Length > 0)
        {
            ShowStatus($"✕ {error}", ok: false);
            return;
        }

        testing = true;
        ShowStatus("", ok: false);
        StateHasChanged();
        try
        {
            var check = await TfsApi.TestConnectionAsync(url, auth, pat, page.Token);
            ShowStatus($"✓ Connected{SignedInAs(check)}. Found {check.ProjectCount} project(s). Nothing was saved.{TfsConnectionForm.InsecureUrlWarning(url, auth)}", ok: true);
        }
        catch (OperationCanceledException ex) when (page.Token.IsCancellationRequested)
        {
            Logger.LogDebug(ex, "The connection test stopped because the page was left");
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "The TFS connection test with {SignIn} sign-in failed", auth);
            ShowStatus($"✕ Connection failed: {TfsErrors.Describe(ex)}", ok: false);
        }
        finally
        {
            testing = false;
        }
    }

    private void ShowStatus(string message, bool ok)
    {
        statusMsg = message;
        statusOk = ok;
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

    private void SetDefaultSprintMode(bool current)
    {
        defaultSprintIsCurrent = current;
        SettingsSvc.DefaultSprintIsCurrent = current;
    }

    /// <summary>The sprint "current sprint" means today, in the default area's project.</summary>
    private string CurrentSprintLabel
    {
        get
        {
            if (iterations.Count == 0)
            {
                return "Shown once the sprints have loaded";
            }

            var sprint = AreaSprintSelect.CurrentSprint(iterations, AreaSprintSelect.Parse(defaultAreaPath)?.Project, DateTime.Today);
            return sprint is null
                ? "No sprint in TFS has dates that include today"
                : $"{sprint.Name} ({sprint.StartDate:d MMM} to {sprint.FinishDate:d MMM})";
        }
    }

    // ─── Code merging branch defaults ───

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

    private void OnDefaultRepoChange(ChangeEventArgs e)
    {
        defaultRepoId = e.Value?.ToString() ?? "";
        SettingsSvc.DefaultRepoId = defaultRepoId;
    }

    // ─── Most-used repositories ───

    private static readonly int[] MostUsedCountChoices = { 0, 3, 5, 8, 10 };

    private static List<GlassDropdown.DropdownOption> MostUsedCountOptions() => MostUsedCountChoices
        .Select(n => new GlassDropdown.DropdownOption
        {
            Value = n.ToString(),
            Label = n == 0 ? "Off" : $"Top {n}"
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
        ordered.AddRange(selected.Except(ordered, StringComparer.OrdinalIgnoreCase).ToList());

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

    public void Dispose() => page.Dispose();
}
