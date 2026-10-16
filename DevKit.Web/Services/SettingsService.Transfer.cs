using System.Text.Json;
using DevKit.Web.Models;

namespace DevKit.Web.Services;

/// <summary>What an import did: whether the settings were taken, and what to tell the person.</summary>
public sealed record SettingsImportResult(bool Imported, string Message);

/// <summary>
/// The settings file people download and load again: every preference, and never the TFS connection. The server
/// address, how DevKit signs in and the personal access token stay out of it, so a shared or mislaid file gives
/// nobody access to TFS, and loading one never changes the connection in use.
/// </summary>
public sealed class PortableSettings
{
    public const string FormatName = "DevKit settings";
    public const int CurrentVersion = 1;

    public string Format { get; set; } = "";
    public int Version { get; set; }
    public DateTimeOffset ExportedAt { get; set; }
    public string TeamName { get; set; } = "";
    public string DefaultAreaPath { get; set; } = "";
    public string DefaultSprint { get; set; } = "";
    public DefaultSprintMode? DefaultSprintMode { get; set; }
    public string DefaultRepoId { get; set; } = "";
    public int MostUsedRepoCount { get; set; }
    public List<string> MostUsedRepoIds { get; set; } = new();
    public CapacityPlanningSettings? CapacityPlanning { get; set; }
    public List<string> CodeMergingRepoIds { get; set; } = new();
    public Dictionary<string, RepoBranchDefaults> RepoBranches { get; set; } = new();
}

/// <summary>Exporting the settings to a file and importing them from one (<see cref="PortableSettings"/>).</summary>
public partial class SettingsService
{
    /// <summary>The largest settings file DevKit reads; a real one is a few kilobytes.</summary>
    public const int MaxImportBytes = 256 * 1024;

    // A settings file never names more repositories than a TFS collection has; this keeps a crafted one small.
    private const int MaxImportedRepositories = 2000;

    private const string NotSettingsFileMessage = "This file is not a DevKit settings file.";

    /// <summary>The settings as a file to download, without the TFS connection.</summary>
    public string ExportSettings()
    {
        lock (_sync)
        {
            var portable = new PortableSettings
            {
                Format = PortableSettings.FormatName,
                Version = PortableSettings.CurrentVersion,
                ExportedAt = DateTimeOffset.UtcNow,
                TeamName = _settings.TeamName,
                DefaultAreaPath = _settings.DefaultAreaPath,
                DefaultSprint = _settings.DefaultSprint,
                DefaultSprintMode = _settings.DefaultSprintMode,
                DefaultRepoId = _settings.DefaultRepoId,
                MostUsedRepoCount = MostUsedRepoCount,
                MostUsedRepoIds = _settings.MostUsedRepoIds.ToList(),
                CapacityPlanning = _settings.CapacityPlanning?.Normalized(),
                CodeMergingRepoIds = _settings.CodeMergingRepoIds.ToList(),
                RepoBranches = CleanBranches(_settings.RepoBranches)
            };
            _logger.LogInformation("Settings exported: {Repositories} repositories with branch defaults", portable.RepoBranches.Count);
            return JsonSerializer.Serialize(portable, FileFormat);
        }
    }

    /// <summary>
    /// Replaces the preferences with those in <paramref name="text"/>, a file made by <see cref="ExportSettings"/>, and
    /// saves them. The TFS connection is never read from the file nor changed. A file that is not a DevKit settings
    /// file, or comes from a newer DevKit, changes nothing.
    /// </summary>
    public SettingsImportResult ImportSettings(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (text.Length > MaxImportBytes)
        {
            return new SettingsImportResult(false, "This file is too large to be a DevKit settings file.");
        }

        PortableSettings? portable;
        try
        {
            portable = JsonSerializer.Deserialize<PortableSettings>(text);
        }
        catch (JsonException ex)
        {
            _logger.LogError(ex, "An imported settings file could not be read as DevKit settings");
            return new SettingsImportResult(false, NotSettingsFileMessage);
        }

        if (portable is null || !string.Equals(portable.Format, PortableSettings.FormatName, StringComparison.Ordinal))
        {
            return new SettingsImportResult(false, NotSettingsFileMessage);
        }

        if (portable.Version > PortableSettings.CurrentVersion)
        {
            return new SettingsImportResult(false, "This file was made by a newer DevKit. Update DevKit, then import it again.");
        }

        return Apply(portable);
    }

    private SettingsImportResult Apply(PortableSettings portable)
    {
        lock (_sync)
        {
            _settings.TeamName = (portable.TeamName ?? "").Trim();
            _settings.DefaultAreaPath = portable.DefaultAreaPath ?? "";
            _settings.DefaultSprint = portable.DefaultSprint ?? "";
            _settings.DefaultSprintMode = portable.DefaultSprintMode;
            _settings.DefaultRepoId = (portable.DefaultRepoId ?? "").Trim();
            _settings.MostUsedRepoCount = Math.Clamp(portable.MostUsedRepoCount, 0, MaxMostUsedRepoCount);
            _settings.MostUsedRepoIds = DistinctIds(portable.MostUsedRepoIds, MostUsedRepoCount);
            _settings.CapacityPlanning = portable.CapacityPlanning?.Normalized() ?? _settings.CapacityPlanning;
            _settings.CodeMergingRepoIds = DistinctIds(portable.CodeMergingRepoIds, MaxImportedRepositories);
            _settings.RepoBranches = CleanBranches(portable.RepoBranches);
            if (!Save())
            {
                return new SettingsImportResult(false, LastSaveError ?? SaveFailedMessage);
            }

            _logger.LogInformation("Settings imported: {Repositories} repositories with branch defaults", _settings.RepoBranches.Count);
            return new SettingsImportResult(true,
                $"Settings imported, including branch defaults for {_settings.RepoBranches.Count} repositories. Your TFS connection was not changed.");
        }
    }

    /// <summary>Branch defaults with names trimmed and empty entries dropped, as Settings itself keeps them.</summary>
    private static Dictionary<string, RepoBranchDefaults> CleanBranches(Dictionary<string, RepoBranchDefaults>? branches) =>
        (branches ?? new())
            .Where(pair => !string.IsNullOrWhiteSpace(pair.Key) && pair.Value is not null)
            .Take(MaxImportedRepositories)
            .Select(pair => (Id: pair.Key.Trim(), Branches: new RepoBranchDefaults
            {
                QaBranch = (pair.Value.QaBranch ?? "").Trim(),
                MergingBranch = (pair.Value.MergingBranch ?? "").Trim()
            }))
            .Where(entry => !entry.Branches.IsEmpty)
            .GroupBy(entry => entry.Id, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First().Branches, StringComparer.OrdinalIgnoreCase);
}
