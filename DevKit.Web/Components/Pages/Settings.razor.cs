using System.Globalization;
using System.Text;
using DevKit.Web.Models;
using DevKit.Web.Services;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.AspNetCore.Components.Routing;
using Microsoft.JSInterop;

namespace DevKit.Web.Components.Pages;

/// <summary>
/// The settings form: connection (Settings.Connection.cs), defaults, repositories (Settings.Repositories.cs) and the
/// Code Merging Sheet's Update status (Settings.StatusUpdate.cs). Everything on it is shown as saved, and edits stay on
/// the page until Save settings, which is enabled while something differs from what is saved or a save failed, and
/// keeps everything else as it is.
/// </summary>
public sealed partial class Settings : IDisposable
{
    private const string LeaveUnsavedQuestion = "Your changes on the Settings page are not saved. Leave without saving them?";

    // The connection checks and branch lists stop when the page closes. Closing only ends the page's wait for the
    // shared metadata: that load goes on for the next page.
    private readonly PageLifetime page = new();

    // The preferences as saved, which the form started from; the fields below are the form as edited.
    private PortableSettings saved = new();
    private string defaultAreaPath = "";
    private string defaultSprint = "";
    private bool defaultSprintIsCurrent;
    private string defaultRepoId = "";
    private string teamName = "";
    private int mostUsedCount;
    private List<string> mostUsedRepoIds = new();
    private HashSet<string> mostUsedSelection = new(StringComparer.OrdinalIgnoreCase);
    private Dictionary<string, RepoBranchDefaults> repoBranches = new(StringComparer.OrdinalIgnoreCase);
    private string mostUsedMsg = "";

    // The circuit's shared lists, so what Settings reloads every other page sees too.
    private IReadOnlyList<TfsRepo> repos => PageState.TfsData.Repos;
    private IReadOnlyList<TfsArea> areas => PageState.TfsData.Areas;
    private IReadOnlyList<TfsIteration> iterations => PageState.TfsData.Iterations;

    // Why the pickers below are short; the lists that did load stay usable.
    private string? metadataError;

    // The areas, sprints and repositories are being read, so an empty list means "not yet", not "not connected".
    private bool listsLoading;

    protected override async Task OnInitializedAsync()
    {
        ReadConnection();
        ReadPreferences();
        SettingsSvc.Changed += OnSettingsChanged;

        if (!SettingsSvc.IsConfigured)
        {
            return;
        }

        // Every visit refreshes the lists, so newly created areas and sprints show up; a refresh that fails keeps
        // the complete lists already loaded.
        listsLoading = true;
        var load = await PageState.TfsData.ReloadAsync(page.Token);
        listsLoading = false;
        if (!load.Cancelled)
        {
            metadataError = load.ErrorMessage;
        }

        await EnsureStatusListsAsync();
    }

    /// <summary>Fills the form's preferences from what is saved: at start, after a save or an import, and to discard edits.</summary>
    private void ReadPreferences()
    {
        saved = ShownPreferences();
        teamName = saved.TeamName ?? "";
        defaultAreaPath = saved.DefaultAreaPath ?? "";
        defaultSprint = saved.DefaultSprint ?? "";
        defaultSprintIsCurrent = saved.DefaultSprintMode == DefaultSprintMode.Current;
        defaultRepoId = saved.DefaultRepoId ?? "";
        mostUsedCount = saved.MostUsedRepoCount ?? 0;
        SetMostUsed(saved.MostUsedRepoIds ?? new List<string>());
        repoBranches = (saved.RepoBranches ?? new Dictionary<string, RepoBranchDefaults>()).ToDictionary(
            pair => pair.Key,
            pair => new RepoBranchDefaults { QaBranch = pair.Value.QaBranch, MergingBranch = pair.Value.MergingBranch },
            StringComparer.OrdinalIgnoreCase);
        ReadStatusUpdate();
    }

