namespace DevKit.Web.Models;

public enum AuditSeverity { Critical, Warning, Info }

/// <summary>One row in an audit check — the work item plus optional "expected/fix" columns.</summary>
public class AuditRow
{
    public WorkItem Item { get; set; } = new();
    public string? Expected { get; set; }
    public string? Expected2 { get; set; }
}

/// <summary>A single validation check: a titled, severity-tagged list of offending items.</summary>
public class AuditCheck
{
    public string Title { get; set; } = "";
    public AuditSeverity Severity { get; set; } = AuditSeverity.Info;
    public List<AuditRow> Rows { get; set; } = new();

    /// <summary>Optional extra column headers (e.g. "Expected Prefix").</summary>
    public string? ExtraColumn { get; set; }
    public string? ExtraColumn2 { get; set; }

    public bool Expanded { get; set; }
    public int Count => Rows.Count;
}

/// <summary>Bundle of fetched data the audit checks run against.</summary>
public class AuditData
{
    public List<WorkItem> Tasks { get; set; } = new();
    public List<WorkItem> Bugs { get; set; } = new();

    /// <summary>Parent work-item id → its System.Tags (for parent-tag exclusions).</summary>
    public Dictionary<string, string> ParentTags { get; set; } = new();

    /// <summary>Bug id → its child Task work items.</summary>
    public Dictionary<string, List<WorkItem>> BugChildren { get; set; } = new();
}
