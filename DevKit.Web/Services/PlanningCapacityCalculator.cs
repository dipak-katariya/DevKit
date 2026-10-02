using DevKit.Web.Models;

namespace DevKit.Web.Services;

/// <summary>
/// Pure calculations for Capacity Planning (business-rules §2), driven entirely by
/// <see cref="CapacityPlanningSettings"/>. No I/O: it takes already-fetched tasks and capacity and
/// produces the member rows and team summaries for every scope in a single pass over the tasks.
///
/// Per member:
/// <code>
///   Sprint days   = working weekdays in the iteration      (or weeks × working days per week)
///   Leave         = member + team days off on working weekdays inside the sprint
///   Actual        = capacity-per-day × (sprint days − leave)
///   AI hours      = max(0, Actual − Excluded) × over-plan %   Excluded = hours on tags carved out of the base
///   Target        = Actual + AI hours                         Regular mode: over-plan is 0, so Target = Actual
///   Planning %    = planned (revised, else original estimate) ÷ Target
///   Deliverable % = deliverable hours ÷ planned hours         likewise Non-Deliverable and Untagged
///
///   Completed %   = Completed Work on non-bug tasks ÷ planned hours
///   Remaining %   = Remaining Work on non-bug tasks ÷ planned hours
///   Bug %         = Completed Work on bug work ÷ all Completed Work    (see <see cref="BugWork"/>)
///   Expected %    = available days already elapsed ÷ available days    (leave off both sides)
/// </code>
/// </summary>
public static partial class PlanningCapacityCalculator
{
    private static readonly PlanningScope[] AllScopes = { PlanningScope.Development, PlanningScope.Qa, PlanningScope.All };

    /// <summary>Upper bound on suggested tags, so a sprint with sprawling tagging cannot flood the picker.</summary>
    private const int MaxObservedTags = 500;

    /// <summary>
    /// Member-name normalization (business-rules §1.3): case-insensitive, strips an
    /// "&lt;email&gt;" suffix, and flips "Surname, First" → "first surname".
    /// </summary>
    public static string NormalizeMemberName(string? name)
    {
        var v = (name ?? "").Trim();
        if (v.Length == 0) return "";
        var lt = v.IndexOf('<');
        if (lt > 0) v = v[..lt].Trim();
        v = v.ToLowerInvariant();
        var comma = v.IndexOf(',');
        if (comma > 0)
        {
            var surname = v[..comma].Trim();
            var first = v[(comma + 1)..].Trim();
            if (first.Length > 0) v = $"{first} {surname}";
        }
        return v.Trim();
    }

    /// <summary>Working days in [start, finish] inclusive, where a working day is one of <paramref name="workingDays"/>.</summary>
    public static int CountWorkingDays(DateTime start, DateTime finish, IReadOnlySet<DayOfWeek> workingDays)
    {
        if (finish < start) return 0;
        var count = 0;
        for (var d = start.Date; d <= finish.Date; d = d.AddDays(1))
            if (workingDays.Contains(d.DayOfWeek)) count++;
        return count;
    }

    /// <summary>
    /// Distinct working-day dates inside [start, finish] that any range covers. A set, so a
    /// personal day off that falls on a team holiday is counted once.
    /// </summary>
    public static int CountLeaveOnWorkingDays(
        IEnumerable<DateRange> ranges, DateTime start, DateTime finish, IReadOnlySet<DayOfWeek> workingDays)
    {
        var dates = new HashSet<DateTime>();
        foreach (var r in ranges)
        {
            var from = r.Start.Date < start.Date ? start.Date : r.Start.Date;
            var to = r.End.Date > finish.Date ? finish.Date : r.End.Date;
            for (var d = from; d <= to; d = d.AddDays(1))
                if (workingDays.Contains(d.DayOfWeek)) dates.Add(d);
        }
        return dates.Count;
    }

