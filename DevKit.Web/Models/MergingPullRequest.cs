namespace DevKit.Web.Models;

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

    /// <summary>The target branch commit the pull request merged onto: the "before" side of its change.</summary>
    public string LastMergeTargetCommitId { get; set; } = "";

    public List<MergingCommit> Commits { get; set; } = new();

    /// <summary>
    /// Why the commits could not be read, from <c>TfsErrors.Describe</c>; null once they were. Unread commits
    /// are not "no commits": while this is set the commit check leaves the pull request unchecked, and its row
    /// cannot roll up as merged.
    /// </summary>
    public string? CommitsError { get; set; }

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
    /// Replaces the code check only if it is still <paramref name="expected"/>. This is how a comparison run
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
    /// otherwise the commit check. Commits can be on a branch whose code is not (a conflict resolved
    /// by dropping lines), so the code, once compared, is what decides.
    /// </summary>
    public bool? EffectiveMerged => CodeCheck is { IsConclusive: true } code
        ? code.Verdict == CodeVerdict.Present
        : IsMergedToTargetBranch;

    /// <summary>
    /// The commit check found every commit on the branch, yet the code comparison found code missing:
    /// the case a commit check alone reports as merged.
    /// </summary>
    public bool CodeContradictsCommits =>
        IsMergedToTargetBranch == true && CodeCheck?.Verdict is CodeVerdict.Missing or CodeVerdict.Partial;

    public bool IsCompleted => string.Equals(Status, "completed", StringComparison.OrdinalIgnoreCase);

    public bool IsAbandoned => string.Equals(Status, "abandoned", StringComparison.OrdinalIgnoreCase);

    /// <summary>When work on this PR actually started: earliest commit, else the creation date.</summary>
    public DateTime? FirstActivityDate
    {
        get
        {
            // Min skips commits without a date and is null when none has one.
            var first = Commits.Select(c => c.Date).Min();
            return first ?? CreationDate;
        }
    }

    /// <summary>Commits that must reach the target branch. Merge-tracking commits never block.</summary>
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
    /// How git opens a merge commit that belongs to the source branch's own history, either
    /// syncing from a remote ("Merge remote-tracking branch 'origin/main' into feature/x") or
    /// merging another branch in locally ("Merge branch 'main' into feature/x"). Neither is a
    /// change the developer wrote, and neither can be cherry-picked to a release branch on its
    /// own, so both are optional. Ordered longest-first is not required: the two openings are
    /// distinct, since a remote-tracking message does not begin with "Merge branch".
    /// </summary>
    public static readonly string[] MergeCommitPrefixes =
    {
        "Merge remote-tracking branch",
        "Merge branch"
    };

    private const int ShortIdLength = 8;

    /// <summary>
    /// Longest team code ("FXCPMGR" in "FXCPMGR : ...") a commit subject may open with before the words
    /// <see cref="IsRebaseCommit"/> and <see cref="IsMergeFromCommit"/> look for.
    /// </summary>
    private const int MaxPrefixLength = 30;

    /// <summary>Between the parts of the team's "TEAM : id : title" form.</summary>
    private const char PrefixSeparator = ':';

    /// <summary>How a rebase commit's subject starts, after any team prefix.</summary>
    private static readonly HashSet<string> RebaseWords = new(StringComparer.OrdinalIgnoreCase) { "rebase", "rebased", "rebasing" };

    private const string MergeFromWords = "Merge from";
    private const string CompletedPullRequestOpening = "Merged PR ";

    public string CommitId { get; set; } = "";
    public string Message { get; set; } = "";
    public string Author { get; set; } = "";
    public DateTime? Date { get; set; }
    public string Url { get; set; } = "";

    /// <summary>
    /// The author's email and time, which git keeps through a cherry-pick and a rebase, so a copy
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
    /// own: "Merge remote-tracking branch 'origin/main' into feature/x" when syncing a remote,
    /// "Merge branch 'main' into feature/x" when merging locally. Each is an artifact of the
    /// source branch's history rather than work of its own, and neither can be cherry-picked to
    /// a release branch independently, so requiring them would report such a pull request
    /// unmerged forever. They are treated as optional instead. Missing this would be the single largest
    /// source of false negatives. Only the subject as written counts, the same opening
    /// <see cref="IsMergeFromCommit"/> starts from.
    /// </summary>
    public bool IsBranchMergeCommit => OpensWithGitMerge(Subject());

    /// <summary>
    /// True for a commit made while rebasing the branch onto another, such as "FXCPMGR : Rebase branch" or "Rebase
    /// Conflict Resolve": its first line starts with the word rebase, as written or after the team's code and a colon
    /// ("FXCPMGR : "). An ordinary word before a colon ("fix: rebase of the import") is not a team code, and a subject
    /// that only mentions a rebase further on ("Fix rebase of the import") is the developer's own work: both stay. So
    /// does "FXCPMGR : 1001 : Rebase button", a work item whose title opens with the word.
    /// Copy details leaves a rebase commit out; verification still requires it (see <see cref="IsMergeFromCommit"/>).
    /// </summary>
    public bool IsRebaseCommit
    {
        get
        {
            var subject = Subject();
            return RebaseWords.Contains(FirstWord(subject)) || RebaseWords.Contains(FirstWord(AfterTeamCode(subject)));
        }
    }

    /// <summary>
    /// True for a commit that brought another branch in, which the sheet's copied Commit IDs leave out: git's own merge
    /// subjects, a hand-written "Merge from ...", each as written, after the team's code ("FXCPMGR : Merge branch
    /// 'main'") or after the team's "TEAM : id :" form ("FXCPMGR : 92599 : Merge from QA"), and "Merged PR 123: ...",
    /// the commit another pull request left on a branch that was then merged in. Only the first line is read.
    /// Verification (<see cref="IsBranchMergeCommit"/>) still requires every one of these but git's own merges, because
    /// the change such a commit carries has to reach the target branch either way; the sheet leaves it out because it
    /// is not this pull request's work, and the merger brings it in with the pull request that made it.
    /// </summary>
    public bool IsMergeFromCommit
    {
        get
        {
            var subject = Subject();
            var afterTeam = AfterTeamCode(subject);
            return BringsBranchIn(subject) || BringsBranchIn(afterTeam) || BringsBranchIn(AfterWorkItemId(afterTeam));
        }
    }

    /// <summary>The first line of the message: git's subject. A colon on a later line says nothing about the commit.</summary>
    private string Subject()
    {
        var message = Message.TrimStart();
        var lineEnd = message.IndexOfAny(['\r', '\n']);
        return lineEnd < 0 ? message : message[..lineEnd];
    }

    private static bool BringsBranchIn(string subject) =>
        OpensWithGitMerge(subject) || OpensWithWords(subject, MergeFromWords) || IsCompletedPullRequestSubject(subject);

    private static bool OpensWithGitMerge(string subject) =>
        MergeCommitPrefixes.Any(prefix => OpensWithWords(subject, prefix));

    /// <summary>
    /// The subject after the team's code and colon ("FXCPMGR : Rebase" gives "Rebase"); empty when it does not open
    /// with one. A team code is what <c>BranchNaming.TeamSegment</c> writes: upper-case letters and digits, with at
    /// least one letter, then a space and the colon, as in "TEAM : id : title". That leaves out the lower- or
    /// mixed-case types of a conventional commit ("fix:", "Bug:") and an upper-case one written without the space
    /// ("UI:"), so the developer's own work is never mistaken for a merge or a rebase.
    /// </summary>
    private static string AfterTeamCode(string subject)
    {
        var colon = subject.IndexOf(PrefixSeparator);
        if (colon <= 1 || colon > MaxPrefixLength + 1 || !char.IsWhiteSpace(subject[colon - 1]))
        {
            return "";
        }

        var code = subject[..colon].TrimEnd();
        return code.All(c => char.IsAsciiLetterUpper(c) || char.IsAsciiDigit(c)) && code.Any(char.IsAsciiLetterUpper)
            ? subject[(colon + 1)..].TrimStart()
            : "";
    }

    /// <summary>The subject after a work item id and colon ("92599 : Merge from QA" gives "Merge from QA"); empty otherwise.</summary>
    private static string AfterWorkItemId(string subject)
    {
        var digits = subject.TakeWhile(char.IsAsciiDigit).Count();
        if (digits == 0)
        {
            return "";
        }

        var rest = subject[digits..].TrimStart();
        return rest.Length > 0 && rest[0] == PrefixSeparator ? rest[1..].TrimStart() : "";
    }

    private static string FirstWord(string text) => new(text.TakeWhile(char.IsLetter).ToArray());

    // "Merge from dev" but not "Merge fromDate fields", and "Merge branch 'main'" but not "Merge branches dropdown":
    // the words end where the subject does, or at a space.
    private static bool OpensWithWords(string subject, string words) =>
        subject.StartsWith(words, StringComparison.OrdinalIgnoreCase)
        && (subject.Length == words.Length || char.IsWhiteSpace(subject[words.Length]));

    // "Merged PR 92599: ...", as TFS titles the commit a completed pull request leaves on its target.
    private static bool IsCompletedPullRequestSubject(string subject)
    {
        if (!subject.StartsWith(CompletedPullRequestOpening, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var digits = subject.Skip(CompletedPullRequestOpening.Length).TakeWhile(char.IsAsciiDigit).Count();
        var colon = CompletedPullRequestOpening.Length + digits;
        return digits > 0 && colon < subject.Length && subject[colon] == ':';
    }
}

/// <summary>How the commit check placed a commit on the branch, or why it did not.</summary>
public enum CommitMatch
{
    None,

    /// <summary>The same commit, merged rather than copied.</summary>
    Sha,

    /// <summary>A copy made with <c>git cherry-pick -x</c>, whose message names this commit.</summary>
    CherryPick,

    /// <summary>A copy with the same author and author time: a cherry-pick or rebase, even reworded.</summary>
    Author,

    /// <summary>A commit with the same, distinctive message.</summary>
    Message,

    /// <summary>
    /// Only a commit with the same message was found, and that message is too short or too common to
    /// prove anything, so it is not counted.
    /// </summary>
    AmbiguousMessage
}
