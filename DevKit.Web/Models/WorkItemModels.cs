namespace DevKit.Web.Models;

public class TfsProject
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
}

public class TfsRepo
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Project { get; set; } = "";
    public string RemoteUrl { get; set; } = "";
}

public class TfsArea
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Path { get; set; } = "";
    public string Project { get; set; } = "";
}

public class TfsIteration
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Path { get; set; } = "";
    public string Project { get; set; } = "";

    /// <summary>
    /// Sprint dates as configured in TFS, when the iteration has them. Used to scope how far
    /// back a branch's history needs reading; null for iterations nobody dated.
    /// </summary>
    public DateTime? StartDate { get; set; }

    public DateTime? FinishDate { get; set; }
}

public class WorkItem
{
    public string Id { get; set; } = "";
    public string Title { get; set; } = "";
    public string State { get; set; } = "";
    public string Assigned { get; set; } = "-";
    public string Area { get; set; } = "";
    public string Sprint { get; set; } = "";
    public string IterationPath { get; set; } = "";
    public string Type { get; set; } = "Requirement";
    public string Project { get; set; } = "";
    public double? Effort { get; set; }
    public double? OriginalEstimate { get; set; }
    public double? RemainingWork { get; set; }
    public double? CompletedWork { get; set; }

    /// <summary>Microsoft.VSTS.Common.Discipline (e.g. "Development", "Test").</summary>
    public string Discipline { get; set; } = "";

    /// <summary>System.Tags, raw semicolon-separated string.</summary>
    public string Tags { get; set; } = "";

    /// <summary>The instance-specific Revised Estimate field value (distinct from Completed+Remaining), when present.</summary>
    public double? RevisedEstimateField { get; set; }

    // ─── Audit fields (Insight Hub Tasks/Bugs tabs) ───
    public string TaskExecutionType { get; set; } = "";
    public string Reason { get; set; } = "";
    public string RootCause { get; set; } = "";
    public string ProposedFix { get; set; } = "";
    public string Severity { get; set; } = "";
    public string CreatedBy { get; set; } = "";
    public string ParentId { get; set; } = "";
    public List<string> ChildIds { get; set; } = new();
    public List<string> DuplicateLinkIds { get; set; } = new();

    /// <summary>Revised Estimate = Completed Work + Remaining Work (the updated total).</summary>
    public double? RevisedEstimate =>
        (CompletedWork ?? 0) + (RemainingWork ?? 0) > 0
            ? (CompletedWork ?? 0) + (RemainingWork ?? 0)
            : null;

    /// <summary>Estimate to use for planning: revised-if-set-and-positive, else original. (Business rule 1.3)</summary>
    public double EffectiveEstimate =>
        RevisedEstimateField is > 0 ? RevisedEstimateField!.Value : (OriginalEstimate ?? 0);

    /// <summary>Case-insensitive check whether System.Tags contains the given tag.</summary>
    public bool HasTag(string tag) =>
        !string.IsNullOrEmpty(Tags)
        && Tags.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
               .Any(t => string.Equals(t, tag, StringComparison.OrdinalIgnoreCase));
}

public class PlanningItem
{
    public WorkItem WorkItem { get; set; } = new();
    public List<WorkItem> Tasks { get; set; } = new();

    /// <summary>WIQL iteration path of the sprint currently being planned. Used to split tasks into past vs. current sprint.</summary>
    public string CurrentSprintPath { get; set; } = "";

    /// <summary>Whether this requirement's task rows are expanded in the grid. Collapsed by default.</summary>
    public bool IsExpanded { get; set; }

    public double TaskTotalEstimate => Tasks.Sum(t => t.OriginalEstimate ?? 0);
    public double TaskTotalRemaining => Tasks.Sum(t => t.RemainingWork ?? 0);
    public double TaskTotalCompleted => Tasks.Sum(t => t.CompletedWork ?? 0);
    public double TaskTotalRevised => Tasks.Sum(t => (t.CompletedWork ?? 0) + (t.RemainingWork ?? 0));
    public double SizeMismatch => (WorkItem.Effort ?? 0) - TaskTotalEstimate;

    /// <summary>True when a task belongs to the sprint currently being planned.</summary>
    public bool IsCurrentSprintTask(WorkItem t) =>
        !string.IsNullOrEmpty(CurrentSprintPath)
        && string.Equals(t.IterationPath.Trim(), CurrentSprintPath.Trim(), StringComparison.OrdinalIgnoreCase);

