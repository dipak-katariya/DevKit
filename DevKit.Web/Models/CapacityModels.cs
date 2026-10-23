namespace DevKit.Web.Models;

public class TfsTeam
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
}

public class DateRange
{
    public DateTime Start { get; set; }
    public DateTime End { get; set; }
}

/// <summary>
/// A member's capacity in the sprint, with the record of every TFS team that counts it. Keyed in
/// <see cref="SprintCapacity.Members"/> by the normalized member name.
/// </summary>
public class MemberCapacity
{
    public string DisplayName { get; set; } = "";

    /// <summary>One entry per TFS team, in team order. A member split over two teams has both shares here.</summary>
    public List<TeamMemberCapacity> Teams { get; set; } = new();

    /// <summary>Hours a day over all of the member's teams, such as 4.5 in one team and 4 in another making 8.5.</summary>
    public double CapacityPerDay => Teams.Sum(team => team.CapacityPerDay);
}

/// <summary>A member's capacity in one TFS team for the sprint, as that team's capacity page records it.</summary>
public class TeamMemberCapacity
{
    public string Team { get; set; } = "";
    public double CapacityPerDay { get; set; }

    /// <summary>The member's own days off in this team plus the team's days off.</summary>
    public List<DateRange> DaysOff { get; set; } = new();
}

/// <summary>Capacity data for a single sprint: per-member capacity and days off, and the iteration date range.</summary>
public class SprintCapacity
{
    public bool Loaded { get; set; }
    public string? Error { get; set; }

    /// <summary>
    /// TFS could not be read at all, so <see cref="Error"/> says why; distinct from a sprint that simply has no
    /// capacity configured.
    /// </summary>
    public bool ReadFailed { get; set; }

    public int TeamsScanned { get; set; }

    /// <summary>
    /// Teams whose settings or days off could not be read. Their members fall back to the default capacity,
    /// or keep leave that was not deducted, so the plan is not the whole picture while this is above zero.
    /// </summary>
    public int TeamsUnread { get; set; }
    public DateTime? Start { get; set; }
    public DateTime? Finish { get; set; }
    public Dictionary<string, MemberCapacity> Members { get; set; } = new();

    /// <summary>
    /// Days off that every team planning the area takes, such as a public holiday, one entry per day. A member no
    /// team lists has no team of their own, so these are their team days off.
    /// </summary>
    public List<DateRange> SharedDaysOff { get; set; } = new();
}

/// <summary>Rounding shared by every planning figure, so a member row and its team total agree.</summary>
public static class PlanningMath
{
    public static double Round2(double v) => Math.Round(v * 100) / 100;

    /// <summary><paramref name="part"/> as a percentage of <paramref name="whole"/>, to one decimal; 0 when there is no whole.</summary>
    public static double Pct(double part, double whole) => whole > 0 ? Math.Round(part / whole * 100, 1) : 0;
}

/// <summary>Deliverable vs Non-Deliverable split of planned hours, for a member, a team or the whole sprint.</summary>
public record DeliverableSplit(double DeliverableHours, double NonDeliverableHours, double OtherHours)
{
    public static readonly DeliverableSplit Empty = new(0, 0, 0);

    public double Total => DeliverableHours + NonDeliverableHours + OtherHours;
    public double DeliverablePct => PlanningMath.Pct(DeliverableHours, Total);
    public double NonDeliverablePct => PlanningMath.Pct(NonDeliverableHours, Total);
    public double OtherPct => PlanningMath.Pct(OtherHours, Total);
}

/// <summary>
/// What one Load reads from TFS for a sprint: its tasks, the team's capacity, and the tasks'
/// parents keyed by id (their tags classify deliverables and their type marks bug work).
/// </summary>
public sealed record SprintWorkData(
    IReadOnlyList<WorkItem> Tasks,
    SprintCapacity Capacity,
    IReadOnlyDictionary<string, WorkItem> Parents)
{
    public static readonly SprintWorkData Empty =
        new(Array.Empty<WorkItem>(), new SprintCapacity(), new Dictionary<string, WorkItem>());
}

/// <summary>Which part of the team a planning view covers.</summary>
public enum PlanningScope
{
    Development = 0,
    Qa = 1,
    All = 2
}

/// <summary>Hours carved out of the AI over-plan base by one tag rule.</summary>
public sealed record TagHours(string Tag, double Hours);

/// <summary>
/// The part of a member's row that one TFS team contributes: its hours a day, its leave, and
/// hours a day × (sprint days − leave). The shares add up to the row's Cap/Day and Actual.
/// </summary>
public sealed record TeamCapacityShare(string Team, double CapacityPerDay, double LeaveDays, double ActualCapacity);

/// <summary>One member's row in the planning table, for one <see cref="PlanningScope"/>.</summary>
public class MemberPlanning
{
    public string DisplayName { get; set; } = "";
    public double CapacityPerDay { get; set; }

    /// <summary>True when the per-day capacity came from TFS rather than the configured default.</summary>
    public bool CapacityFromTfs { get; set; }

    public int SprintDays { get; set; }

    /// <summary>
    /// Leave on working days. For a member in several TFS teams, each team's leave weighted by the member's
    /// hours there, since a team's days off take only that team's hours away.
    /// </summary>
    public double LeaveDays { get; set; }
    public double AvailableDays => Math.Max(0, SprintDays - LeaveDays);

    /// <summary>Capacity-per-day × available days.</summary>
    public double ActualCapacity { get; set; }

