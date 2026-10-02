using System.Text.Json;
using DevKit.Web.Models;

namespace DevKit.Web.Services;

/// <summary>
/// Reads and writes usersettings.json, with the PAT encrypted at rest. Repository settings and the
/// local-clone scan live in SettingsService.Repos.cs; the stored shape is <see cref="UserSettings"/>.
/// </summary>
public partial class SettingsService
{
    private const string SettingsFileName = "usersettings.json";
    private const string TargetFolderPrefix = "net";

    private readonly string _settingsPath;
    private readonly ILogger<SettingsService> _logger;
    private UserSettings _settings = new();

    public SettingsService(ILogger<SettingsService> logger)
    {
        _logger = logger;
        _settingsPath = Path.Combine(AppContext.BaseDirectory, SettingsFileName);
        AdoptFromPreviousTarget(_settingsPath, _logger);
        Load();
    }

    /// <summary>
    /// A source build keeps its files in bin/&lt;Configuration&gt;/&lt;target framework&gt;/, so moving to a
    /// newer target framework moves the binary away from the settings it wrote. When the settings file
    /// is missing from such a folder, it is copied from the newest older target framework folder beside
    /// it — bin/Debug/net9.0 for bin/Debug/net10.0 — instead of the app opening unconfigured. The old
    /// file is left in place. Returns the file adopted from, or null when nothing was.
    /// </summary>
    public static string? AdoptFromPreviousTarget(string settingsPath, ILogger logger)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(settingsPath);
        ArgumentNullException.ThrowIfNull(logger);
        if (File.Exists(settingsPath))
        {
            return null;
        }