    /// <summary>
    /// The saved preferences this form shows: not the Code Merging Sheet's repositories nor Capacity Planning, which
    /// other pages change, so a change there is not taken for one made to this form.
    /// </summary>
    private PortableSettings ShownPreferences()
    {
        var preferences = SettingsSvc.CurrentPreferences();
        preferences.CodeMergingRepoIds = null;
        preferences.CapacityPlanning = null;
        return preferences;
    }

    /// <summary>The preferences as the form holds them now.</summary>
    private PortableSettings Edited() => new PortableSettings
    {
        TeamName = teamName,
        DefaultAreaPath = defaultAreaPath,
        DefaultSprint = defaultSprint,
        DefaultSprintMode = defaultSprintIsCurrent ? DefaultSprintMode.Current : DefaultSprintMode.Chosen,
        DefaultRepoId = defaultRepoId,
        MostUsedRepoCount = mostUsedCount,
        MostUsedRepoIds = mostUsedRepoIds,
        RepoBranches = repoBranches
    }.WithStatusUpdate(statusForm);

    private bool HasPreferenceChanges => Edited().ChangesFrom(saved).HasChanges;

    /// <summary>Worked out afresh; the markup reads <see cref="unsavedShown"/>, worked out once per render.</summary>
    private bool HasUnsavedChanges => HasPreferenceChanges || ConnectionChanged;

    // HasUnsavedChanges as of this render: a full diff of the form, read by several parts of the markup.
    private bool unsavedShown;

    private string SaveLabel() => (confirming, unsavedShown) switch
    {
        (true, _) => "Saving...",
        (false, true) => "Save settings",
        _ when SettingsSvc.LastSaveError is not null => "Save again",
        _ => "Saved"
    };

    private string SaveTitle() => (SettingsSvc.SavingBlocked, unsavedShown) switch
    {
        (true, _) => "Nothing can be saved until your settings open again. Click Try again.",
        (false, true) => "Save the changes on this page. Everything else stays as it is.",
        _ when SettingsSvc.LastSaveError is not null => "The last save failed. Try it again.",
        _ => "Nothing to save: the form shows your saved settings."
    };

    // An import replaces the form's values, and saves: not over changes made here, nor over settings that did not open.
    private bool ImportLocked => unsavedShown || SettingsSvc.SavingBlocked;

    private string ImportTitle() => (SettingsSvc.SavingBlocked, unsavedShown) switch
    {
        (true, _) => "Nothing can be imported until your settings open again. Click Try again.",
        (false, true) => "Save or discard your changes first, then import the file.",
        _ => "Load settings from a DevKit file: an export, or an earlier usersettings.json. What the file does not name is kept, and your TFS connection is not changed."
    };

    private bool retrying;

    // A save made elsewhere arrived while this page was saving or trying again; it is followed once that is done.
    private bool missedSave;

    /// <summary>
    /// Raised on the thread that saved: another page of the person (shared mode, once this page has read it again), or in
    /// local mode any page. The form follows what is saved and keeps the edits made here. While this page saves or tries
    /// again, the save is followed afterwards (<see cref="FollowMissedSave"/>), so it is never lost.
    /// </summary>
    private void OnSettingsChanged() => _ = InvokeAsync(() =>
    {
        if (confirming || saving || retrying)
        {
            missedSave = true;
            return;
        }

        FollowSaveElsewhere();
        StateHasChanged();
    });

    private void FollowSaveElsewhere()
    {
        FollowSavedConnection();
        if (FollowSavedPreferences())
        {
            ShowTransfer("Your settings were saved on another page and show here now. Your changes on this page are kept, not saved yet.", ok: true);
        }
    }

    /// <summary>Follows a save made elsewhere while this page was busy, once nothing of its own is running.</summary>
    private void FollowMissedSave()
    {
        if (!missedSave || confirming || saving || retrying)
        {
            return;
        }

        missedSave = false;
        FollowSaveElsewhere();
    }