    /// <summary>What each TFS team adds to the row when the member is in more than one; empty otherwise.</summary>
    public IReadOnlyList<TeamCapacityShare> TfsTeams { get; set; } = Array.Empty<TeamCapacityShare>();

    /// <summary>Hours on tags carved out of the over-plan base, broken down by the rule that matched.</summary>
    public IReadOnlyList<TagHours> ExcludedByTag { get; set; } = Array.Empty<TagHours>();
    public double ExcludedHours { get; set; }

    /// <summary>The over-plan: what AI tooling is expected to add on top of <see cref="ActualCapacity"/>.</summary>
    public double AiHours { get; set; }

    /// <summary>Actual + AI hours: what planning is measured against. Equal to Actual in Regular mode.</summary>
    public double TargetCapacity { get; set; }

    /// <summary>Planned hours using Original Estimate (Planning Day).</summary>
    public double PlannedPlanningDay { get; set; }

    /// <summary>Planned hours using Revised-else-Original (Review Day).</summary>
    public double PlannedReviewDay { get; set; }

    public int TaskCount { get; set; }

    /// <summary>
    /// Set when the member has work in both the development and QA disciplines. Each team view then
    /// counts that team's tasks against the member's whole capacity, so the two views overlap.
    /// </summary>
    public bool WorksAcrossTeams { get; set; }

    public DeliverableSplit Split { get; set; } = DeliverableSplit.Empty;

    public double PlanningPctPlanningDay => PlanningMath.Pct(PlannedPlanningDay, TargetCapacity);
    public double PlanningPctReviewDay => PlanningMath.Pct(PlannedReviewDay, TargetCapacity);

    // ─── Sprint progress ───

    /// <summary>Completed Work on the member's non-bug tasks: progress against the plan.</summary>
    public double CompletedHours { get; set; }

    /// <summary>Remaining Work on the member's non-bug tasks: what is still pending.</summary>
    public double RemainingHours { get; set; }

    /// <summary>Completed Work on bug work. Shown alongside, never counted as progress against the plan.</summary>
    public double BugHours { get; set; }

    /// <summary>
    /// Share of the member's available days already behind them, which is how far through the plan they
    /// should be by now. Their own leave is taken out of both sides. Null when the sprint has no dates.
    /// </summary>
    public double? ExpectedPct { get; set; }

    public double CompletedPct => PlanningMath.Pct(CompletedHours, PlannedReviewDay);
    public double RemainingPct => PlanningMath.Pct(RemainingHours, PlannedReviewDay);

    /// <summary>Bug hours as a share of every hour the member logged in the sprint.</summary>
    public double BugPct => PlanningMath.Pct(BugHours, CompletedHours + BugHours);
}

/// <summary>Totals for one team view: the development team, the QA team, or everyone.</summary>
public sealed record TeamPlanningSummary(
    PlanningScope Scope,
    int MemberCount,
    int MembersAcrossTeams,
    double ActualCapacity,
    double ExcludedHours,
    double AiHours,
    double TargetCapacity,
    double PlannedPlanningDay,
    double PlannedReviewDay,
    DeliverableSplit Split,
    double CompletedHours,
    double RemainingHours,
    double BugHours,
    double? ExpectedPct)
{
    public double PlanningPct => PlanningMath.Pct(PlannedReviewDay, TargetCapacity);
    public double CompletedPct => PlanningMath.Pct(CompletedHours, PlannedReviewDay);
    public double RemainingPct => PlanningMath.Pct(RemainingHours, PlannedReviewDay);
    public double BugPct => PlanningMath.Pct(BugHours, CompletedHours + BugHours);

    public static TeamPlanningSummary Empty(PlanningScope scope) =>
        new(scope, 0, 0, 0, 0, 0, 0, 0, 0, DeliverableSplit.Empty, 0, 0, 0, null);
}

/// <summary>A whole sprint's capacity plan, worked out once for every scope.</summary>
public sealed class CapacityPlan
{
    public static readonly CapacityPlan Empty = new();

    public int SprintDays { get; init; }

    /// <summary>False when the sprint length came from the configured weeks rather than TFS dates.</summary>
    public bool SprintDaysFromIteration { get; init; }

    /// <summary>
    /// Working days of the sprint fully behind us. Today does not count until it is over. Null
    /// when the sprint has no dates, since elapsed time cannot be placed without them.
    /// </summary>
    public int? ElapsedDays { get; init; }

    public double? ElapsedPct => ElapsedDays is { } elapsed ? PlanningMath.Pct(elapsed, SprintDays) : null;

    public IReadOnlyDictionary<PlanningScope, IReadOnlyList<MemberPlanning>> Members { get; init; } =
        new Dictionary<PlanningScope, IReadOnlyList<MemberPlanning>>();

    public IReadOnlyDictionary<PlanningScope, TeamPlanningSummary> Summaries { get; init; } =
        new Dictionary<PlanningScope, TeamPlanningSummary>();

    /// <summary>Every tag seen on the sprint's tasks or their parents, offered when adding a tag rule.</summary>
    public IReadOnlyList<string> ObservedTags { get; init; } = Array.Empty<string>();

    public IReadOnlyList<MemberPlanning> For(PlanningScope scope) =>
        Members.TryGetValue(scope, out var rows) ? rows : Array.Empty<MemberPlanning>();

    public TeamPlanningSummary SummaryFor(PlanningScope scope) =>
        Summaries.TryGetValue(scope, out var summary) ? summary : TeamPlanningSummary.Empty(scope);
}