        try
        {
            var previous = FindPreviousTargetSettings(Path.GetFullPath(settingsPath));
            if (previous is null)
            {
                return null;
            }

            File.Copy(previous, settingsPath, overwrite: false);
            logger.LogInformation("Settings carried over from {Folder} after a target framework upgrade",
                Path.GetFileName(Path.GetDirectoryName(previous)));
            return previous;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogError(ex, "Settings from an earlier target framework could not be carried over; starting unconfigured");
            return null;
        }
    }

    private static string? FindPreviousTargetSettings(string settingsPath)
    {
        var outputDir = Path.GetDirectoryName(settingsPath);
        var configurationDir = Path.GetDirectoryName(outputDir);
        if (outputDir is null || configurationDir is null || TargetVersion(Path.GetFileName(outputDir)) is not { } current)
        {
            return null;
        }

        return Directory.EnumerateDirectories(configurationDir)
            .Select(dir => (Dir: dir, Version: TargetVersion(Path.GetFileName(dir))))
            .Where(candidate => candidate.Version is not null && candidate.Version < current)
            .OrderByDescending(candidate => candidate.Version)
            .Select(candidate => Path.Combine(candidate.Dir, Path.GetFileName(settingsPath)))
            .FirstOrDefault(file => IsConfinedRegularFile(file, configurationDir));
    }

    /// <summary>The version a build output folder is named for — net10.0 is 10.0 — or null for any other folder.</summary>
    private static Version? TargetVersion(string folderName) =>
        folderName.StartsWith(TargetFolderPrefix, StringComparison.OrdinalIgnoreCase)
        && Version.TryParse(folderName[TargetFolderPrefix.Length..], out var version)
            ? version
            : null;

    /// <summary>Exists inside <paramref name="root"/>, and neither the file nor its folder is a link.</summary>
    private static bool IsConfinedRegularFile(string path, string root)
    {
        var full = Path.GetFullPath(path);
        var rootWithSeparator = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)) + Path.DirectorySeparatorChar;
        if (!full.StartsWith(rootWithSeparator, StringComparison.Ordinal) || !File.Exists(full))
        {
            return false;
        }

        var file = new FileInfo(full);
        return !file.Attributes.HasFlag(FileAttributes.ReparsePoint)
               && file.Directory is { } folder
               && !folder.Attributes.HasFlag(FileAttributes.ReparsePoint);
    }

    public TfsSettings Tfs => _settings.Tfs;
    public Dictionary<string, string> RepoPaths => _settings.RepoPaths;
    public Dictionary<string, string> BaseBranches => _settings.BaseBranches;
    public string DefaultProjectPath
    {
        get => _settings.DefaultProjectPath;
        set { _settings.DefaultProjectPath = value; Save(); }
    }
    public string DefaultAreaPath
    {
        get => _settings.DefaultAreaPath;
        set { _settings.DefaultAreaPath = value; Save(); }
    }
    public string DefaultSprint
    {
        get => _settings.DefaultSprint;
        set { _settings.DefaultSprint = value; Save(); }
    }
    public string DefaultRepoId
    {
        get => _settings.DefaultRepoId;
        set { _settings.DefaultRepoId = value; Save(); }
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
        set
        {
            var clamped = Math.Clamp(value, 0, MaxMostUsedRepoCount);
            _settings.MostUsedRepoCount = clamped;
            TrimMostUsedRepos(clamped);
            Save();
        }
    }

    /// <summary>
    /// Pinned repository ids, in the order the user picked them — that order is the display order.
    /// Ids, not names: names are not unique across projects.
    /// </summary>
    public IReadOnlyList<string> MostUsedRepoIds => _settings.MostUsedRepoIds;

    public void SetMostUsedRepoIds(IEnumerable<string>? repoIds)
    {
        var deduped = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var cap = MostUsedRepoCount;
        foreach (var id in repoIds ?? Enumerable.Empty<string>())
        {
            // Checked before adding, not after: the other way round a cap of zero still kept one.
            if (deduped.Count >= cap) break;
            if (string.IsNullOrWhiteSpace(id)) continue;

            var trimmed = id.Trim();
            if (seen.Add(trimmed)) deduped.Add(trimmed);
        }

        _settings.MostUsedRepoIds = deduped;
        Save();
    }

    private void TrimMostUsedRepos(int count)
    {
        if (_settings.MostUsedRepoIds.Count > count)
            _settings.MostUsedRepoIds = _settings.MostUsedRepoIds.Take(count).ToList();
    }
    public string TeamName
    {
        get => _settings.TeamName;
        set { _settings.TeamName = value; Save(); }
    }
    public bool IsConfigured => Tfs.IsConfigured;

    public string GetRepoPath(string repoId) =>
        _settings.RepoPaths.TryGetValue(repoId, out var p) ? p : "";

    public void SetRepoPath(string repoId, string path)
    {
        _settings.RepoPaths[repoId] = path;
        Save();
    }

    // ═══ CAPACITY PLANNING ═══

    private readonly object _capacityPlanningSync = new();

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
            lock (_capacityPlanningSync)
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
        lock (_capacityPlanningSync)
        {
            _settings.CapacityPlanning = settings.Normalized();
            Save();
        }
    }

    public void SaveTfs(string url, string pat)
    {
        _settings.Tfs.Url = url;
        _settings.Tfs.Pat = pat;
        Save();
    }

    public void Save()
    {
        try
        {
            var json = JsonSerializer.Serialize(BuildPersistableCopy(), new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(_settingsPath, json);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to save settings");
        }
    }

    private void Load()
    {
        try
        {
            if (!File.Exists(_settingsPath)) return;

            var json = File.ReadAllText(_settingsPath);
            _settings = JsonSerializer.Deserialize<UserSettings>(json) ?? new UserSettings();

            var storedPat = _settings.Tfs.Pat;
            _settings.Tfs.Pat = DecryptPat(storedPat);

            // Migrate a legacy plaintext PAT to encrypted-at-rest on first load after upgrade.
            if (!string.IsNullOrEmpty(_settings.Tfs.Pat) && !SecretProtector.IsProtected(storedPat))
                Save();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to load settings, using defaults");
            _settings = new UserSettings();
        }
    }

    private string DecryptPat(string stored)
    {
        try
        {
            return SecretProtector.Unprotect(stored);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Stored PAT could not be decrypted (copied from another user or machine?). Re-enter it in Settings.");
            return "";
        }
    }

    // The PAT is held in memory as plaintext (the API client needs it) but persisted
    // encrypted. Build a copy with the protected PAT so the in-memory value is untouched.
    private UserSettings BuildPersistableCopy() => new()
    {
        Tfs = new TfsSettings
        {
            Url = _settings.Tfs.Url,
            Pat = SecretProtector.Protect(_settings.Tfs.Pat),
            ApiVersion = _settings.Tfs.ApiVersion
        },
        DefaultProjectPath = _settings.DefaultProjectPath,
        DefaultAreaPath = _settings.DefaultAreaPath,
        DefaultSprint = _settings.DefaultSprint,
        DefaultRepoId = _settings.DefaultRepoId,
        MostUsedRepoCount = _settings.MostUsedRepoCount,
        MostUsedRepoIds = _settings.MostUsedRepoIds,
        TeamName = _settings.TeamName,
        RepoPaths = _settings.RepoPaths,
        BaseBranches = _settings.BaseBranches,
        DeliverableTags = _settings.DeliverableTags,
        CapacityPlanning = _settings.CapacityPlanning,
        CodeMergingTargetBranch = _settings.CodeMergingTargetBranch,
        CodeMergingRepoIds = _settings.CodeMergingRepoIds,
        RepoTargetBranches = _settings.RepoTargetBranches,
        RepoBranches = _settings.RepoBranches
    };
}
