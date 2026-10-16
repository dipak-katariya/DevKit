using DevKit.Web.Models;

namespace DevKit.Web.Services;

/// <summary>
/// Scoped service that preserves page state across tab navigation within the same Blazor circuit.
/// </summary>
public class PageStateService
{
    public PageStateService(SharedTfsData tfsData)
    {
        TfsData = tfsData;
    }

    // ═══ BRANCH CREATOR STATE ═══
    public BranchCreatorState BranchCreator { get; } = new();

    /// <summary>The Sprint Insights Task Creation tab, which lives in its own component.</summary>
    public TaskCreationState TaskCreation { get; } = new();

    /// <summary>The circuit's TFS projects, repositories, areas and sprints, loaded once for every page.</summary>
    public SharedTfsData TfsData { get; }
}

public class BranchCreatorState
{
    public bool HasState { get; set; }

    // Selections
    public TfsArea? SelectedArea { get; set; }
    public string? SelectedSprint { get; set; }
    public TfsRepo? SelectedRepo { get; set; }
    public WorkItem? SelectedItem { get; set; }

    // Work items
    public List<WorkItem> WorkItems { get; set; } = new();
    public List<WorkItem> Filtered { get; set; } = new();

    /// <summary>
    /// Branches of the selected repository. Read-only because it is handed straight to the picker
    /// and is shared with <see cref="BranchCacheService"/>, which owns the list.
    /// </summary>
    public IReadOnlyList<string> Branches { get; set; } = Array.Empty<string>();

    // Branch form
    public string BranchName { get; set; } = "";
    public string PrName { get; set; } = "";
    public string BaseBranch { get; set; } = "develop";

    /// <summary>Branches already linked to the selected work item, across every repository.</summary>
    public List<WorkItemBranchLink> ExistingBranches { get; set; } = new();

    // Filters
    public string SearchQ { get; set; } = "";
    public string TypeFilter { get; set; } = "";
    public string AssigneeFilter { get; set; } = "";
    public string SortCol { get; set; } = "id";
    public bool SortAsc { get; set; } = true;

    // Result
    public string ResultMsg { get; set; } = "";
    public bool ResultOk { get; set; }
}

public class TaskCreationState
{
    /// <summary>Project, area and sprint the requirements were loaded for; empty before the first load.</summary>
    public string LoadedKey { get; set; } = "";

    /// <summary>Sprint the requirements came from: the iteration their new tasks go into.</summary>
    public string LoadedSprint { get; set; } = "";

    public List<WorkItem> Requirements { get; set; } = new();

    /// <summary>Tasks created per requirement id since the last load, for the "n Created" badge.</summary>
    public Dictionary<string, int> CreatedCounts { get; set; } = new();

    // Filters
    public string SearchQ { get; set; } = "";
    public string TypeFilter { get; set; } = "";
    public string AssigneeFilter { get; set; } = "";
}