    /// <summary>Takes what is saved now into the form, keeping the edits made here. True when there were edits to keep.</summary>
    private bool FollowSavedPreferences()
    {
        if (!ShownPreferences().ChangesFrom(saved).HasChanges)
        {
            return false;
        }

        var edits = Edited().ChangesFrom(saved);
        ReadPreferences();
        if (!edits.HasChanges)
        {
            return false;
        }

        teamName = edits.TeamName ?? teamName;
        defaultAreaPath = edits.DefaultAreaPath ?? defaultAreaPath;
        defaultSprint = edits.DefaultSprint ?? defaultSprint;
        defaultSprintIsCurrent = edits.DefaultSprintMode is { } mode ? mode == DefaultSprintMode.Current : defaultSprintIsCurrent;
        defaultRepoId = edits.DefaultRepoId ?? defaultRepoId;
        mostUsedCount = edits.MostUsedRepoCount ?? mostUsedCount;
        SetMostUsed((edits.MostUsedRepoIds ?? mostUsedRepoIds).Take(mostUsedCount));
        foreach (var (id, branches) in edits.RepoBranches ?? new Dictionary<string, RepoBranchDefaults>())
        {
            repoBranches[id] = branches;
        }

        KeepStatusEdits(edits);
        return true;
    }

    /// <summary>
    /// Reads the settings again once they could not be opened; saving is allowed again when they open. Edits made here
    /// meanwhile are kept, and the message says so, or says what to do when they still do not open.
    /// </summary>
    private async Task RetryLoadAsync()
    {
        retrying = true;
        SettingsReload outcome;
        try
        {
            // Off the page's thread: the domain can take a while to answer.
            outcome = await Task.Run(SettingsSvc.ReloadFromStore);
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Trying again to open the settings failed");
            ShowTransfer("Your settings still could not be opened. Reload the page; if it keeps happening, tell whoever runs DevKit.", ok: false);
            return;
        }
        finally
        {
            retrying = false;
            missedSave = false;
        }

        ShowRetryOutcome(outcome);
    }

    private void ShowRetryOutcome(SettingsReload outcome)
    {
        if (SettingsSvc.SavingBlocked)
        {
            ShowTransfer(outcome == SettingsReload.NotReadable
                ? "Your settings opened, but this DevKit cannot read them, so nothing is saved here."
                : "Your settings still could not be opened. Try again in a minute, or reload the page.", ok: false);
            return;
        }

        FollowSavedConnection();
        FollowSavedPreferences();
        unsavedShown = HasUnsavedChanges;
        ShowTransfer(unsavedShown
            ? "Your settings opened. Your changes on this page are kept, not saved yet."
            : "Your settings opened. You can change and save them again.", ok: true);
    }

    private string transferMsg = "";
    private bool transferOk;
    private bool confirming;

    /// <summary>
    /// Saves what the form changed, and nothing else: a changed connection first, checked with TFS, then the preferences
    /// in one save. With nothing changed it only tries again a save that failed. Local mode names the settings file.
    /// </summary>
    private async Task SaveSettings()
    {
        confirming = true;
        try
        {
            if (ConnectionChanged && await SaveConnectionAsync() is var connection && connection != ConnectionSave.Saved)
            {
                ShowConnectionNotSaved(connection);
                return;
            }

            var changes = Edited().ChangesFrom(saved);
            var kept = changes.HasChanges
                ? SettingsSvc.SavePreferences(changes)
                : SettingsSvc.LastSaveError is null || await SettingsSvc.SaveAndConfirmAsync();
            if (!kept)
            {
                ShowTransfer(SettingsSvc.LastSaveError ?? "Settings could not be saved. Reload the page and try again.", ok: false);
                return;
            }

            ReadPreferences();
            ShowTransfer(Session.IsShared
                ? "Settings saved."
                : $"Settings saved in {SettingsSvc.SettingsFilePath}.", ok: true);
        }
        finally
        {
            confirming = false;
            FollowMissedSave();
        }
    }

    private void ShowConnectionNotSaved(ConnectionSave connection)
    {
        switch (connection)
        {
            case ConnectionSave.Refused:
                ShowTransfer("Nothing was saved: the TFS connection was not accepted. The message under it says why.", ok: false);
                break;
            case ConnectionSave.NotKept:
                ShowTransfer($"Nothing was saved: TFS accepted the connection, but it could not be kept. {SettingsSvc.LastSaveError}", ok: false);
                break;
            default:
                // The page was left, or the form was not filled in: the connection section says so, if anyone is there.
                break;
        }
    }

