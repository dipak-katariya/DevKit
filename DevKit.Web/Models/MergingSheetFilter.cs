namespace DevKit.Web.Models;

/// <summary>
/// Client-side filtering and ordering for the Code Merging Sheet.
///
/// Every visible number goes through <see cref="VisiblePrs"/> — the pull-request table, the
/// per-row counts and the summary cards all call it — so the figures on screen cannot drift
/// apart as filters are added. The Created By filter is why this matters: it narrows the
/// pull requests *inside* a row as well as the rows themselves.
/// </summary>
public class MergingSheetFilter
{
    public const string Merged = "merged";
    public const string Partial = "partial";
    public const string NotMerged = "notmerged";
    public const string RolledBack = "rolledback";
    public const string Unchecked = "unchecked";
    public const string NoPrs = "noprs";

    /// <summary>A pull request's code comparison found something to look at, or could not run.</summary>
    public const string CodeIssues = "codeissues";

    public string Search { get; set; } = "";
    public string Member { get; set; } = "";
    public string Author { get; set; } = "";
    public string MergeState { get; set; } = "";

    /// <summary>
    /// Order by when work started — see <see cref="WorkStarted"/> — instead of by assignee; rows
    /// with no pull requests go last. On by default: reading the sheet in the order work actually
    /// started is the common case.
    /// </summary>
    public bool CommitWise { get; set; } = true;

    /// <summary>
    /// Include pull requests that did not target their repository's QA branch. Off by default,
    /// because the sheet exists to answer "did this reach the release branch" and an
    /// intermediate feature-branch pull request cannot. Turned on to cross-check that no pull
    /// request belonging to a work item has been missed.
    /// </summary>
    public bool ShowAllPrs { get; set; }

    /// <summary>
    /// The one place a row's pull requests are chosen. Counts, the per-row table, the summary
    /// cards and the merge roll-up all read it, so nothing on screen can disagree with anything
    /// else — including when <see cref="ShowAllPrs"/> widens the set.
    /// </summary>
    public List<MergingPullRequest> VisiblePrs(RequirementMergingRow row)
    {
        var prs = ShowAllPrs ? row.AllPullRequests : row.PullRequests;
        return string.IsNullOrEmpty(Author) ? prs : prs.Where(p => p.CreatedBy == Author).ToList();
    }

    public List<RequirementMergingRow> Apply(IEnumerable<RequirementMergingRow> rows)
    {
        var matched = rows.Where(Matches);
        return (CommitWise
                ? matched.OrderBy(r => WorkStarted(r) ?? DateTime.MaxValue).ThenBy(r => r.WorkItemId)
                : matched.OrderBy(r => r.AssignedTo, StringComparer.OrdinalIgnoreCase).ThenBy(r => r.WorkItemId))
            .ToList();
    }

    /// <summary>
    /// When work on a row began, read from its visible pull requests: each one's earliest commit, or its
    /// creation while its commits are not read yet. Most rows' commits load only on demand, so without
    /// that stand-in they would all sort last by id until every commit was in; and reading the visible
    /// pull requests keeps the order in step with All PRs and Created By.
    /// </summary>
    private DateTime? WorkStarted(RequirementMergingRow row) => VisiblePrs(row).Min(p => p.FirstActivityDate);

    /// <summary>
    /// The rows every filter except the merge state lets through, unordered. The merge-state chips
    /// count these, so picking one state still shows how many work items are in each of the others.
    /// </summary>
    public List<RequirementMergingRow> ApplyAllButMergeState(IEnumerable<RequirementMergingRow> rows) =>
        rows.Where(MatchesAllButMergeState).ToList();

    /// <summary>Work items per merge state, computing each row's roll-up once.</summary>
    public Dictionary<MergeRollup, int> CountByRollup(IEnumerable<RequirementMergingRow> rows) =>
        rows.GroupBy(r => Rollup(r).State).ToDictionary(g => g.Key, g => g.Count());