    /// <summary>
    /// Builds the whole sprint's plan — every scope, every member, every team total, and progress
    /// to date. <paramref name="today"/> is a parameter rather than the clock so the result is
    /// reproducible, and so tests can stand on any day of a sprint.
    /// </summary>
    public static CapacityPlan Build(SprintWorkData data, CapacityPlanningSettings settings, DateTime today)
    {
        ArgumentNullException.ThrowIfNull(data);
        ArgumentNullException.ThrowIfNull(settings);

        var workingDays = settings.WorkingDays.ToHashSet();
        var sprint = ResolveSprint(data.Capacity, settings, workingDays, today);
        var tagIndex = new TagIndex(data.Parents);
        var classifier = new TaskClassifier(settings, tagIndex);
        var members = TallyMembers(data.Tasks, classifier);
        var capacities = members.Values.ToDictionary(
            m => m.Key, m => MemberCapacityFor(m.Key, data.Capacity, sprint, settings, workingDays));

        var rowsByScope = new Dictionary<PlanningScope, IReadOnlyList<MemberPlanning>>();
        var summaries = new Dictionary<PlanningScope, TeamPlanningSummary>();
        foreach (var scope in AllScopes)
        {
            var rows = members.Values
                .Select(m => ToRow(m, scope, capacities[m.Key], sprint.Days, settings.EffectiveOverPlanPercent))
                .OfType<MemberPlanning>()
                .OrderBy(r => r.DisplayName, StringComparer.OrdinalIgnoreCase)
                .ToList();
            rowsByScope[scope] = rows;
            summaries[scope] = Summarize(scope, rows);
        }

        return new CapacityPlan
        {
            SprintDays = sprint.Days,
            SprintDaysFromIteration = sprint.FromIteration,
            ElapsedDays = sprint.ElapsedDays,
            Members = rowsByScope,
            Summaries = summaries,
            ObservedTags = tagIndex.Observed(MaxObservedTags)
        };
    }

    // ─── Sprint length and elapsed time ───

    /// <param name="ElapsedEnd">Last working-day candidate already behind us, capped at the finish.</param>
    private sealed record SprintWindow(
        int Days, bool FromIteration, DateTime? Start, DateTime? Finish, int? ElapsedDays, DateTime? ElapsedEnd);

    /// <summary>
    /// Iteration dates when configured and present; otherwise the fixed length. An iteration with
    /// no dates used to yield zero working days — and so zero capacity for everyone.
    ///
    /// Elapsed days count only days fully behind us: Completed Work is usually logged at the end
    /// of a day, so counting today would mark everyone behind every morning.
    /// </summary>
    private static SprintWindow ResolveSprint(
        SprintCapacity capacity, CapacityPlanningSettings settings, IReadOnlySet<DayOfWeek> workingDays, DateTime today)
    {
        if (capacity.Start is not { } start || capacity.Finish is not { } finish)
            return new SprintWindow(settings.FixedSprintDays, false, null, null, null, null);

        var fromIteration = settings.SprintLength == SprintLengthSource.IterationDates;
        var days = fromIteration ? CountWorkingDays(start, finish, workingDays) : settings.FixedSprintDays;

        var lastFullDay = today.Date.AddDays(-1);
        var elapsedEnd = lastFullDay < finish.Date ? lastFullDay : finish.Date;
        var elapsed = elapsedEnd < start.Date ? 0 : Math.Min(days, CountWorkingDays(start, elapsedEnd, workingDays));

        return new SprintWindow(days, fromIteration, start, finish, elapsed, elapsedEnd);
    }

    // ─── Per-member capacity (the same in every scope) ───

    private sealed record MemberCapacityFacts(double PerDay, bool FromTfs, double LeaveDays, double Actual, double? ExpectedPct);

    private static MemberCapacityFacts MemberCapacityFor(
        string memberKey, SprintCapacity capacity, SprintWindow sprint, CapacityPlanningSettings settings, IReadOnlySet<DayOfWeek> workingDays)
    {
        capacity.Members.TryGetValue(memberKey, out var cap);
        var fromTfs = settings.UseTfsMemberCapacity && cap is { CapacityPerDay: > 0 };
        var perDay = fromTfs ? cap!.CapacityPerDay : settings.HoursPerDay;

        if (sprint.Start is not { } start || sprint.Finish is not { } finish)
            return new MemberCapacityFacts(perDay, fromTfs, 0, PlanningMath.Round2(perDay * sprint.Days), null);

        // A set of dates, so a personal day off on a team holiday is counted once.
        var ranges = new List<DateRange>(capacity.TeamDaysOff);
        if (cap != null) ranges.AddRange(cap.DaysOff);

        var leave = Math.Min(sprint.Days, CountLeaveOnWorkingDays(ranges, start, finish, workingDays));
        var available = Math.Max(0, sprint.Days - leave);
        var actual = PlanningMath.Round2(perDay * available);

        return new MemberCapacityFacts(perDay, fromTfs, leave, actual, ExpectedPct(sprint, ranges, available, workingDays));
    }

    /// <summary>
    /// How far through their own plan a member should be by now: the share of their available
    /// days already behind them. Leave comes off both sides, so a week away does not read as a
    /// week behind.
    /// </summary>
    private static double? ExpectedPct(SprintWindow sprint, List<DateRange> ranges, double available, IReadOnlySet<DayOfWeek> workingDays)
    {
        if (sprint.ElapsedDays is not { } elapsedDays || sprint.Start is not { } start || sprint.ElapsedEnd is not { } elapsedEnd)
            return null;

        var leaveSoFar = elapsedEnd < start ? 0 : CountLeaveOnWorkingDays(ranges, start, elapsedEnd, workingDays);
        var availableSoFar = Math.Clamp(elapsedDays - leaveSoFar, 0, available);
        return PlanningMath.Pct(availableSoFar, available);
    }

