using System.Text.Json;
using DevKit.Web.Models;

namespace DevKit.Web.Services;

public class SettingsService
{
    private readonly string _settingsPath;
    private readonly ILogger<SettingsService> _logger;
    private UserSettings _settings = new();

    public SettingsService(ILogger<SettingsService> logger)
    {
        _logger = logger;
        _settingsPath = Path.Combine(AppContext.BaseDirectory, "usersettings.json");
        Load();
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

    public string GetBaseBranch(string repoId) =>
        _settings.BaseBranches.TryGetValue(repoId, out var b) ? b : "";

    public void SetBaseBranch(string repoId, string branch)
    {
        _settings.BaseBranches[repoId] = branch;
        Save();
    }

    /// <summary>
    /// Scans DefaultProjectPath for subdirectories containing .git folders.
    /// Reads the git remote origin URL from each to identify the TFS repo.
    /// </summary>
    public List<LocalRepoInfo> ScanLocalRepos(List<TfsRepo>? tfsRepos = null)
    {
        var result = new List<LocalRepoInfo>();
        var basePath = _settings.DefaultProjectPath;
        if (string.IsNullOrWhiteSpace(basePath) || !Directory.Exists(basePath))
            return result;

        try
        {
            foreach (var dir in Directory.GetDirectories(basePath))
            {
                var gitDir = System.IO.Path.Combine(dir, ".git");
                if (!Directory.Exists(gitDir)) continue;

                var info = new LocalRepoInfo
                {
                    FolderName = System.IO.Path.GetFileName(dir),
                    LocalPath = dir,
                    RemoteUrl = ReadGitRemoteUrl(dir)
                };

                // Try to match to a TFS repo by remote URL or name
                if (tfsRepos != null)
                    MatchToTfsRepo(info, tfsRepos);

                result.Add(info);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to scan local repos in {Path}", basePath);
        }
        return result;
    }

    /// <summary>
    /// Reads the git remote origin URL from a local repo.
    /// First tries git config file, falls back to running git command.
    /// </summary>
    private string ReadGitRemoteUrl(string repoPath)
    {
        try
        {
            // Read from .git/config directly (no process needed)
            var configPath = System.IO.Path.Combine(repoPath, ".git", "config");
            if (File.Exists(configPath))
            {
                var lines = File.ReadAllLines(configPath);
                bool inOrigin = false;
                foreach (var line in lines)
                {
                    var trimmed = line.Trim();
                    if (trimmed == "[remote \"origin\"]")
                    {
                        inOrigin = true;
                        continue;
                    }
                    if (inOrigin && trimmed.StartsWith("["))
                        break;
                    if (inOrigin && trimmed.StartsWith("url = ", StringComparison.OrdinalIgnoreCase))
                        return trimmed["url = ".Length..].Trim();
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not read git remote URL from {RepoPath}", repoPath);
        }
        return "";
    }

    /// <summary>
    /// Matches a local repo to a TFS repo by comparing remote URLs, then by repo name extracted from URL.
    /// Handles clones like FX-CPFOIA-Migration_2 which have the same remote as FX-CPFOIA-Migration.
    /// </summary>
    private void MatchToTfsRepo(LocalRepoInfo local, List<TfsRepo> tfsRepos)
    {
        // Strategy 1: Exact remote URL match
        if (!string.IsNullOrEmpty(local.RemoteUrl))
        {
            var normalizedLocal = NormalizeUrl(local.RemoteUrl);
            var match = tfsRepos.FirstOrDefault(r => NormalizeUrl(r.RemoteUrl) == normalizedLocal);
            if (match != null)
            {
                local.TfsRepoId = match.Id;
                local.TfsRepoName = match.Name;
                local.TfsProject = match.Project;
                return;
            }

            // Strategy 2: Extract repo name from remote URL and match
            var repoNameFromUrl = ExtractRepoNameFromUrl(local.RemoteUrl);
            if (!string.IsNullOrEmpty(repoNameFromUrl))
            {
                match = tfsRepos.FirstOrDefault(r =>
                    r.Name.Equals(repoNameFromUrl, StringComparison.OrdinalIgnoreCase));
                if (match != null)
                {
                    local.TfsRepoId = match.Id;
                    local.TfsRepoName = match.Name;
                    local.TfsProject = match.Project;
                    return;
                }
            }
        }

        // Strategy 3: Folder name exact match
        var nameMatch = tfsRepos.FirstOrDefault(r =>
            r.Name.Equals(local.FolderName, StringComparison.OrdinalIgnoreCase));
        if (nameMatch != null)
        {
            local.TfsRepoId = nameMatch.Id;
            local.TfsRepoName = nameMatch.Name;
            local.TfsProject = nameMatch.Project;
        }
    }

    private static string NormalizeUrl(string url)
    {
        if (string.IsNullOrEmpty(url)) return "";
        return url.TrimEnd('/').ToLowerInvariant()
            .Replace("http://", "").Replace("https://", "");
    }

    private static string ExtractRepoNameFromUrl(string url)
    {
        if (string.IsNullOrEmpty(url)) return "";
        // TFS/Azure DevOps: https://tfs.example.com/tfs/Collection/Project/_git/RepoName
        var gitIdx = url.LastIndexOf("/_git/", StringComparison.OrdinalIgnoreCase);
        if (gitIdx >= 0)
            return url[(gitIdx + 6)..].TrimEnd('/');
        // Generic: last segment of URL
        var lastSlash = url.TrimEnd('/').LastIndexOf('/');
        if (lastSlash >= 0)
        {
            var name = url[(lastSlash + 1)..].TrimEnd('/');
            if (name.EndsWith(".git", StringComparison.OrdinalIgnoreCase))
                name = name[..^4];
            return name;
        }
        return "";
    }

    /// <summary>
    /// Auto-maps all scanned local repos to TFS repos and saves paths.
    /// </summary>
    public int AutoMapLocalRepos(List<TfsRepo> tfsRepos)
    {
        var localRepos = ScanLocalRepos(tfsRepos);
        int mapped = 0;
        foreach (var local in localRepos.Where(l => l.IsMapped))
        {
            _settings.RepoPaths[local.TfsRepoId!] = local.LocalPath;
            mapped++;
        }
        if (mapped > 0) Save();
        return mapped;
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
        TeamName = _settings.TeamName,
        RepoPaths = _settings.RepoPaths,
        BaseBranches = _settings.BaseBranches
    };
}

public class UserSettings
{
    public TfsSettings Tfs { get; set; } = new();
    public string DefaultProjectPath { get; set; } = "";
    public string DefaultAreaPath { get; set; } = "";
    public string DefaultSprint { get; set; } = "";
    public string DefaultRepoId { get; set; } = "";
    public string TeamName { get; set; } = "";
    public Dictionary<string, string> RepoPaths { get; set; } = new();
    public Dictionary<string, string> BaseBranches { get; set; } = new();
}
