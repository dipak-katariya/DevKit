namespace DevKit.Web.Models;

/// <summary>
/// One load of the Code Merging Sheet: the requirement rows plus the branch they were
/// last verified against. Failures are carried as <see cref="ErrorMessage"/> rather than
/// thrown, so a sprint that partially fails to load still renders what it did get.
/// </summary>
public class CodeMergingBundle
{
    public List<RequirementMergingRow> Requirements { get; set; } = new();

    /// <summary>
    /// Merging target branch each repository was verified against, keyed by repository id.
    /// Empty until a verification runs. Per repository rather than one branch for the sheet,
    /// because a release branch is named per repository and moves per sprint.
    /// </summary>
    public Dictionary<string, string> VerifiedBranchByRepoId { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Null until something has been verified; drives every "is this verified" check.</summary>
    public string? TargetBranchName => VerifiedBranchSummary;

    /// <summary>
    /// The branch name when every repository was verified against the same one, otherwise a
    /// comma-separated list, so the accuracy note names what was actually checked.
    /// </summary>
    public string? VerifiedBranchSummary
    {
        get
        {
            var names = VerifiedBranchByRepoId.Values
                .Where(v => !string.IsNullOrWhiteSpace(v))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(v => v, StringComparer.OrdinalIgnoreCase)
                .ToList();
            return names.Count == 0 ? null : string.Join(", ", names);
        }
    }

    public string? ErrorMessage { get; set; }

    /// <summary>Rows whose commits were deferred, so the page can offer to load the rest.</summary>
    public int DeferredRowCount { get; set; }

    /// <summary>Set when a fetch cap truncated the result, so the UI can say what was left out.</summary>
    public string? TruncationNotice { get; set; }

    public IEnumerable<MergingPullRequest> AllPullRequests => Requirements.SelectMany(r => r.PullRequests);

    /// <summary>What the last code comparison did; null until one has run on this load.</summary>
    public CodeCompareRunSummary? LastCodeCompare { get; set; }

    /// <summary>Branch commits actually read during the last verification, across repositories.</summary>
    public int ScannedCommitCount { get; set; }

    /// <summary>True when a repository's scan stopped at the cap rather than at the branch's start.</summary>
    public bool CommitWindowCapped { get; set; }

    /// <summary>Earliest point any repository's scan reached. Null when nothing bounded it.</summary>
    public DateTime? ScannedFrom { get; set; }
}

/// <summary>One work item and every pull request discovered for it.</summary>
public class RequirementMergingRow
{
    public int WorkItemId { get; set; }
    public string Title { get; set; } = "";
    public string State { get; set; } = "";
    public string AssignedTo { get; set; } = "-";
    public string WorkItemType { get; set; } = "";
    public string WorkItemUrl { get; set; } = "";
    public List<MergingPullRequest> PullRequests { get; set; } = new();

    /// <summary>
    /// Pull requests this work item links to that did not target their repository's QA branch —
    /// typically an intermediate feature-branch pull request. Kept rather than discarded so the
    /// sheet can show them on demand for cross-checking, but held out of the counts and merge
    /// verdicts by default, which are about code reaching the release branch.
    /// </summary>
    public List<MergingPullRequest> OffQaBranchPullRequests { get; set; } = new();

    public int PrsOffQaBranch => OffQaBranchPullRequests.Count;

    /// <summary>Everything found for this work item, ordered the same way as the QA-branch set.</summary>
    public List<MergingPullRequest> AllPullRequests => PullRequests
        .Concat(OffQaBranchPullRequests)
        .OrderBy(pr => pr.FirstActivityDate ?? DateTime.MaxValue)
        .ThenBy(pr => pr.PullRequestId)
        .ToList();

    /// <summary>UI-only: whether this row's pull-request table is open.</summary>
    public bool IsExpanded { get; set; }

    /// <summary>UI-only: a single-row re-verification is in flight.</summary>
    public bool IsVerifying { get; set; }

    /// <summary>
    /// Whether this row's commits have been read. Only the first rows are loaded up front;
    /// the rest fill in on expand, so a large sprint does not pay for every commit of every
    /// work item before anything is on screen. Counts and merge state stay blank until then
    /// rather than showing a zero that would read as "no commits".
    /// </summary>
    public bool CommitsLoaded { get; set; }

    /// <summary>UI-only: this row's commits are being fetched right now.</summary>
    public bool IsLoadingCommits { get; set; }
}

/// <summary>
/// Pull requests raised recently that carry no work-item link at all, which is why they never
/// appear on the sheet: nothing ties them to a Requirement, Change Request or Bug.
/// </summary>
public class UnlinkedPrBundle
{
    public List<MergingPullRequest> PullRequests { get; set; } = new();

    /// <summary>Pull requests that targeted a QA branch and so had their links read.</summary>
    public int ScannedPrCount { get; set; }

    /// <summary>All pull requests raised in the window, before the QA-branch filter.</summary>
    public int RaisedPrCount { get; set; }