    public int CountMergedPrs(IEnumerable<RequirementMergingRow> rows) =>
        rows.SelectMany(VisiblePrs).Count(p => p.EffectiveMerged == true);

    public int CountNotMergedPrs(IEnumerable<RequirementMergingRow> rows) =>
        rows.SelectMany(VisiblePrs).Count(p => p.EffectiveMerged == false);

    /// <summary>
    /// Pull requests whose code comparison has finished, whatever it found. One skipped for having
    /// nothing to compare — abandoned, say — was not compared, so it does not count.
    /// </summary>
    public int CountCodeCompared(IEnumerable<RequirementMergingRow> rows) =>
        rows.SelectMany(VisiblePrs).Count(p => p.CodeCheck is { Verdict: not (CodeVerdict.Checking or CodeVerdict.Skipped) });

    /// <summary>Pull requests whose code needs a look: missing, partly merged, not comparable, or failed.</summary>
    public int CountCodeIssues(IEnumerable<RequirementMergingRow> rows) =>
        rows.SelectMany(VisiblePrs).Count(p => p.CodeCheck?.NeedsAttention == true);

    /// <summary>Pull requests whose commits are all on the branch while their code is not.</summary>
    public int CountCodeLost(IEnumerable<RequirementMergingRow> rows) =>
        rows.SelectMany(VisiblePrs).Count(p => p.CodeContradictsCommits);

    /// <summary>Commits that reached the branch and were reverted off it again.</summary>
    public int CountRolledBackCommits(IEnumerable<RequirementMergingRow> rows) =>
        rows.SelectMany(VisiblePrs).Sum(p => p.RolledBackCommitCount);

    public int CountPrs(IEnumerable<RequirementMergingRow> rows) => rows.Sum(r => VisiblePrs(r).Count);

    public int CountCommits(IEnumerable<RequirementMergingRow> rows) =>
        rows.Sum(r => VisiblePrs(r).Sum(p => p.Commits.Count));

    /// <summary>Work items with at least one visible pull request.</summary>
    public int CountWithPrs(IEnumerable<RequirementMergingRow> rows) => rows.Count(r => VisiblePrs(r).Count > 0);

    private bool Matches(RequirementMergingRow row) => MatchesAllButMergeState(row) && MatchesMergeState(row);

    private bool MatchesAllButMergeState(RequirementMergingRow row)
    {
        if (!string.IsNullOrEmpty(Member) && row.AssignedTo != Member)
        {
            return false;
        }
        if (!string.IsNullOrEmpty(Author) && VisiblePrs(row).Count == 0)
        {
            return false;
        }
        return MatchesSearch(row);
    }

    private bool MatchesSearch(RequirementMergingRow row) =>
        string.IsNullOrEmpty(Search)
        || row.WorkItemId.ToString().Contains(Search, StringComparison.OrdinalIgnoreCase)
        || row.Title.Contains(Search, StringComparison.OrdinalIgnoreCase);

    /// <summary>The work-item level roll-up, computed from the pull requests this filter shows.</summary>
    public MergeRollupResult Rollup(RequirementMergingRow row) => MergeRollupCalculator.For(VisiblePrs(row));

    private bool MatchesMergeState(RequirementMergingRow row)
    {
        if (string.IsNullOrEmpty(MergeState)) return true;
        if (MergeState == CodeIssues)
        {
            return VisiblePrs(row).Any(p => p.CodeCheck?.NeedsAttention == true);
        }

        var state = Rollup(row).State;
        return MergeState switch
        {
            Merged => state == MergeRollup.Merged,
            Partial => state == MergeRollup.Partial,
            NotMerged => state == MergeRollup.NotMerged,
            RolledBack => state == MergeRollup.RolledBack,
            Unchecked => state == MergeRollup.NotChecked,
            NoPrs => state == MergeRollup.NoPullRequests,
            _ => true
        };
    }
}
