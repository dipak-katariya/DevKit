using System.Security.Cryptography;
using System.Text.Json;
using DevKit.Web.Models;

namespace DevKit.Web.Services;

/// <summary>
/// The user's settings, held in memory and kept with the PAT encrypted at rest for the server it belongs to: in a
/// per-user usersettings.json (see <see cref="SettingsFile"/>) in local mode, or in shared mode in the signed-in
/// person's own file on the DevKit server (see <see cref="ServerSettingsStore"/>). Every change is saved as it is
/// made, and a save that fails is reported through <see cref="LastSaveError"/> rather than lost. Repository branch
/// settings live in SettingsService.Repos.cs; the stored shape is <see cref="UserSettings"/>.
/// </summary>
public partial class SettingsService
{
    private const string PatContextPrefix = "devkit-pat|";

    private const string SaveFailedMessage =
        "Settings could not be saved. Check that the settings file is not read-only or open in another program, then try again.";

    private const string WindowsSignInRefusedMessage =
        "Windows sign-in to TFS is only for the Windows account DevKit runs as. Use a personal access token.";

    private static readonly JsonSerializerOptions FileFormat = new() { WriteIndented = true };

    private readonly ISettingsStore _store;
    private readonly ILogger<SettingsService> _logger;
    private readonly object _sync = new();
    private UserSettings _settings = new();

    // Set when the file could not be read: saving over it would replace what it holds with defaults.
    private string? _saveBlockedReason;

