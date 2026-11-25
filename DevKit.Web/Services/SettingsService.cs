using System.Security.Cryptography;
using System.Text.Json;
using DevKit.Web.Models;

namespace DevKit.Web.Services;

/// <summary>
/// The user's settings, held in memory and kept with the PAT encrypted at rest: in a per-user usersettings.json (see
/// <see cref="SettingsFile"/>) in local mode, with the PAT encrypted for its server, or in shared mode on the DevKit
/// server, sealed as a whole for the signed-in person alone (see <see cref="PersonSettingsStore"/>). Every change is saved as it is
/// made, and a save that fails is reported through <see cref="LastSaveError"/> rather than lost. The TFS connection lives
/// in SettingsService.Connection.cs, repository branch settings in SettingsService.Repos.cs; the stored shape is
/// <see cref="UserSettings"/>.
/// </summary>
public partial class SettingsService
{
    private const string SaveFailedMessage =
        "Settings could not be saved. Check that the settings file is not read-only or open in another program, then try again.";

    internal static readonly JsonSerializerOptions FileFormat = new() { WriteIndented = true };

    private readonly ISettingsStore _store;
    private readonly ILogger<SettingsService> _logger;
    private readonly object _sync = new();
    private UserSettings _settings = new();

    // Set when the file could not be read: saving over it would replace what it holds with defaults.
    private string? _saveBlockedReason;

    // A stored PAT this account cannot decrypt, written back unchanged so a save never erases it.
    private string? _undecryptablePat;

    // The settings as last loaded or saved, as stored: a save tells the store what changed since (see ISettingsStore.Write).
    private string? _savedText;

    // Counts the saves tried, so a reload read outside the lock can tell that the settings changed while it read.
    private long _changes;

    public SettingsService(ILogger<SettingsService> logger)
        : this(logger, SettingsFile.ResolvePath(), LegacySettingsPathToMove())
    {
    }

    /// <param name="logger">Receives every load and save failure.</param>
    /// <param name="settingsPath">The settings file to use.</param>
    /// <param name="legacySettingsPath">
    /// Where builds before the per-user file kept it, to move from on first start; null to move nothing.
    /// </param>
    internal SettingsService(ILogger<SettingsService> logger, string settingsPath, string? legacySettingsPath)
    {
        _logger = logger;
        var file = new SettingsFile(settingsPath);
        _store = file;
        Load(() => MoveLegacySettings(file, legacySettingsPath) ?? file.ReadText());
        RestrictFileAccess(file);
    }

    /// <summary>
    /// Shared mode: one person's settings, kept on the DevKit server for them alone. Nothing is in use until
    /// <see cref="LoadFromStore"/> runs, once the store knows who is signed in.
    /// </summary>
    /// <param name="fixedTfsUrl">The one TFS server everyone works against; people cannot point DevKit elsewhere.</param>
    internal SettingsService(ILogger<SettingsService> logger, ISettingsStore store, string fixedTfsUrl)
    {
        _logger = logger;
        _store = store;
        FixedTfsUrl = fixedTfsUrl;
        AllowsWindowsSignIn = false;
        _settings.Tfs.Url = fixedTfsUrl;
        // Until settings are loaded, the defaults: a first save then adds only what it changed to whatever another of
        // the person's pages saved first.
        _savedText = PersistableText();
    }

    /// <summary>Where the settings are kept: the settings file, or (shared mode) the DevKit server, for this person alone.</summary>
    public string SettingsFilePath => _store.Location;

    /// <summary>The TFS server everyone in shared mode works against; null in local mode, where the user picks it.</summary>
    public string? FixedTfsUrl { get; }

    /// <summary>Shared mode: who these settings belong to, as a tag that names nobody, for their share of the TFS slots; null in local mode.</summary>
    internal string? Person => _store.Person;

    /// <summary>
    /// Whether TFS may be signed in to as the Windows account DevKit runs as. Always in local mode; in shared mode only
    /// for the person signed in as that same account, since for anyone else it would act as someone they are not.
    /// </summary>
    public bool AllowsWindowsSignIn { get; private set; } = true;

