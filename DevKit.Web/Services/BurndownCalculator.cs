using System.Globalization;
using DevKit.Web.Models;

namespace DevKit.Web.Services;

/// <summary>Aggregates raw <see cref="BurndownContribution"/>s into a member × period matrix (§3).</summary>
public static class BurndownCalculator
{
    public static BurndownMatrix Build(IReadOnlyList<BurndownContribution> contributions, bool weekly, string? memberFilter,
        BurndownScope? scope = null)
    {
        var matrix = new BurndownMatrix();
        if (contributions.Count == 0) return matrix;

        var filtered = string.IsNullOrWhiteSpace(memberFilter)
            ? contributions
            : contributions.Where(c => c.MemberDisplay.Contains(memberFilter, StringComparison.OrdinalIgnoreCase)).ToList();
        if (filtered.Count == 0) return matrix;

        var window = scope ?? new BurndownScope();
        var workingDays = window.WorkingDays.ToHashSet();

        matrix.Columns = BuildColumns(filtered, weekly, window, workingDays);
        (matrix.WorkingDays, matrix.SprintWorkingDays) = TotalWorkingDays(matrix.Columns, window, workingDays);
        foreach (var group in filtered.GroupBy(c => c.MemberKey).OrderBy(g => g.First().MemberDisplay, StringComparer.OrdinalIgnoreCase))
            matrix.Rows.Add(BuildRow(group, weekly));
        return matrix;
    }

    private static List<BurndownColumn> BuildColumns(IEnumerable<BurndownContribution> contributions, bool weekly,
        BurndownScope window, IReadOnlySet<DayOfWeek> workingDays)
    {
        var columns = new Dictionary<string, (BurndownColumn Col, DateTime Sort)>();
        foreach (var c in contributions)
        {
            var (key, label, sort) = weekly ? WeekColumn(c.Date) : DayColumn(c.Date);
            if (columns.ContainsKey(key)) continue;
            var span = WorkingDaysIn(sort, weekly ? sort.AddDays(6) : sort, window, workingDays);
            columns[key] = (new BurndownColumn { Key = key, Label = label, WorkingDays = span }, sort);
        }
        return columns.OrderBy(kv => kv.Value.Sort).Select(kv => kv.Value.Col).ToList();
    }

    private static BurndownRow BuildRow(IGrouping<string, BurndownContribution> group, bool weekly)
    {
        var row = new BurndownRow { MemberKey = group.Key, MemberDisplay = group.First().MemberDisplay };
        foreach (var c in group)
        {
            var key = (weekly ? WeekColumn(c.Date) : DayColumn(c.Date)).Key;
            if (!row.Cells.TryGetValue(key, out var cell)) { cell = new BurndownCell(); row.Cells[key] = cell; }
            if (c.IsBug) { cell.Bug += c.Delta; row.TotalBug += c.Delta; }
            else { cell.Regular += c.Delta; row.TotalRegular += c.Delta; }
            cell.Items.Add(c);
        }
        foreach (var cell in row.Cells.Values)
        {
            cell.Regular = Round2(cell.Regular);
            cell.Bug = Round2(cell.Bug);
        }
        row.TotalRegular = Round2(row.TotalRegular);
        row.TotalBug = Round2(row.TotalBug);
        return row;
    }

    /// <summary>
    /// What the Total is measured against — the sprint's elapsed working days, so the Total is not
    /// judged on days still to come — and how many the whole sprint holds. Without sprint dates,
    /// both are what the columns themselves cover.
    /// </summary>
    private static (int Elapsed, int Sprint) TotalWorkingDays(List<BurndownColumn> columns, BurndownScope window,
        IReadOnlySet<DayOfWeek> workingDays)
    {
        if (window.Start is { } start && window.Finish is { } finish)
        {
            var sprint = PlanningCapacityCalculator.CountWorkingDays(start, finish, workingDays);
            if (sprint > 0) return (WorkingDaysIn(start, finish, window, workingDays), sprint);
        }
        var covered = columns.Sum(c => c.WorkingDays);
        return (covered, covered);
    }

    /// <summary>
    /// Working days of [from, to] that the sprint covers and that have already finished — zero for
    /// a period nobody owes this sprint hours in yet, rather than one they are behind on.
    /// </summary>
    private static int WorkingDaysIn(DateTime from, DateTime to, BurndownScope window, IReadOnlySet<DayOfWeek> workingDays)
    {
        if (window.Start is { } start && start.Date > from) from = start.Date;
        if (window.Finish is { } finish && finish.Date < to) to = finish.Date;
        if (window.Today is { } today && today.Date.AddDays(-1) < to) to = today.Date.AddDays(-1);
        return PlanningCapacityCalculator.CountWorkingDays(from, to, workingDays);
    }

    private static (string Key, string Label, DateTime Sort) DayColumn(DateTime d)
        => (d.ToString("yyyy-MM-dd"), d.ToString("ddd d MMM", CultureInfo.InvariantCulture), d.Date);

    private static (string Key, string Label, DateTime Sort) WeekColumn(DateTime d)
    {
        var week = ISOWeek.GetWeekOfYear(d);
        var year = ISOWeek.GetYear(d);
        var monday = ISOWeek.ToDateTime(year, week, DayOfWeek.Monday);
        return ($"{year}-W{week:00}", $"W{week} · {monday:d MMM}", monday);
    }

    private static double Round2(double v) => Math.Round(v * 100) / 100;
}