    /// <summary>Puts the form back as saved.</summary>
    private void DiscardChanges()
    {
        ReadConnection();
        ReadPreferences();
        mostUsedMsg = "";
        ShowStatus("", ok: false);
        ShowTransfer("Your changes were discarded. The form shows your saved settings.", ok: true);
    }

    /// <summary>Leaving with changes not saved asks first, so they are not lost by a stray click.</summary>
    private async Task ConfirmLeavingAsync(LocationChangingContext context)
    {
        if (!HasUnsavedChanges)
        {
            return;
        }

        try
        {
            if (!await JS.InvokeAsync<bool>("confirm", LeaveUnsavedQuestion))
            {
                context.PreventNavigation();
            }
        }
        catch (JSDisconnectedException ex)
        {
            Logger.LogDebug(ex, "The page closed while asking whether to leave without saving");
        }
        catch (JSException ex)
        {
            Logger.LogError(ex, "The browser could not ask whether to leave the Settings page without saving; it stays open");
            StayWithUnsavedChanges(context);
        }
        catch (TaskCanceledException ex)
        {
            Logger.LogError(ex, "The question whether to leave the Settings page without saving got no answer in time; it stays open");
            StayWithUnsavedChanges(context);
        }
    }

    private void StayWithUnsavedChanges(LocationChangingContext context)
    {
        context.PreventNavigation();
        ShowTransfer("Your changes are not saved, so the page stayed open. Save or discard them, then leave.", ok: false);
        StateHasChanged();
    }

    /// <summary>Hands the saved settings to the browser as a file; it holds no TFS connection or token.</summary>
    private async Task ExportSettings()
    {
        var file = Encoding.UTF8.GetBytes(SettingsSvc.ExportSettings());
        try
        {
            await JS.InvokeVoidAsync("devKit.downloadFile",
                $"devkit-settings-{DateTime.Today.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}.json",
                "application/json", Convert.ToBase64String(file));
            ShowTransfer(HasUnsavedChanges
                ? "Your saved settings were exported; changes not saved yet are not in the file. It does not include your TFS connection or token."
                : "Settings exported. The file does not include your TFS connection or token.", ok: true);
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

    /// <summary>
    /// Loads a settings file the person picked: an export, or an earlier DevKit's whole settings file. What it does not
    /// name is kept, and the TFS connection is never part of it. Not while the form has changes, which it would replace.
    /// </summary>
    private async Task ImportSettings(InputFileChangeEventArgs e)
    {
        unsavedShown = HasUnsavedChanges;
        if (ImportLocked)
        {
            ShowTransfer(ImportTitle(), ok: false);
            return;
        }

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

    private void OnTeamNameChange(ChangeEventArgs e) => teamName = e.Value?.ToString()?.Trim() ?? "";

    private void OnDefaultAreaChange(ChangeEventArgs e) => defaultAreaPath = e.Value?.ToString() ?? "";

    private void OnDefaultSprintChange(ChangeEventArgs e) => defaultSprint = e.Value?.ToString() ?? "";

    private void SetDefaultSprintMode(bool current) => defaultSprintIsCurrent = current;

    private void OnDefaultRepoChange(ChangeEventArgs e) => defaultRepoId = e.Value?.ToString() ?? "";

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

    private List<GlassDropdown.DropdownGroup> AreaGroups => AreaSprintSelect.AreaGroups(areas);

    private List<GlassDropdown.DropdownGroup> SprintGroups => AreaSprintSelect.SprintGroups(iterations);

    private List<GlassDropdown.DropdownGroup> RepoGroups => RepoSelect.Groups(repos, mostUsedRepoIds);

    public void Dispose()
    {
        SettingsSvc.Changed -= OnSettingsChanged;
        CancellationSources.CancelAndDispose(ref patHide);
        page.Dispose();
    }
}
