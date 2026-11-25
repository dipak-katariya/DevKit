using System.Text.Json;
using System.Text.Json.Serialization;
using DevKit.Web.Models;

namespace DevKit.Web.Services;

/// <summary>How the default sprint is chosen. Stored by name, so the settings file reads as what was chosen.</summary>
[JsonConverter(typeof(TolerantEnumConverter<DefaultSprintMode>))]
public enum DefaultSprintMode
{
    /// <summary>The sprint running today, by the sprint dates in TFS.</summary>
    Current,

    /// <summary>The one sprint picked in Settings.</summary>
    Chosen
}

/// <summary>How the Dashboard lays out the tools. Stored by name; a file without it means Grid.</summary>
[JsonConverter(typeof(TolerantEnumConverter<DashboardView>))]
public enum DashboardView
{
    Grid,
    List
}

// The shape of usersettings.json. Written and read only through SettingsService, which
// sanitises every value on the way in and out. Each part keeps the fields this build does not
// know (UnknownFields) and writes them back, so a save by this package, after going back to it
// from a later one, never drops a setting the later one added. Packages made before this do
// drop them, so "Run Latest DevKit.bat" asks before going back to one of those.

/// <summary>
/// The two branches a repository needs on the merging sheet: the one developers merge into,
/// and the one that code has to reach afterwards.
/// </summary>
public class RepoBranchDefaults
{
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? UnknownFields { get; set; }

    /// <summary>Branch developers merge pull requests into; decides which PRs the sheet shows.</summary>
    public string QaBranch { get; set; } = "";

    /// <summary>Release / migration branch the commits must reach; what verification checks.</summary>
    public string MergingBranch { get; set; } = "";

    public bool IsEmpty => string.IsNullOrWhiteSpace(QaBranch) && string.IsNullOrWhiteSpace(MergingBranch);
}

public class UserSettings
{
    public TfsSettings Tfs { get; set; } = new();
    public string DefaultAreaPath { get; set; } = "";
    public string DefaultSprint { get; set; } = "";

    /// <summary>Null in a settings file from before the choice existed; see <see cref="SettingsService.DefaultSprintIsCurrent"/>.</summary>
    public DefaultSprintMode? DefaultSprintMode { get; set; }

    public string DefaultRepoId { get; set; } = "";
    public int MostUsedRepoCount { get; set; }
    public List<string> MostUsedRepoIds { get; set; } = new();
    public string TeamName { get; set; } = "";

    /// <summary>
    /// Superseded by <see cref="CapacityPlanning"/>'s tag rules. Still persisted so a build from
    /// before them reads its own toggles, and read once as the migration source.
    /// </summary>
    public Dictionary<string, bool> DeliverableTags { get; set; } = new();

    /// <summary>Null on a settings file written before these existed. That is what triggers migration.</summary>
    public CapacityPlanningSettings? CapacityPlanning { get; set; }
    public string CodeMergingTargetBranch { get; set; } = "";
    public List<string> CodeMergingRepoIds { get; set; } = new();
    public Dictionary<string, string> RepoTargetBranches { get; set; } = new();
    public Dictionary<string, RepoBranchDefaults> RepoBranches { get; set; } = new();
    public DashboardView DashboardView { get; set; }

    /// <summary>False until the first-visit tour has opened for the person; a file without it shows the tour once.</summary>
    public bool GuideTourSeen { get; set; }

    /// <summary>What the Code Merging Sheet's Update status button does; off until the person sets it up.</summary>
    public StatusUpdateSettings StatusUpdate { get; set; } = new();

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? UnknownFields { get; set; }
}
