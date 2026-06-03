using DevKit.Web.Models;

namespace DevKit.Web.Services;

/// <summary>
/// Scoped service that preserves page state across tab navigation within the same Blazor circuit.
/// </summary>
public class PageStateService
{
    // ═══ BRANCH CREATOR STATE ═══
    public BranchCreatorState BranchCreator { get; } = new();

    // ═══ MERGE TOOL STATE ═══
    public MergeToolState MergeTool { get; } = new();

    // ═══ SHARED TFS DATA (loaded once, shared across pages) ═══
    public SharedTfsData TfsData { get; } = new();
}

public class SharedTfsData
{
    public bool IsLoaded { get; set; }
    public List<TfsProject> Projects { get; set; } = new();
    public List<TfsRepo> Repos { get; set; } = new();
    public List<TfsArea> Areas { get; set; } = new();
    public List<TfsIteration> Iterations { get; set; } = new();
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
    public List<string> Branches { get; set; } = new();

    // Branch form
    public string BranchName { get; set; } = "";
    public string PrName { get; set; } = "";
    public string BaseBranch { get; set; } = "develop";

    // Existing branches
    public List<string> ExistingBranches { get; set; } = new();

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

public class MergeToolState
{
    public bool HasState { get; set; }

    // Selections
    public TfsArea? SelectedArea { get; set; }
    public string? SelectedSprint { get; set; }
    public string? SelectedItemId { get; set; }

    // Local repo
    public string SelectedLocalRepoPath { get; set; } = "";
    public string SelectedLocalRepoName { get; set; } = "";
    public string GlobalBaseBranch { get; set; } = "";
    public List<string> GlobalBranches { get; set; } = new();

    // Work items
    public List<WorkItem> WorkItems { get; set; } = new();
    public List<WorkItem> Filtered { get; set; } = new();

    // PR data
    public Dictionary<string, RepoPrData> RepoPRs { get; set; } = new();
    public Dictionary<string, List<string>> BranchCache { get; set; } = new();
    public Dictionary<string, CherryPickHistoryEntry> CherryHistory { get; set; } = new();

    // UI state
    public bool PrSortDesc { get; set; } = true;
    public string SearchQ { get; set; } = "";
    public string TypeFilter { get; set; } = "";
    public string AssigneeFilter { get; set; } = "";
    public string SortCol { get; set; } = "id";
    public bool SortAsc { get; set; } = true;
    public HashSet<string> CollapsedProjects { get; set; } = new();
    public HashSet<string> CollapsedPRs { get; set; } = new();
}
