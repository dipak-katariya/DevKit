using DevKit.Web.Models;

namespace DevKit.Web.Services;

/// <summary>
/// Pure validation checks for the Insight Hub Tasks (§4) and Bugs (§5) tabs.
/// No I/O — operates on already-fetched <see cref="AuditData"/>.
/// </summary>
public static class InsightAuditService
{
    // Valid title prefixes = the current planning catalog (shared with Task Creation) plus
    // legacy prefixes still accepted during the naming transition.
    private static readonly string[] DevPrefixes =
        PlanningTaskCatalog.DevPrefixes.Concat(PlanningTaskCatalog.LegacyDevPrefixes).ToArray();
    private static readonly string[] TestPrefixes =
        PlanningTaskCatalog.TestPrefixes.Concat(PlanningTaskCatalog.LegacyTestPrefixes).ToArray();

    // The "Expected Prefix" column shows only the current standard (what Task Creation produces).
    private static readonly string ExpectedDevPrefixes = string.Join(" · ", PlanningTaskCatalog.DevPrefixes);
    private static readonly string ExpectedTestPrefixes = string.Join(" · ", PlanningTaskCatalog.TestPrefixes);

    // Tags that exempt a task from prefix/discipline checks (and their parent req carrying them).
    private static readonly string[] PrefixExemptTags = { "Team B Support", "ALM", "Engineering Operations & Support", "Regression" };
    // Title prefixes exempt from the manual-execution check.
    private static readonly string[] ManualExemptPrefixes = { "Support", "L2", "L3", "Code Support", "L1 Testing", "Plan Review", "Wiki Review" };
    // Test-discipline titles exempt from estimate checks.
    private static readonly string[] RegressionTitles = { "Production Regression", "Azure Regression" };

    private const string Dev = "Development";
    private const string Test = "Test";

    // ─────────────── shared helpers ───────────────

    private static bool TagsContain(string? tags, string tag) =>
        !string.IsNullOrEmpty(tags)
        && tags.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
               .Any(t => string.Equals(t, tag, StringComparison.OrdinalIgnoreCase));

    private static bool StateIn(WorkItem w, params string[] states) =>
        states.Any(s => string.Equals(w.State, s, StringComparison.OrdinalIgnoreCase));

    private static bool IsRejected(WorkItem w) => string.Equals(w.Reason, "Rejected", StringComparison.OrdinalIgnoreCase);