    // A stored PAT this account cannot decrypt, written back unchanged so a save never erases it.
    private string? _undecryptablePat;

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
    /// Shared mode: one person's settings, kept in their own file on the server. Nothing is in use until
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
    }

    /// <summary>Where the settings are kept: the settings file, or (shared mode) this person's file on the server.</summary>
    public string SettingsFilePath => _store.Location;

    /// <summary>The TFS server everyone in shared mode works against; null in local mode, where the user picks it.</summary>
    public string? FixedTfsUrl { get; }

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
    internal void LoadFromStore(bool allowsWindowsSignIn)
    {
        lock (_sync)
        {
            AllowsWindowsSignIn = allowsWindowsSignIn;
            Load(_store.ReadText);
        }
    }

    /// <summary>Why the most recent save failed, or null when it succeeded.</summary>
    public string? LastSaveError { get; private set; }

    /// <summary>What went wrong reading the settings at startup, or null when nothing did.</summary>
    public string? LoadProblem { get; private set; }

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
    public bool IsConfigured => Tfs.IsConfigured && (Tfs.Auth != TfsAuthMode.Windows || AllowsWindowsSignIn);

    /// <summary>True when a PAT is stored, without handing it out.</summary>
    public bool HasStoredPat => !string.IsNullOrEmpty(_settings.Tfs.Pat);

    /// <summary>
    /// True when <paramref name="url"/> is the server the stored PAT belongs to, however it was typed: the
    /// same comparison that decides whether the stored PAT decrypts.
    /// </summary>
    public bool IsStoredServer(string url) => SameServer(url, _settings.Tfs.Url);

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
    /// Sets the server, how DevKit signs in to it, and the PAT together, since the PAT is encrypted for that
    /// server. Windows sign-in keeps no PAT: one passed with it is ignored, and one stored before is dropped,
    /// from the backup too. In shared mode the server is always <see cref="FixedTfsUrl"/>, and Windows sign-in is
    /// refused unless <see cref="AllowsWindowsSignIn"/>. Returns false when the save failed or was refused;
    /// <see cref="LastSaveError"/> says why.
    /// </summary>
    public bool SaveTfs(string url, string pat, TfsAuthMode auth = TfsAuthMode.Pat)
    {
        lock (_sync)
        {
            if (auth == TfsAuthMode.Windows && !AllowsWindowsSignIn)
            {
                LastSaveError = WindowsSignInRefusedMessage;
                Changed?.Invoke();
                return false;
            }

            var dropsStoredPat = auth == TfsAuthMode.Windows && (HasStoredPat || _undecryptablePat is not null);
            _settings.Tfs.Url = FixedTfsUrl ?? url;
            _settings.Tfs.Auth = auth;
            _settings.Tfs.Pat = auth == TfsAuthMode.Windows ? "" : pat;
            _undecryptablePat = null;
            return dropsStoredPat ? SaveDroppingOldPat() : Save();
        }
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
        if (_saveBlockedReason is not null)
        {
            LastSaveError = _saveBlockedReason;
            return false;
        }

        try
        {
            _store.Write(JsonSerializer.Serialize(BuildPersistableCopy(), FileFormat));
            LastSaveError = null;
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or CryptographicException)
        {
            _logger.LogError(ex, "Settings could not be saved");
            LastSaveError = SaveFailedMessage;
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

    /// <summary>Puts loaded settings in use. True when they need saving again: the PAT was stored in an older form.</summary>
    private bool Apply(UserSettings loaded)
    {
        _settings = loaded;
        var stored = loaded.Tfs.Pat ?? "";
        loaded.Tfs.Pat = DecryptPat(stored, loaded.Tfs.Url);
        if (FixedTfsUrl is not null && !SameServer(loaded.Tfs.Url, FixedTfsUrl))
        {
            // The token belongs to another server, so it must never be sent to this one.
            loaded.Tfs.Url = FixedTfsUrl;
            loaded.Tfs.Pat = "";
            _undecryptablePat = null;
        }

        return !string.IsNullOrEmpty(loaded.Tfs.Pat) && !SecretProtector.IsCurrent(stored);
    }

    /// <summary>
    /// Saves a change that replaces the stored PAT: one read in an older form (plaintext, or encrypted without its
    /// server) saved in the current one, or one Windows sign-in no longer keeps. The backup that save keeps is the
    /// old file, PAT and all, so it is deleted rather than left behind. Returns false when the save failed.
    /// </summary>
    private bool SaveDroppingOldPat()
    {
        if (!Save())
        {
            return false;
        }

        try
        {
            _store.DeleteBackup();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogError(ex, "The settings backup still holding the replaced PAT could not be deleted");
        }

        return true;
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

    private string DecryptPat(string stored, string? url)
    {
        _undecryptablePat = null;
        try
        {
            return SecretProtector.Unprotect(stored, PatContext(url));
        }
        catch (Exception ex) when (ex is CryptographicException or FormatException)
        {
            _logger.LogError(ex,
                "The stored PAT could not be decrypted: it was saved by another Windows account or machine, or for another server. " +
                "It is kept unchanged; enter the PAT again in Settings to replace it.");
            _undecryptablePat = stored;
            return "";
        }
    }

    /// <summary>What the PAT is encrypted for: the server, written the same way however it was typed.</summary>
    private static string PatContext(string? url) =>
        PatContextPrefix + (url ?? "").Trim().TrimEnd('/').ToUpperInvariant();

    private static bool SameServer(string? url, string? other) =>
        string.Equals(PatContext(url), PatContext(other), StringComparison.Ordinal);

    private string PersistedPat() =>
        string.IsNullOrEmpty(_settings.Tfs.Pat) && _undecryptablePat is not null
            ? _undecryptablePat
            : SecretProtector.Protect(_settings.Tfs.Pat, PatContext(_settings.Tfs.Url));

    // The PAT is held in memory as plaintext (the API client needs it) but persisted
    // encrypted. Build a copy with the protected PAT so the in-memory value is untouched.
    private UserSettings BuildPersistableCopy() => new()
    {
        Tfs = new TfsSettings
        {
            Url = _settings.Tfs.Url,
            Auth = _settings.Tfs.Auth,
            Pat = PersistedPat(),
            ApiVersion = _settings.Tfs.ApiVersion
        },
        DefaultAreaPath = _settings.DefaultAreaPath,
        DefaultSprint = _settings.DefaultSprint,
        DefaultSprintMode = _settings.DefaultSprintMode,
        DefaultRepoId = _settings.DefaultRepoId,
        MostUsedRepoCount = _settings.MostUsedRepoCount,
        MostUsedRepoIds = _settings.MostUsedRepoIds,
        TeamName = _settings.TeamName,
        DeliverableTags = _settings.DeliverableTags,
        CapacityPlanning = _settings.CapacityPlanning,
        CodeMergingTargetBranch = _settings.CodeMergingTargetBranch,
        CodeMergingRepoIds = _settings.CodeMergingRepoIds,
        RepoTargetBranches = _settings.RepoTargetBranches,
        RepoBranches = _settings.RepoBranches
    };
}
