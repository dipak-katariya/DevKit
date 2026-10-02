using DevKit.Web.Models;

namespace DevKit.Web.Services;

// The shape of usersettings.json. Written and read only through SettingsService, which
// sanitises every value on the way in and out.

/// <summary>
/// The two branches a repository needs on the merging sheet: the one developers merge into,
/// and the one that code has to reach afterwards.
/// </summary>
public class RepoBranchDefaults
{
    /// <summary>Branch developers merge pull requests into; decides which PRs the sheet shows.</summary>
    public string QaBranch { get; set; } = "";

    /// <summary>Release / migration branch the commits must reach; what verification checks.</summary>
    public string MergingBranch { get; set; } = "";

    public bool IsEmpty => string.IsNullOrWhiteSpace(QaBranch) && string.IsNullOrWhiteSpace(MergingBranch);
}

public class UserSettings
{
    public TfsSettings Tfs { get; set; } = new();
    public string DefaultProjectPath { get; set; } = "";
    public string DefaultAreaPath { get; set; } = "";
    public string DefaultSprint { get; set; } = "";
    public string DefaultRepoId { get; set; } = "";
    public int MostUsedRepoCount { get; set; }
    public List<string> MostUsedRepoIds { get; set; } = new();
    public string TeamName { get; set; } = "";
    public Dictionary<string, string> RepoPaths { get; set; } = new();
    public Dictionary<string, string> BaseBranches { get; set; } = new();
    /// <summary>
    /// Superseded by <see cref="CapacityPlanning"/>'s tag rules. Still persisted so a build from
    /// before them reads its own toggles, and read once as the migration source.
    /// </summary>
    public Dictionary<string, bool> DeliverableTags { get; set; } = new();

    /// <summary>Null on a settings file written before these existed — that is what triggers migration.</summary>
    public CapacityPlanningSettings? CapacityPlanning { get; set; }
    public string CodeMergingTargetBranch { get; set; } = "";
    public List<string> CodeMergingRepoIds { get; set; } = new();
    public Dictionary<string, string> RepoTargetBranches { get; set; } = new();
    public Dictionary<string, RepoBranchDefaults> RepoBranches { get; set; } = new();
}