    /// <summary>
    /// Shared mode: reads this person's settings from the store, through the same checks as a settings file. The
    /// server is always <see cref="FixedTfsUrl"/>; a PAT saved for another server is dropped rather than sent to this one.
    /// </summary>
    /// <param name="allowsWindowsSignIn">Whether this person is the account DevKit runs as.</param>
    /// <param name="earlierText">
    /// Settings an earlier DevKit kept somewhere else, read instead of the store's, which holds none, and then saved
    /// into it; null to read the store.
    /// </param>
    internal void LoadFromStore(bool allowsWindowsSignIn, string? earlierText = null)
    {
        lock (_sync)
        {
            AllowsWindowsSignIn = allowsWindowsSignIn;
            if (earlierText is null)
            {
                Load(_store.ReadText);
                return;
            }

            Load(() => earlierText);
            // The store keeps them from now on, also when nothing in them needed saving again. They replace whatever it
            // keeps whole: as changes, they would be measured against themselves, which leaves nothing to send.
            _savedText = null;
            Save();
        }
    }

    /// <summary>Why the most recent save failed, or null when it succeeded.</summary>
    public string? LastSaveError { get; private set; }

    /// <summary>What went wrong reading the settings at startup, or null when nothing did.</summary>
    public string? LoadProblem { get; private set; }

    /// <summary>
    /// Whether the store kept no settings when they were loaded, and nothing was saved since: in shared mode, a
    /// person's first visit.
    /// </summary>
    public bool StartedEmpty { get; private set; }

    /// <summary>
    /// Saves the settings as they are, for a person who wants to see that their settings are saved. False, with the
    /// reason in <see cref="LastSaveError"/>, when they were not kept.
    /// </summary>
    public Task<bool> SaveAndConfirmAsync() => Task.FromResult(Save());

    public TfsSettings Tfs => _settings.Tfs;
    public string DefaultAreaPath
    {
        get => _settings.DefaultAreaPath;
        set => Update(() => _settings.DefaultAreaPath = value);
    }
    public string DefaultSprint
    {
        get => _settings.DefaultSprint;
        set => Update(() => _settings.DefaultSprint = value);
    }

    /// <summary>
    /// Whether pages start on the sprint running today rather than on <see cref="DefaultSprint"/>. Settings saved before
    /// the choice existed count as "current" unless they name a sprint, so a sprint picked earlier is kept.
    /// </summary>
    public bool DefaultSprintIsCurrent
    {
        get => _settings.DefaultSprintMode switch
        {
            DefaultSprintMode.Current => true,
            DefaultSprintMode.Chosen => false,
            _ => string.IsNullOrEmpty(_settings.DefaultSprint)
        };
        set => Update(() => _settings.DefaultSprintMode = value ? DefaultSprintMode.Current : DefaultSprintMode.Chosen);
    }
    public string DefaultRepoId
    {
        get => _settings.DefaultRepoId;
        set => Update(() => _settings.DefaultRepoId = value);
    }

    // ═══ MOST-USED REPOSITORIES ═══

    /// <summary>Upper bound on the pinned list, so a hand-edited settings file cannot fill a dropdown.</summary>
    public const int MaxMostUsedRepoCount = 20;

    /// <summary>
    /// How many repositories may be pinned to the top of every repository picker. Zero turns the
    /// feature off. Lowering it trims the pinned list so the two can never disagree.
    /// </summary>
    public int MostUsedRepoCount
    {
        get => Math.Clamp(_settings.MostUsedRepoCount, 0, MaxMostUsedRepoCount);
        set => Update(() =>
        {
            var clamped = Math.Clamp(value, 0, MaxMostUsedRepoCount);
            _settings.MostUsedRepoCount = clamped;
            TrimMostUsedRepos(clamped);
        });
    }

    /// <summary>
    /// Pinned repository ids, in the order the user picked them. That order is the display order.
    /// Ids, not names: names are not unique across projects.
    /// </summary>
    public IReadOnlyList<string> MostUsedRepoIds => _settings.MostUsedRepoIds;

    public void SetMostUsedRepoIds(IEnumerable<string>? repoIds) =>
        Update(() => _settings.MostUsedRepoIds = DistinctIds(repoIds, MostUsedRepoCount));

    /// <summary>Repository ids trimmed, without blanks or repeats, in their order, and at most <paramref name="cap"/> of them.</summary>
    private static List<string> DistinctIds(IEnumerable<string>? ids, int cap)
    {
        var distinct = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var id in ids ?? Enumerable.Empty<string>())
        {
            // Checked before adding, not after: the other way round a cap of zero still kept one.
            if (distinct.Count >= cap) break;
            if (string.IsNullOrWhiteSpace(id)) continue;

            var trimmed = id.Trim();
            if (seen.Add(trimmed)) distinct.Add(trimmed);
        }