    /// <summary>
    /// Pull requests with no formal link that still name a work item in their branch or title.
    /// Held back because the sheet finds those by that id, so they are not invisible.
    /// </summary>
    public int ReferencedByIdCount { get; set; }

    /// <summary>QA branch each repository was filtered by, for display. Blank means all branches.</summary>
    public Dictionary<string, string> QaBranchByRepo { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public DateTime? SearchedFrom { get; set; }

    public string? ErrorMessage { get; set; }
    public string? TruncationNotice { get; set; }

    public bool HasRun { get; set; }

    /// <summary>Grouped for display, newest pull request first within each repository.</summary>
    public IEnumerable<IGrouping<string, MergingPullRequest>> ByRepository =>
        PullRequests
            .OrderByDescending(p => p.CreationDate ?? DateTime.MinValue)
            .GroupBy(p => p.RepositoryName, StringComparer.OrdinalIgnoreCase)
            .OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase);
}

/// <summary>A pull request linked to a requirement, with the commits it carries.</summary>
public class MergingPullRequest
{
    public int PullRequestId { get; set; }
    public string Title { get; set; } = "";
    public string Project { get; set; } = "";
    public string RepositoryId { get; set; } = "";
    public string RepositoryName { get; set; } = "";
    public string SourceBranch { get; set; } = "";
    public string TargetBranch { get; set; } = "";
    public string SourceBranchUrl { get; set; } = "";
    public string TargetBranchUrl { get; set; } = "";
    public string Status { get; set; } = "";
    public string CreatedBy { get; set; } = "";
    public DateTime? CreationDate { get; set; }
    public DateTime? ClosedDate { get; set; }
    public string Url { get; set; } = "";

    /// <summary>
    /// Merge commit on the target branch: the fallback check when no commits load, and the "after"
    /// side of the pull request's own change for the code comparison.
    /// </summary>
    public string LastMergeCommitId { get; set; } = "";
    public string LastMergeSourceCommitId { get; set; } = "";

    /// <summary>The target branch commit the pull request merged onto — the "before" side of its change.</summary>
    public string LastMergeTargetCommitId { get; set; } = "";

    public List<MergingCommit> Commits { get; set; } = new();

    /// <summary>The commit check's verdict: whether each commit's SHA, cherry-pick or message is on the branch.</summary>
    public bool? IsMergedToTargetBranch { get; set; }

    /// <summary>
    /// The code comparison's verdict; null until one has run for this pull request. Written from the
    /// comparison's worker threads as well as the page, so every write takes the same lock.
    /// </summary>
    public PrCodeCheck? CodeCheck
    {
        get => Volatile.Read(ref _codeCheck);
        set
        {
            lock (CodeCheckGate)
            {
                _codeCheck = value;
            }
        }
    }

    private static readonly object CodeCheckGate = new();
    private PrCodeCheck? _codeCheck;

    /// <summary>
    /// Replaces the code check only if it is still <paramref name="expected"/> — how a comparison run
    /// writes back without overwriting a newer run's result, a reset, or line detail added meanwhile.
    /// </summary>
    public bool TryReplaceCodeCheck(PrCodeCheck? expected, PrCodeCheck? replacement)
    {
        lock (CodeCheckGate)
        {
            if (!ReferenceEquals(_codeCheck, expected))
            {
                return false;
            }
            _codeCheck = replacement;
            return true;
        }
    }

    /// <summary>
    /// What the sheet counts this pull request as: the code comparison when it reached an answer,
    /// otherwise the commit check. Commits can be on a branch whose code is not — a conflict resolved
    /// by dropping lines — so the code, once compared, is what decides.
    /// </summary>
    public bool? EffectiveMerged => CodeCheck is { IsConclusive: true } code
        ? code.Verdict == CodeVerdict.Present
        : IsMergedToTargetBranch;

    /// <summary>
    /// The commit check found every commit on the branch, yet the code comparison found code missing —
    /// the case a commit check alone reports as merged.
    /// </summary>
    public bool CodeContradictsCommits =>
        IsMergedToTargetBranch == true && CodeCheck?.Verdict is CodeVerdict.Missing or CodeVerdict.Partial;

    public bool IsCompleted => string.Equals(Status, "completed", StringComparison.OrdinalIgnoreCase);

    /// <summary>When work on this PR actually started: earliest commit, else the creation date.</summary>
    public DateTime? FirstActivityDate
    {
        get
        {
            DateTime? first = null;
            foreach (var c in Commits)
                if (c.Date.HasValue && (first is null || c.Date < first)) first = c.Date;
            return first ?? CreationDate;
        }
    }

    /// <summary>Commits that must reach the target branch — merge-tracking commits never block.</summary>
    public IEnumerable<MergingCommit> RequiredCommits => Commits.Where(c => !c.IsBranchMergeCommit);

    public int MissingCommitCount => RequiredCommits.Count(c => c.IsMergedToTargetBranch == false);

