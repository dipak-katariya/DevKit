using DevKit.Web.Services;

namespace DevKit.Web.Models;

/// <summary>What Branch Delete loads: the repositories always; the area and sprints when the cleanup is sprint-driven.</summary>
public sealed class BranchCleanupQuery
{
    public IReadOnlyList<TfsRepo> Repos { get; init; } = Array.Empty<TfsRepo>();

    public TfsArea? Area { get; init; }

    /// <summary>Iteration paths of the sprints being cleaned up; empty when no sprint is chosen.</summary>
    public IReadOnlyList<string> SprintPaths { get; init; } = Array.Empty<string>();

    /// <summary>The team name from Settings. Empty turns unattached detection off, since there is no prefix to match.</summary>
    public string TeamName { get; init; } = "";

    /// <summary>
    /// Branches Settings configures per repository id — the QA and merging branches — which are
    /// never offered for deletion whatever they are called.
    /// </summary>
    public IReadOnlyDictionary<string, IReadOnlyList<string>> ConfiguredBranchesByRepoId { get; init; } =
        new Dictionary<string, IReadOnlyList<string>>();
}

/// <summary>A work item and the branches its artifact links point at.</summary>
public sealed record WorkItemBranches(WorkItem Item, IReadOnlyList<WorkItemBranchLink> Links);

/// <summary>A work item that links a branch, reduced to what the grid shows.</summary>
public sealed record LinkedWorkItem(string Id, string Title, string Type, string State, string Sprint, string Project);

public enum BranchLinkState
{
    /// <summary>At least one work item links the branch.</summary>
    Linked,

    /// <summary>Carries the team prefix, yet no work item that was checked links it.</summary>
    Unattached,

    /// <summary>Not a team branch, so its links were not looked up.</summary>
    NotChecked
}

/// <summary>One branch in one repository, with everything the Delete decision rests on.</summary>
public sealed class BranchCleanupRow
{
    public required TfsRepo Repo { get; init; }
    public required string Branch { get; init; }
    public required string ObjectId { get; init; }
    public string Creator { get; init; } = "";

    /// <summary>The name read back through the team formula; null when it does not follow it.</summary>
    public BranchNameParts? Parsed { get; init; }

    /// <summary>The team prefix is one of the branch's words.</summary>
    public bool MentionsTeam { get; init; }

    public List<LinkedWorkItem> LinkedBy { get; } = new();

    /// <summary>Linked from a work item in a selected sprint, or named after a selected sprint.</summary>
    public bool InSelectedSprint { get; set; }

    /// <summary>Why the branch must be kept; null when it may be deleted.</summary>
    public string? ProtectedReason { get; set; }

    public bool CanDelete => ProtectedReason is null;

    public BranchLinkState LinkState =>
        LinkedBy.Count > 0 ? BranchLinkState.Linked
        : MentionsTeam ? BranchLinkState.Unattached
        : BranchLinkState.NotChecked;

    /// <summary>Identifies the row across repositories, which can hold branches of the same name.</summary>
    public string Key => KeyOf(Repo.Id, Branch);

    public static string KeyOf(string repoId, string branch) =>
        repoId.ToLowerInvariant() + "\u0000" + branch.ToLowerInvariant();
}

public sealed class BranchCleanupResult
{
    public List<BranchCleanupRow> Rows { get; } = new();

    /// <summary>Per-repository problems — a listing that failed or was cut short — shown above the grid.</summary>
    public List<string> Warnings { get; } = new();

    public int SprintWorkItemCount { get; set; }

    /// <summary>Work items that were read to decide which branches are linked.</summary>
    public int CheckedWorkItemCount { get; set; }
}

/// <summary>How far a load or delete has got, for the progress bar.</summary>
public readonly record struct BranchCleanupProgress(string Stage, int Done, int Total)
{
    public int Percent => Total <= 0 ? 0 : (int)Math.Round(100.0 * Math.Min(Done, Total) / Total);
}

/// <summary>What TFS answered for one branch in a delete batch.</summary>
public sealed record BranchDeleteOutcome(string RepoId, string RepoName, string Branch, bool Deleted, string Message);
