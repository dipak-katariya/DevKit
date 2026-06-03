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

    /// <summary>Revised Estimate = Completed Work + Remaining Work (the updated total).</summary>
    public double? RevisedEstimate =>
        (CompletedWork ?? 0) + (RemainingWork ?? 0) > 0
            ? (CompletedWork ?? 0) + (RemainingWork ?? 0)
            : null;
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
}

public class ClassificationNode
{
    public string? Id { get; set; }
    public string? Name { get; set; }
    public string? Path { get; set; }
    public List<ClassificationNode>? Children { get; set; }
}