    /// <summary>Sum of Original Estimate for tasks in the sprint being planned.</summary>
    public double CurrentSprintOriginal => Tasks.Where(IsCurrentSprintTask).Sum(t => t.OriginalEstimate ?? 0);

    /// <summary>Sum of Completed Work for tasks from previous sprints.</summary>
    public double PastSprintCompleted => Tasks.Where(t => !IsCurrentSprintTask(t)).Sum(t => t.CompletedWork ?? 0);

    /// <summary>
    /// Suggested requirement size = completed work carried from past sprints + original estimate planned this sprint.
    /// </summary>
    public double CalculatedSize => PastSprintCompleted + CurrentSprintOriginal;

    /// <summary>True when the calculated size differs from the requirement's current size.</summary>
    public bool CalcDiffersFromCurrent =>
        CalculatedSize > 0 && Math.Abs(CalculatedSize - (WorkItem.Effort ?? 0)) > 0.01;

    public string? EditingSize { get; set; }
    public bool Saving { get; set; }
}

public class TfsRef
{
    public string Name { get; set; } = "";
    public string ObjectId { get; set; } = "";

    /// <summary>Display name of whoever created the branch; empty when TFS does not say.</summary>
    public string Creator { get; set; } = "";
}

/// <summary>
/// A branch a work item is linked to, read back from its Git ref artifact link. The repository
/// is identified by id because repository names are not unique across projects.
/// </summary>
public class WorkItemBranchLink
{
    public string ProjectId { get; set; } = "";
    public string RepositoryId { get; set; } = "";
    public string BranchName { get; set; } = "";

    /// <summary>Resolved from the repository list for display; empty when the repo is unknown here.</summary>
    public string RepositoryName { get; set; } = "";

    /// <summary>
    /// Set when the branch could actually be checked and is no longer in its repository. TFS keeps
    /// a link after the branch is deleted, so a link alone is no proof the branch exists. False
    /// also covers "not checked" — a repository whose branches have not been loaded is not judged.
    /// </summary>
    public bool BranchMissing { get; set; }

    public bool BelongsTo(string? repoId) =>
        !string.IsNullOrEmpty(repoId) && string.Equals(RepositoryId, repoId, StringComparison.OrdinalIgnoreCase);

    /// <summary>True when this link already points at the given branch in the given repository.</summary>
    public bool Matches(string? repoId, string? branchName) =>
        BelongsTo(repoId)
        && !string.IsNullOrWhiteSpace(branchName)
        && string.Equals(BranchName, branchName.Trim(), StringComparison.OrdinalIgnoreCase);
}

public enum WorkItemLinkOutcome
{
    /// <summary>The relation was added by this call.</summary>
    Linked,

    /// <summary>The work item already pointed at this branch, so nothing needed adding.</summary>
    AlreadyLinked,

    /// <summary>The relation was removed by this call.</summary>
    Unlinked,

    /// <summary>There was no such relation to remove.</summary>
    NotLinked,

    /// <summary>The change could not be made; <see cref="WorkItemLinkResult.Detail"/> says why.</summary>
    Failed
}

/// <summary>Outcome of adding or removing a work item's link to a branch.</summary>
public readonly record struct WorkItemLinkResult(WorkItemLinkOutcome Outcome, string Detail)
{
    /// <summary>True when the work item ends up pointing at the branch, whoever put the link there.</summary>
    public bool IsLinked => Outcome is WorkItemLinkOutcome.Linked or WorkItemLinkOutcome.AlreadyLinked;

    /// <summary>True when the work item ends up with no such link, whether or not this call removed it.</summary>
    public bool IsUnlinked => Outcome is WorkItemLinkOutcome.Unlinked or WorkItemLinkOutcome.NotLinked;

    public static WorkItemLinkResult Linked() => new(WorkItemLinkOutcome.Linked, "");
    public static WorkItemLinkResult AlreadyLinked() => new(WorkItemLinkOutcome.AlreadyLinked, "");
    public static WorkItemLinkResult Unlinked() => new(WorkItemLinkOutcome.Unlinked, "");
    public static WorkItemLinkResult NotLinked() => new(WorkItemLinkOutcome.NotLinked, "");
    public static WorkItemLinkResult Failed(string detail) => new(WorkItemLinkOutcome.Failed, detail);
}

public class ClassificationNode
{
    public string? Id { get; set; }
    public string? Name { get; set; }
    public string? Path { get; set; }
    public List<ClassificationNode>? Children { get; set; }
}