    private static bool StartsWithPrefix(string title, string prefix)
    {
        var t = (title ?? "").Trim();
        if (!t.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return false;
        return t.Length == prefix.Length || !char.IsLetterOrDigit(t[prefix.Length]);
    }

    private static bool MatchesAny(string title, string[] prefixes) => prefixes.Any(p => StartsWithPrefix(title, p));
    private static bool IsRegressionTitle(WorkItem t) => RegressionTitles.Any(p => StartsWithPrefix(t.Title, p));

    private static bool PrefixExempt(WorkItem task, IReadOnlyDictionary<string, string> parentTags)
    {
        if (PrefixExemptTags.Any(task.HasTag)) return true;
        if (!string.IsNullOrEmpty(task.ParentId) && parentTags.TryGetValue(task.ParentId, out var pt))
            return PrefixExemptTags.Any(tag => TagsContain(pt, tag));
        return false;
    }

    private static AuditCheck Check(string title, AuditSeverity sev, IEnumerable<AuditRow> rows,
        string? extra = null, string? extra2 = null) =>
        new() { Title = title, Severity = sev, Rows = rows.ToList(), ExtraColumn = extra, ExtraColumn2 = extra2 };

    private static AuditRow Row(WorkItem w, string? expected = null, string? expected2 = null) =>
        new() { Item = w, Expected = expected, Expected2 = expected2 };

    // ─────────────── §4 TASKS ───────────────

    public static List<AuditCheck> RunTaskChecks(AuditData data)
    {
        var tasks = data.Tasks;
        var parents = data.ParentTags;
        var checks = new List<AuditCheck>();

        checks.Add(Check("Tasks Without Discipline", AuditSeverity.Warning,
            tasks.Where(t => string.IsNullOrWhiteSpace(t.Discipline)).Select(t => Row(t))));

        checks.Add(Check("Tasks Without Original Estimate", AuditSeverity.Warning,
            tasks.Where(t => (t.OriginalEstimate ?? 0) <= 0
                          && !(string.Equals(t.Discipline, Test, StringComparison.OrdinalIgnoreCase) && IsRegressionTitle(t)))
                 .Select(t => Row(t))));

        checks.Add(Check("Tasks — Missing Estimate or Remaining Work", AuditSeverity.Critical,
            tasks.Where(t =>
                    ((t.OriginalEstimate ?? 0) <= 0
                     || (!StateIn(t, "Closed") && t.RemainingWork is null))
                    && !IsRejected(t)
                    && !t.HasTag(BugWork.MaintenanceTag)
                    && !(string.Equals(t.Discipline, Test, StringComparison.OrdinalIgnoreCase) && IsRegressionTitle(t)))
                 .Select(t => Row(t))));

        checks.Add(Check("Development Discipline — Invalid Title Prefix", AuditSeverity.Warning,
            tasks.Where(t => string.Equals(t.Discipline, Dev, StringComparison.OrdinalIgnoreCase)
                          && !MatchesAny(t.Title, DevPrefixes)
                          && !PrefixExempt(t, parents))
                 .Select(t => Row(t, ExpectedDevPrefixes)),
            extra: "Expected Prefix"));

        checks.Add(Check("Test Discipline — Invalid Title Prefix", AuditSeverity.Warning,
            tasks.Where(t => string.Equals(t.Discipline, Test, StringComparison.OrdinalIgnoreCase)
                          && !MatchesAny(t.Title, TestPrefixes)
                          && !PrefixExempt(t, parents))
                 .Select(t => Row(t, ExpectedTestPrefixes)),
            extra: "Expected Prefix"));

        checks.Add(Check("Discipline / Title Prefix Mismatch", AuditSeverity.Warning,
            tasks.Where(t => !PrefixExempt(t, parents) && IsDisciplineMismatch(t))
                 .Select(t => Row(t,
                     string.Equals(t.Discipline, Dev, StringComparison.OrdinalIgnoreCase)
                         ? "Discipline → Test, or retitle to a Development prefix"
                         : "Discipline → Development, or retitle to a Test prefix")),
            extra: "Suggested Fix"));

        checks.Add(Check("Manual Execution Tasks", AuditSeverity.Info,
            tasks.Where(t => string.Equals(t.TaskExecutionType, "Manual", StringComparison.OrdinalIgnoreCase)
                          && !MatchesAny(t.Title, ManualExemptPrefixes)
                          && !t.HasTag("Regression"))
                 .Select(t => Row(t))));

        checks.Add(Check("Has Estimate, No Remaining, No Completed", AuditSeverity.Warning,
            tasks.Where(t => (t.OriginalEstimate ?? 0) > 0 && (t.RemainingWork ?? 0) == 0 && (t.CompletedWork ?? 0) == 0 && !IsRejected(t))
                 .Select(t => Row(t))));

        checks.Add(Check("Closed With Remaining Work", AuditSeverity.Warning,
            tasks.Where(t => StateIn(t, "Closed") && (t.RemainingWork ?? 0) > 0).Select(t => Row(t))));

        checks.Add(Check("No Estimate, Has Completed Work", AuditSeverity.Info,
            tasks.Where(t => (t.OriginalEstimate ?? 0) <= 0 && (t.CompletedWork ?? 0) > 0).Select(t => Row(t))));

        return checks;
    }

    private static bool IsDisciplineMismatch(WorkItem t)
    {
        var isDev = string.Equals(t.Discipline, Dev, StringComparison.OrdinalIgnoreCase);
        var isTest = string.Equals(t.Discipline, Test, StringComparison.OrdinalIgnoreCase);
        if (isDev) return MatchesAny(t.Title, TestPrefixes) && !MatchesAny(t.Title, DevPrefixes);
        if (isTest) return MatchesAny(t.Title, DevPrefixes) && !MatchesAny(t.Title, TestPrefixes);
        return false;
    }

    // ─────────────── §5 BUGS ───────────────

    /// <summary>§5.1 global bug exclusion — by both Reason and Tags.</summary>
    public static bool IsBugExcluded(WorkItem bug)
    {
        string[] terms = { "Duplicate", "Deferred", "Cannot Reproduce", "Cannot Reproduced", "Rejected" };
        if (terms.Any(t => string.Equals(bug.Reason, t, StringComparison.OrdinalIgnoreCase))) return true;
        return terms.Any(bug.HasTag);
    }

    public static List<AuditCheck> RunBugChecks(AuditData data)
    {
        var bugs = data.Bugs;
        var children = data.BugChildren;
        var checks = new List<AuditCheck>();

        checks.Add(Check("Bugs Without Root Cause", AuditSeverity.Warning,
            bugs.Where(b => StateIn(b, "Active", "Resolved", "Closed") && string.IsNullOrWhiteSpace(b.RootCause))
                .Select(b => Row(b))));

        checks.Add(Check("Bugs — No Dev Task", AuditSeverity.Critical,
            bugs.Where(b => !IsBugExcluded(b) && !ChildrenOf(children, b).Any(c => string.Equals(c.Discipline, Dev, StringComparison.OrdinalIgnoreCase)))
                .Select(b => Row(b))));

        var bugTasks = bugs.SelectMany(b => ChildrenOf(children, b).Select(c => (Bug: b, Task: c))).ToList();

        checks.Add(Check("Bug Tasks — Missing Bug/Maintenance Tag & No Work", AuditSeverity.Warning,
            bugTasks.Where(x => !x.Task.HasTag(BugWork.MaintenanceTag) && (x.Task.CompletedWork ?? 0) == 0)
                    .Select(x => Row(x.Task, $"Bug #{x.Bug.Id}")),
            extra: "Parent Bug"));

        checks.Add(Check("Bug Tasks — No Completed Work (Active/Resolved/Closed)", AuditSeverity.Warning,
            bugTasks.Where(x => StateIn(x.Task, "Active", "Resolved", "Closed") && (x.Task.CompletedWork ?? 0) == 0 && !IsRejected(x.Task))
                    .Select(x => Row(x.Task, $"Bug #{x.Bug.Id}")),
            extra: "Parent Bug"));

        checks.Add(Check("Active Bugs", AuditSeverity.Info, bugs.Where(b => StateIn(b, "Active")).Select(b => Row(b))));
        checks.Add(Check("Proposed Bugs", AuditSeverity.Info, bugs.Where(b => StateIn(b, "Proposed")).Select(b => Row(b))));
        checks.Add(Check("Closed Bugs", AuditSeverity.Info, bugs.Where(b => StateIn(b, "Closed")).Select(b => Row(b))));

        // Flag Provided Date (Expected2) is filled by the caller via revision history.
        checks.Add(Check("Resolved Bugs with Published Tag", AuditSeverity.Info,
            bugs.Where(b => StateIn(b, "Resolved") && b.HasTag("Published")).Select(b => Row(b, b.CreatedBy)),
            extra: "Created By", extra2: "Flag Provided Date"));

        checks.Add(Check("Resolved Bugs Not Published", AuditSeverity.Warning,
            bugs.Where(b => StateIn(b, "Resolved") && !b.HasTag("Published")).Select(b => Row(b))));

        checks.Add(Check("Closed Bugs Without Fix", AuditSeverity.Warning,
            bugs.Where(b => StateIn(b, "Closed") && string.IsNullOrWhiteSpace(b.ProposedFix) && !IsBugExcluded(b)).Select(b => Row(b))));

        checks.Add(Check("Resolved Bugs Without Fix", AuditSeverity.Warning,
            bugs.Where(b => StateIn(b, "Resolved") && string.IsNullOrWhiteSpace(b.ProposedFix) && !IsBugExcluded(b)).Select(b => Row(b))));

        checks.Add(Check("Rejected / Deferred / Duplicate Bugs", AuditSeverity.Info,
            bugs.Where(IsBugExcluded).Select(b => Row(b, ExclusionReason(b))), extra: "Classification"));

        checks.Add(Check("Duplicate Without Link", AuditSeverity.Warning,
            bugs.Where(b => (string.Equals(b.Reason, "Duplicate", StringComparison.OrdinalIgnoreCase) || b.HasTag("Duplicate"))
                         && b.DuplicateLinkIds.Count == 0)
                .Select(b => Row(b))));

        return checks;
    }

    private static List<WorkItem> ChildrenOf(IReadOnlyDictionary<string, List<WorkItem>> children, WorkItem bug) =>
        children.TryGetValue(bug.Id, out var list) ? list : new List<WorkItem>();

    private static string ExclusionReason(WorkItem b)
    {
        string[] terms = { "Duplicate", "Deferred", "Cannot Reproduce", "Cannot Reproduced", "Rejected" };
        var hit = terms.FirstOrDefault(t => string.Equals(b.Reason, t, StringComparison.OrdinalIgnoreCase) || b.HasTag(t));
        return hit ?? "Excluded";
    }
}
