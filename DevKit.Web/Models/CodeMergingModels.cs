namespace DevKit.Web.Models;

/// <summary>
/// One load of the Code Merging Sheet: the requirement rows plus the branch they were
/// last verified against. Failures are carried as <see cref="ErrorMessage"/> rather than
/// thrown, so a sprint that partially fails to load still renders what it did get.
/// </summary>
public class CodeMergingBundle
{
    private static long _lastGeneration;

    private readonly object _resultsGate = new();
    private long _verificationGeneration = NextGeneration();

    public List<RequirementMergingRow> Requirements { get; set; } = new();

    /// <summary>
    /// Which verification the verdicts on this sheet belong to. A reset moves it on, and a new sheet starts
    /// past every earlier one, so a check or comparison that notes it when it starts can tell when it
    /// finishes that its results describe settings or a sheet that no longer apply. Runs start on the
    /// page's synchronization context, so noting the generation and a run's first writes are one step.
    /// </summary>
    public long VerificationGeneration => Interlocked.Read(ref _verificationGeneration);

    /// <summary>
    /// Moves the sheet to a new generation and runs <paramref name="reset"/> under the lock
    /// <see cref="TryWriteResults"/> takes, so no run can write between the two.
    /// </summary>
    public void AdvanceVerificationGeneration(Action reset)
    {
        ArgumentNullException.ThrowIfNull(reset);
        lock (_resultsGate)
        {
            Interlocked.Exchange(ref _verificationGeneration, NextGeneration());
            reset();
        }
    }

    /// <summary>
    /// Runs <paramref name="write"/> only while the sheet is still at <paramref name="generation"/>.
    /// False, with nothing written, once a reset or a new load has moved it on.
    /// </summary>
    public bool TryWriteResults(long generation, Action write)
    {
        ArgumentNullException.ThrowIfNull(write);
        lock (_resultsGate)
        {
            if (VerificationGeneration != generation)
            {
                return false;
            }
            write();
            return true;
        }
    }

    private static long NextGeneration() => Interlocked.Increment(ref _lastGeneration);

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

    /// <summary>
    /// Every read that failed while finding pull requests. Each may have cost work items some of their
    /// pull requests, so the rows it touched also carry it in <see cref="RequirementMergingRow.DiscoveryGaps"/>.
    /// </summary>
    public List<DiscoveryWarning> DiscoveryWarnings { get; set; } = new();

    /// <summary>
    /// Work items the query matched that TFS did not return, so they have no row. Each is also one of the
    /// <see cref="DiscoveryWarnings"/>, which is how the page lists them.
    /// </summary>
    public List<int> UnreadWorkItemIds { get; set; } = new();

    /// <summary>
    /// Every row's QA-branch pull requests. The off-QA ones are not included: anything that must reach
    /// every pull request goes through each row's <see cref="RequirementMergingRow.AllPullRequests"/>.
    /// </summary>
    public IEnumerable<MergingPullRequest> AllPullRequests => Requirements.SelectMany(r => r.PullRequests);

    /// <summary>What the last code comparison did; null until one has run on this load.</summary>
    public CodeCompareRunSummary? LastCodeCompare { get; set; }

    /// <summary>
    /// Branch commits read by the last Verify All, in the busiest repository. This and the two figures below
    /// describe that sheet-wide scan; a one-row re-check leaves them alone.
    /// </summary>
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
    /// Pull requests this work item links to that did not target their repository's QA branch,
    /// typically an intermediate feature-branch pull request. Kept rather than discarded so the
    /// sheet can show them on demand for cross-checking, but held out of the counts and merge
    /// verdicts by default, which are about code reaching the release branch.
    /// </summary>
    public List<MergingPullRequest> OffQaBranchPullRequests { get; set; } = new();

    public int PrsOffQaBranch => OffQaBranchPullRequests.Count;

    /// <summary>
    /// Failed reads that could have found more pull requests for this work item. While any is recorded,
    /// what was found cannot vouch for the rest, so the row never rolls up as merged.
    /// </summary>
    public List<DiscoveryWarning> DiscoveryGaps { get; set; } = new();

    public bool DiscoveryIncomplete => DiscoveryGaps.Count > 0;

    /// <summary>Everything found for this work item, ordered the same way as the QA-branch set.</summary>
    public List<MergingPullRequest> AllPullRequests() => PullRequests
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
    /// rather than showing a zero that would read as "no commits". A row whose read failed stays
    /// unloaded, so opening it, Verify All and Load all read it again.
    /// </summary>
    public bool CommitsLoaded { get; set; }

    /// <summary>The first reason a commit read of this row's pull requests failed; null when none did.</summary>
    public string? CommitsError => PullRequests.Concat(OffQaBranchPullRequests)
        .Select(pr => pr.CommitsError)
        .FirstOrDefault(error => error is not null);

    /// <summary>UI-only: this row's commits are being fetched right now.</summary>
    public bool IsLoadingCommits { get; set; }
}

/// <summary>A discovery read that failed: what it was for, and what went wrong in words the page can show.</summary>
/// <param name="Subject">
/// The read, naming its repository or work item: "Pull request list of Web", "Links of work item #1234",
/// "Pull request !56 linked from #1234".
/// </param>
/// <param name="Reason">What went wrong, from <c>TfsErrors.Describe</c>; the full exception is in the log.</param>
public sealed record DiscoveryWarning(string Subject, string Reason)
{
    public string Text => $"{Subject}: {Reason}";
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

    /// <summary>
    /// Pull-request lists and link checks that failed. A failed list hides its pull requests, and a failed
    /// check counts its pull request as linked, so while any is here "every pull request is linked" is not proven.
    /// </summary>
    public List<DiscoveryWarning> ReadFailures { get; set; } = new();

    public bool HasRun { get; set; }

    /// <summary>Grouped for display, newest pull request first within each repository.</summary>
    public IEnumerable<IGrouping<string, MergingPullRequest>> ByRepository =>
        PullRequests
            .OrderByDescending(p => p.CreationDate ?? DateTime.MinValue)
            .GroupBy(p => p.RepositoryName, StringComparer.OrdinalIgnoreCase)
            .OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase);
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

/// <summary>
/// What the Code Merging Sheet is busy with, which decides what its buttons may start. Kept out of the
/// page so the rules can be tested without rendering it.
/// </summary>
public readonly record struct MergingSheetActivity(bool Loading, bool Verifying, bool LoadingRest, bool ComparingCode)
{
    /// <summary>Verify All, the read of the remaining commits, or a code comparison is running.</summary>
    public bool IsChecking => Verifying || LoadingRest || ComparingCode;

    /// <summary>
    /// Nothing is loading or being checked. Load replaces the sheet, so it waits for every check: one still
    /// running would carry on against a sheet whose commits it never read, which can read Merged when it
    /// is not.
    /// </summary>
    public bool IsIdle => !Loading && !IsChecking;
}

public static class CodeMergingDefaults
{
    /// <summary>
    /// How many rows have their commits read during the initial load. The rest fill in when
    /// opened, which keeps a large sprint responsive without hiding anything.
    /// </summary>
    public const int EagerCommitRows = 7;
}
