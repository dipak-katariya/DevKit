namespace DevKit.Web.Models;

/// <summary>A single CompletedWork change attributed to a member on a date (§3.2).</summary>
public class BurndownContribution
{
    public DateTime Date { get; set; }
    public string MemberDisplay { get; set; } = "";
    public string MemberKey { get; set; } = "";
    public string ItemId { get; set; } = "";
    public string ItemTitle { get; set; } = "";
    public string ItemArea { get; set; } = "";
    public string ItemType { get; set; } = "";
    public bool IsBug { get; set; }
    public double Delta { get; set; }
}

public class BurndownCell
{
    public double Regular { get; set; }
    public double Bug { get; set; }
    public List<BurndownContribution> Items { get; set; } = new();
    public double Total => Regular + Bug;
}

public class BurndownColumn
{
    public string Key { get; set; } = "";
    public string Label { get; set; } = "";

    /// <summary>
    /// Working days the column covers — one for a day, and for a week the sprint's working days
    /// inside it, so the hours expected of a three-day first week are not a full week's.
    /// </summary>
    public int WorkingDays { get; set; } = 1;
}

public class BurndownRow
{
    public string MemberKey { get; set; } = "";
    public string MemberDisplay { get; set; } = "";
    public Dictionary<string, BurndownCell> Cells { get; set; } = new();
    public double TotalRegular { get; set; }
    public double TotalBug { get; set; }
}

public class BurndownMatrix
{
    public List<BurndownColumn> Columns { get; set; } = new();
    public List<BurndownRow> Rows { get; set; } = new();
    public bool IsEmpty => Rows.Count == 0;

    /// <summary>Working days the Total column is measured against: the sprint's elapsed days.</summary>
    public int WorkingDays { get; set; } = 1;

    /// <summary>Working days the whole sprint holds, so the Total can say how far through it is.</summary>
    public int SprintWorkingDays { get; set; } = 1;
}

/// <summary>
/// The sprint window and working-day pattern a period's expected hours are measured against, so a
/// week is held to the days it actually contains. Taken from the Capacity Planning settings.
/// </summary>
public sealed class BurndownScope
{
    public DateTime? Start { get; init; }
    public DateTime? Finish { get; init; }

    /// <summary>
    /// Hours are only expected of days already finished, so a week in progress is not held to a
    /// whole week's. Today itself is still being worked, exactly as the capacity tab counts it.
    /// </summary>
    public DateTime? Today { get; init; }

    public IReadOnlyList<DayOfWeek> WorkingDays { get; init; } = CapacityPlanningSettings.DefaultWorkingDays();
}