        return distinct;
    }

    private void TrimMostUsedRepos(int count)
    {
        if (_settings.MostUsedRepoIds.Count > count)
            _settings.MostUsedRepoIds = _settings.MostUsedRepoIds.Take(count).ToList();
    }
    public string TeamName
    {
        get => _settings.TeamName;
        set => Update(() => _settings.TeamName = value);
    }

    /// <summary>How the Dashboard lays out the tools; kept with the other settings, so it outlasts every restart.</summary>
    public DashboardView DashboardView
    {
        get => _settings.DashboardView;
        set => Update(() => _settings.DashboardView = Enum.IsDefined(value) ? value : DashboardView.Grid);
    }

    /// <summary>True once the first-visit tour has opened; the Guide page sets it back to show the tour again.</summary>
    public bool GuideTourSeen
    {
        get => _settings.GuideTourSeen;
        set => Update(() => _settings.GuideTourSeen = value);
    }

    /// <summary>
    /// Whether the first-visit tour opens now. It is marked as seen in the same step, before anyone reads it, so a tab
    /// closed, reloaded or signed out during the tour never brings it back. Not while saving is stopped, nor when the
    /// mark could not be saved: the tour would then come back on every page.
    /// </summary>
    public bool TryStartGuideTour()
    {
        lock (_sync)
        {
            if (SavingBlocked || _settings.GuideTourSeen)
            {
                return false;
            }

            var earlierError = LastSaveError;
            if (UpdateOrRollBack(() => _settings.GuideTourSeen = true))
            {
                return true;
            }

            // Nobody asked to save this mark, so its failure is logged rather than shown as a save problem of theirs.
            _logger.LogWarning("The first-visit tour was not opened: the mark that it was seen could not be saved ({Reason})", LastSaveError);
            LastSaveError = earlierError;
            return false;
        }
    }

    public bool IsConfigured => Tfs.IsConfigured && (Tfs.Auth != TfsAuthMode.Windows || AllowsWindowsSignIn);

    // ═══ CAPACITY PLANNING ═══

    /// <summary>
    /// The Capacity Planning formula settings, sanitised. The first read on a machine that
    /// predates them builds them from the older per-tag Deliverable toggles and saves, so the
    /// tab opens showing the same numbers it did before. Returns a copy: callers edit it and
    /// hand it back through <see cref="SaveCapacityPlanning"/>, so a half-finished edit in the
    /// settings dialog never leaks into another page's calculation.
    /// </summary>
    public CapacityPlanningSettings CapacityPlanning
    {
        get
        {
            lock (_sync)
            {
                if (_settings.CapacityPlanning is null)
                {
                    _settings.CapacityPlanning = CapacityPlanningSettings.FromLegacy(_settings.DeliverableTags).Normalized();
                    Save();
                }
                // Normalized is itself a deep copy, and re-sanitises a value hand-edited on disk.
                return _settings.CapacityPlanning.Normalized();
            }
        }
    }

    public void SaveCapacityPlanning(CapacityPlanningSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        Update(() => _settings.CapacityPlanning = settings.Normalized());
    }

    /// <summary>
    /// Writes the settings to disk. Returns false, with the reason in <see cref="LastSaveError"/>, when
    /// they could not be written; the settings in memory stay in use either way.
    /// </summary>
    public bool Save()
    {
        lock (_sync)
        {
            try
            {
                return WriteSettings();
            }
            finally
            {
                Changed?.Invoke();
            }
        }
    }

    /// <summary>
    /// Raised after every save, whether it worked or not, and after a change that was refused, so whatever shows the
    /// settings (the top bar's connection state, a save problem) can show them again. Handlers must not save.
    /// </summary>
    public event Action? Changed;

    private bool WriteSettings()
    {
        // Any save, kept or not, may have changed the settings in memory: a reload read before it is out of date.
        Interlocked.Increment(ref _changes);
        if (_saveBlockedReason is not null)
        {
            LastSaveError = _saveBlockedReason;
            return false;
        }

        try
        {
            LastSaveError = null;
            var text = PersistableText();
            _store.Write(text, _savedText);
            _savedText = text;
            StartedEmpty = false;
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or CryptographicException or JsonException)
        {
            _logger.LogError(ex, "Settings could not be saved");
            LastSaveError = _store.SaveFailedMessage ?? SaveFailedMessage;
            return false;
        }
    }

    /// <summary>Applies one change and saves it, one caller at a time.</summary>
    private bool Update(Action change)
    {
        lock (_sync)
        {
            change();
            return Save();
        }
    }

    /// <summary>
    /// Applies one change and saves it; when the save fails, the change is taken back, so the settings in use stay the
    /// ones stored. False, with the reason in <see cref="LastSaveError"/>, when it was not kept.
    /// </summary>
    private bool UpdateOrRollBack(Action change)
    {
        lock (_sync)
        {
            var before = CopyOfSettings();
            var undecryptable = _undecryptablePat;
            try
            {
                change();
                if (WriteSettings())
                {
                    return true;
                }

                _settings = before;
                _undecryptablePat = undecryptable;
                return false;
            }
            finally
            {
                Changed?.Invoke();
            }
        }
    }

    /// <summary>A deep copy of the settings in use, the PAT as held in memory included; it never leaves the process.</summary>
    private UserSettings CopyOfSettings()
    {
        var copy = JsonSerializer.Deserialize<UserSettings>(JsonSerializer.Serialize(_settings)) ?? new UserSettings();
        copy.Tfs ??= new TfsSettings();
        copy.StatusUpdate ??= new StatusUpdateSettings();
        return copy;
    }

    /// <summary>Loaded settings made ready for use, not in use yet: what is stored, and whether it needs saving again.</summary>
    private sealed record PreparedSettings(UserSettings Settings, string SavedText, bool NeedsResave);

    /// <summary>Puts loaded settings in use. True when they need saving again: the PAT was stored in an older form.</summary>
    private bool Apply(UserSettings loaded) => Publish(Prepare(loaded));

    /// <summary>
    /// Decrypts the PAT of <paramref name="loaded"/> and drops one for another server, before anything uses them: TFS
    /// requests read the settings in use without the lock, so they must never see them half done.
    /// </summary>
    private PreparedSettings Prepare(UserSettings loaded)
    {
        var stored = loaded.Tfs.Pat ?? "";
        loaded.Tfs.Pat = DecryptPat(stored, loaded.Tfs.Url);
        // What is stored, as this build reads it: a value it does not know reads as its default here, and stays stored
        // until the person changes that setting.
        var savedText = PersistableText(loaded);
        if (FixedTfsUrl is not null && !SameServer(loaded.Tfs.Url, FixedTfsUrl))
        {
            // The token belongs to another server, so it must never be sent to this one.
            loaded.Tfs.Url = FixedTfsUrl;
            loaded.Tfs.Pat = "";
            _undecryptablePat = null;
        }

        return new PreparedSettings(loaded, savedText, !string.IsNullOrEmpty(loaded.Tfs.Pat) && !_store.Pat.IsCurrent(stored));
    }

    private bool Publish(PreparedSettings prepared)
    {
        _savedText = prepared.SavedText;
        _settings = prepared.Settings;
        return prepared.NeedsResave;
    }

    private void RestrictFileAccess(SettingsFile file)
    {
        try
        {
            file.RestrictExistingFiles();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or InvalidOperationException)
        {
            _logger.LogError(ex, "The settings file could not be limited to the current Windows account");
        }
    }

    private string PersistableText() => PersistableText(_settings);

    private string PersistableText(UserSettings settings) => JsonSerializer.Serialize(BuildPersistableCopy(settings), FileFormat);

    // The PAT is held in memory as plaintext (the API client needs it) but persisted
    // encrypted, by itself or with the whole text. Build a copy so the in-memory value is untouched.
    private UserSettings BuildPersistableCopy(UserSettings settings) => new()
    {
        Tfs = new TfsSettings
        {
            Url = settings.Tfs.Url,
            Auth = settings.Tfs.Auth,
            Pat = PersistedPat(settings.Tfs),
            ApiVersion = settings.Tfs.ApiVersion,
            UnknownFields = settings.Tfs.UnknownFields
        },
        DefaultAreaPath = settings.DefaultAreaPath,
        DefaultSprint = settings.DefaultSprint,
        DefaultSprintMode = settings.DefaultSprintMode,
        DefaultRepoId = settings.DefaultRepoId,
        MostUsedRepoCount = settings.MostUsedRepoCount,
        MostUsedRepoIds = settings.MostUsedRepoIds,
        TeamName = settings.TeamName,
        DeliverableTags = settings.DeliverableTags,
        CapacityPlanning = settings.CapacityPlanning,
        CodeMergingTargetBranch = settings.CodeMergingTargetBranch,
        CodeMergingRepoIds = settings.CodeMergingRepoIds,
        RepoTargetBranches = settings.RepoTargetBranches,
        RepoBranches = settings.RepoBranches,
        DashboardView = settings.DashboardView,
        GuideTourSeen = settings.GuideTourSeen,
        StatusUpdate = settings.StatusUpdate,
        UnknownFields = settings.UnknownFields
    };
}
