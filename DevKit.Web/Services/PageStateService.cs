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

    /// <summary>The Insight Hub's Task Creation tab, which lives in its own component.</summary>
    public TaskCreationState TaskCreation { get; } = new();

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

    /// <summary>Sprint the requirements came from — the iteration their new tasks go into.</summary>
    public string LoadedSprint { get; set; } = "";

    public List<WorkItem> Requirements { get; set; } = new();

    /// <summary>Tasks created per requirement id since the last load, for the "n Created" badge.</summary>
    public Dictionary<string, int> CreatedCounts { get; set; } = new();

    // Filters
    public string SearchQ { get; set; } = "";
    public string TypeFilter { get; set; } = "";
    public string AssigneeFilter { get; set; } = "";
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

    /// <summary>Number of the latest pull request search. An older search stops writing once a newer one starts.</summary>
    public int PrSearch { get; set; }
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