    // ─── Rows and totals ───

    private static MemberPlanning? ToRow(MemberTally member, PlanningScope scope, MemberCapacityFacts cap, int sprintDays, double overPlanPercent)
    {
        var tally = member.For(scope);
        if (tally.Tasks == 0) return null;

        var excluded = tally.ExcludedHours;

        // Clamped: support work larger than the member's capacity would otherwise make the
        // over-plan negative and shrink the target below what they can actually work.
        var aiHours = Math.Max(0, cap.Actual - excluded) * overPlanPercent / 100.0;

        return new MemberPlanning
        {
            DisplayName = member.DisplayName,
            CapacityPerDay = cap.PerDay,
            CapacityFromTfs = cap.FromTfs,
            SprintDays = sprintDays,
            LeaveDays = cap.LeaveDays,
            ActualCapacity = cap.Actual,
            ExcludedByTag = tally.ExcludedBreakdown(),
            ExcludedHours = PlanningMath.Round2(excluded),
            AiHours = PlanningMath.Round2(aiHours),
            TargetCapacity = PlanningMath.Round2(cap.Actual + aiHours),
            PlannedPlanningDay = PlanningMath.Round2(tally.Plan),
            PlannedReviewDay = PlanningMath.Round2(tally.Review),
            TaskCount = tally.Tasks,
            WorksAcrossTeams = member.WorksAcrossTeams,
            Split = new DeliverableSplit(
                PlanningMath.Round2(tally.Deliverable),
                PlanningMath.Round2(tally.NonDeliverable),
                PlanningMath.Round2(tally.Untagged)),
            CompletedHours = PlanningMath.Round2(tally.Completed),
            RemainingHours = PlanningMath.Round2(tally.Remaining),
            BugHours = PlanningMath.Round2(tally.BugHours),
            ExpectedPct = cap.ExpectedPct
        };
    }

    /// <summary>
    /// Team progress expectation, weighted by each member's planned hours: a member carrying most
    /// of the plan moves the team's "should be done by now" more than one carrying a little.
    /// </summary>
    private static double? WeightedExpectedPct(IReadOnlyList<MemberPlanning> rows)
    {
        if (rows.Count == 0 || rows.Any(r => r.ExpectedPct is null)) return null;

        var planned = rows.Sum(r => r.PlannedReviewDay);
        return planned > 0
            ? Math.Round(rows.Sum(r => r.PlannedReviewDay * r.ExpectedPct!.Value) / planned, 1)
            : Math.Round(rows.Average(r => r.ExpectedPct!.Value), 1);
    }

    private static TeamPlanningSummary Summarize(PlanningScope scope, IReadOnlyList<MemberPlanning> rows) => new(
        scope,
        rows.Count,
        rows.Count(r => r.WorksAcrossTeams),
        PlanningMath.Round2(rows.Sum(r => r.ActualCapacity)),
        PlanningMath.Round2(rows.Sum(r => r.ExcludedHours)),
        PlanningMath.Round2(rows.Sum(r => r.AiHours)),
        PlanningMath.Round2(rows.Sum(r => r.TargetCapacity)),
        PlanningMath.Round2(rows.Sum(r => r.PlannedPlanningDay)),
        PlanningMath.Round2(rows.Sum(r => r.PlannedReviewDay)),
        new DeliverableSplit(
            PlanningMath.Round2(rows.Sum(r => r.Split.DeliverableHours)),
            PlanningMath.Round2(rows.Sum(r => r.Split.NonDeliverableHours)),
            PlanningMath.Round2(rows.Sum(r => r.Split.OtherHours))),
        PlanningMath.Round2(rows.Sum(r => r.CompletedHours)),
        PlanningMath.Round2(rows.Sum(r => r.RemainingHours)),
        PlanningMath.Round2(rows.Sum(r => r.BugHours)),
        WeightedExpectedPct(rows));

    /// <summary>One pass over the tasks, bucketing each into its member and team.</summary>
    private static Dictionary<string, MemberTally> TallyMembers(IReadOnlyList<WorkItem> tasks, TaskClassifier classifier)
    {
        var members = new Dictionary<string, MemberTally>(StringComparer.Ordinal);
        foreach (var task in tasks)
        {
            var key = NormalizeMemberName(task.Assigned);
            if (key.Length == 0 || key == "-") continue;

            if (!members.TryGetValue(key, out var member))
                members[key] = member = new MemberTally(key, task.Assigned);

            member.Add(classifier.Classify(task));
        }
        return members;
    }
}