    /// <summary>Commits that reached the branch and were taken back off it again.</summary>
    public int RolledBackCommitCount => RequiredCommits.Count(c => c.IsRolledBack);

    /// <summary>True when the only thing stopping this pull request is rolled-back code.</summary>
    public bool IsRolledBack =>
        IsMergedToTargetBranch == false && RolledBackCommitCount > 0
        && RolledBackCommitCount == MissingCommitCount;
}

/// <summary>A single commit inside a pull request.</summary>
public class MergingCommit
{
    /// <summary>
    /// How git opens a merge commit that belongs to the source branch's own history — either
    /// syncing from a remote ("Merge remote-tracking branch 'origin/main' into feature/x") or
    /// merging another branch in locally ("Merge branch 'main' into feature/x"). Neither is a
    /// change the developer wrote, and neither can be cherry-picked to a release branch on its
    /// own, so both are optional. Ordered longest-first is not required — the two openings are
    /// distinct, since a remote-tracking message does not begin with "Merge branch".
    /// </summary>
    public static readonly string[] MergeCommitPrefixes =
    {
        "Merge remote-tracking branch",
        "Merge branch"
    };

    private const int ShortIdLength = 8;

    public string CommitId { get; set; } = "";
    public string Message { get; set; } = "";
    public string Author { get; set; } = "";
    public DateTime? Date { get; set; }
    public string Url { get; set; } = "";

    /// <summary>
    /// The author's email and time, which git keeps through a cherry-pick and a rebase — so a copy
    /// with a new SHA, or a message changed while fixing a conflict, still matches the original.
    /// </summary>
    public string AuthorEmail { get; set; } = "";
    public DateTime? AuthorDate { get; set; }

    public bool? IsMergedToTargetBranch { get; set; }

    /// <summary>What the commit check matched this commit by, for the tooltip.</summary>
    public CommitMatch MatchKind { get; set; }

    /// <summary>
    /// Set when the commit reached the branch and was later taken back off it. Distinct from
    /// simply not merged: the code was there, so this is a regression to chase rather than
    /// work still to do. Rolled-back commits also report <see cref="IsMergedToTargetBranch"/>
    /// as false, so anything that only reads that keeps working.
    /// </summary>
    public bool IsRolledBack { get; set; }

    /// <summary>How the rollback was established, for the tooltip. Empty when not rolled back.</summary>
    public string RollbackReason { get; set; } = "";

    public string ShortId => CommitId.Length > ShortIdLength ? CommitId[..ShortIdLength] : CommitId;

    /// <summary>
    /// True for the merge commits git creates when a developer brings another branch into their
    /// own — "Merge remote-tracking branch 'origin/main' into feature/x" when syncing a remote,
    /// "Merge branch 'main' into feature/x" when merging locally. Each is an artifact of the
    /// source branch's history rather than work of its own, and neither can be cherry-picked to
    /// a release branch independently, so requiring them would report such a pull request
    /// unmerged forever. They are treated as optional instead — the single largest source of
    /// false negatives if this is missed.
    /// </summary>
    public bool IsBranchMergeCommit
    {
        get
        {
            var message = Message.TrimStart();
            return MergeCommitPrefixes.Any(p => message.StartsWith(p, StringComparison.OrdinalIgnoreCase));
        }
    }
}

/// <summary>How the commit check placed a commit on the branch — or why it did not.</summary>
public enum CommitMatch
{
    None,

    /// <summary>The same commit, merged rather than copied.</summary>
    Sha,

    /// <summary>A copy made with <c>git cherry-pick -x</c>, whose message names this commit.</summary>
    CherryPick,

    /// <summary>A copy with the same author and author time — a cherry-pick or rebase, even reworded.</summary>
    Author,

    /// <summary>A commit with the same, distinctive message.</summary>
    Message,

    /// <summary>
    /// Only a commit with the same message was found, and that message is too short or too common to
    /// prove anything, so it is not counted.
    /// </summary>
    AmbiguousMessage
}

/// <summary>Everything needed for one Code Merging Sheet load.</summary>
/// <param name="QaBranchByRepoId">
/// Per-repository branch that pull requests must target to appear in the sheet, keyed by
/// repository id. A repository absent from the map, or mapped to blank, keeps all of its
/// pull requests.
/// </param>
public sealed record CodeMergingQuery(
    string Project,
    string AreaPath,
    IReadOnlyList<string> IterationPaths,
    IReadOnlyList<TfsRepo> Repositories,
    bool DeliverableOnly,
    IReadOnlyDictionary<string, string> QaBranchByRepoId,
    bool MatchByBranchOrTitle,
    int EagerCommitRows = CodeMergingDefaults.EagerCommitRows)
{
    /// <summary>For log and error messages.</summary>
    public string IterationSummary => string.Join(", ", IterationPaths);
}

public static class CodeMergingDefaults
{
    /// <summary>
    /// How many rows have their commits read during the initial load. The rest fill in when
    /// opened, which keeps a large sprint responsive without hiding anything.
    /// </summary>
    public const int EagerCommitRows = 7;
}
