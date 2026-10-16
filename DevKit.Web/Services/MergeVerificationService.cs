using DevKit.Web.Models;

namespace DevKit.Web.Services;

/// <summary>
/// The commit check: did this pull request's commits reach the release branch?
///
/// Code often reaches a release branch by cherry-pick rather than by merge, which produces a different
/// SHA for the same change. The target branch's history is therefore indexed by SHA, by the author's
/// email and time (kept through a cherry-pick, even a reworded one) and by distinctive messages (see
/// <see cref="BranchHistoryIndex"/>).
///
/// Commits being on a branch does not prove their code is: a conflict resolved by dropping lines leaves
/// every commit in place. <see cref="CodeCompareService"/> answers that; this check is the fast first pass.
/// Two limits are surfaced in the UI: only the most recent <see cref="TargetBranchCommitWindow"/> commits
/// are read (still-missing commits past it are then asked about directly), and a commit whose only match
/// is a common message is not counted.
/// </summary>
public partial class MergeVerificationService
{
    private const int TargetBranchCommitWindow = 20_000;

    /// <summary>The commits API caps a page at 1000, so the window is read in pages.</summary>
    private const int CommitPageSize = 1000;

    /// <summary>
    /// Direct "is this commit on the branch" questions asked after a capped scan, at most. Each is one
    /// request, so the bound keeps a huge branch from turning one click into thousands.
    /// </summary>
    private const int MaxBeyondWindowProbes = 200;

    /// <summary>
    /// How far before a sprint starts the scan still looks, used only when the pull requests
    /// carry no commit dates of their own. Work can begin on a branch before the sprint
    /// formally opens, so the floor is deliberately loose.
    /// </summary>
    private static readonly TimeSpan SprintLookbackBuffer = TimeSpan.FromDays(120);

    /// <summary>
    /// Slack on the earliest-commit floor, covering author/commit date skew and rebases that
    /// can place a commit on the branch fractionally before its recorded author date.
    /// </summary>
    private static readonly TimeSpan CommitDateSkewBuffer = TimeSpan.FromDays(7);

    private readonly TfsRequestClient _req;
    private readonly ILogger<MergeVerificationService> _logger;

    public MergeVerificationService(TfsRequestClient req, ILogger<MergeVerificationService> logger)
    {
        _req = req;
        _logger = logger;
    }

    /// <summary>Over a client of the caller's own, as tests use it; DI only sees the public constructor.</summary>
    internal MergeVerificationService(HttpClient http, SettingsService settings, ILogger<MergeVerificationService> logger)
        : this(new TfsRequestClient(http, settings), logger)
    {
    }

    /// <summary>Most commits looked up one by one after a capped scan, for the accuracy note.</summary>
    public static int BeyondWindowProbeLimit => MaxBeyondWindowProbes;

    /// <summary>Number of target-branch commits examined per repository.</summary>
    public static int CommitWindow => TargetBranchCommitWindow;

    /// <summary>
    /// Verifies every completed pull request in the bundle, each against the merging branch
    /// configured for its own repository. Repositories with no branch configured are skipped
    /// rather than verified against someone else's branch. Sets the sheet-wide scan figures the
    /// accuracy note quotes.
    /// </summary>
    public async Task VerifyAsync(
        CodeMergingBundle bundle, IReadOnlyDictionary<string, string> mergingBranchByRepoId,
        DateTime? since = null, CancellationToken ct = default)
    {
        var check = new CommitCheck(bundle, bundle.AllPullRequests.ToList(), mergingBranchByRepoId, since);
        var indexes = await VerifyPullRequestsAsync(check, ct);
        if (indexes is not null)
        {
            bundle.TryWriteResults(check.Generation, () => RecordScanFigures(bundle, indexes));
        }
    }

    /// <summary>
    /// Re-verifies a single requirement. Lets someone who has just pushed a cherry-pick
    /// refresh one row instead of re-verifying the whole sprint. The sheet-wide scan figures stay as
    /// Verify All set them: they describe that scan, and a row's re-check reads only its own part of
    /// the history. Written over, they dropped the cap warning and quoted one row's date for the sheet.
    /// </summary>
    public async Task VerifyRequirementAsync(
        CodeMergingBundle bundle, int workItemId,
        IReadOnlyDictionary<string, string> mergingBranchByRepoId,
        DateTime? since = null, CancellationToken ct = default)
    {
        var row = bundle.Requirements.FirstOrDefault(r => r.WorkItemId == workItemId);
        if (row is not null)
        {
            await VerifyPullRequestsAsync(new CommitCheck(bundle, row.PullRequests.ToList(), mergingBranchByRepoId, since), ct);
        }
    }

    /// <summary>
    /// Drops all verification state (commit and code alike, scan figures included) from every pull
    /// request found for every row, whether or not it targeted QA, so the Merged column disappears cleanly.
    /// It also moves the sheet to a new generation, so a commit check still running writes nothing; a code
    /// comparison still running cannot either, because its in-progress marker is cleared with the rest.
    /// </summary>
    public static void ClearVerification(CodeMergingBundle bundle) =>
        bundle.AdvanceVerificationGeneration(() =>
        {
            bundle.VerifiedBranchByRepoId.Clear();
            bundle.LastCodeCompare = null;
            bundle.ScannedCommitCount = 0;
            bundle.CommitWindowCapped = false;
            bundle.ScannedFrom = null;
            foreach (var pr in bundle.Requirements.SelectMany(r => r.AllPullRequests))
            {
                ClearPullRequest(pr);
            }
        });

    private static void ClearPullRequest(MergingPullRequest pr)
    {
        pr.IsMergedToTargetBranch = null;
        pr.CodeCheck = null;
        foreach (var c in pr.Commits)
        {
            c.IsMergedToTargetBranch = null;
            c.IsRolledBack = false;
            c.RollbackReason = "";
            c.MatchKind = CommitMatch.None;
        }
    }

    /// <summary>
    /// Judges the candidates and writes their verdicts in one step, only if the sheet is still at the
    /// generation the check started in: one reset or replaced meanwhile gets nothing, not even part of a
    /// verdict. Returns the branch histories read, or null when nothing was checked or written.
    /// </summary>
    private async Task<IReadOnlyCollection<BranchHistoryIndex>?> VerifyPullRequestsAsync(CommitCheck check, CancellationToken ct)
    {
        var (bundle, branches) = (check.Bundle, check.Branches);
        if (branches.Count == 0)
        {
            return null;
        }

        var completed = check.Candidates
            .Where(p => p.IsCompleted && BranchFor(branches, p.RepositoryId).Length > 0)
            .Distinct()
            .ToList();
        if (completed.Count == 0)
        {
            return null;
        }

        // Written before the first await, so still inside the generation the check started in.
        foreach (var repoId in completed.Select(pr => pr.RepositoryId))
        {
            bundle.VerifiedBranchByRepoId[repoId] = BranchFor(branches, repoId);
        }

        var targetKeys = await LoadTargetKeysAsync(completed, branches, check.Since, ct);
        MarkSharedMessages(bundle, targetKeys.Values);
        var verdicts = await Task.WhenAll(completed.Select(pr =>
            EvaluateAsync(pr, BranchFor(branches, pr.RepositoryId), targetKeys, ct)));
        await ProbeBeyondWindowAsync(verdicts, branches, targetKeys, ct);

        var written = bundle.TryWriteResults(check.Generation, () =>
        {
            foreach (var verdict in verdicts)
            {
                verdict.Write();
            }
            WarnAboutUnreadHistory(bundle, completed, targetKeys);
        });
        if (!written)
        {
            _logger.LogInformation(
                "Commit check of {PullRequests} pull requests discarded: the sheet was reset or reloaded while it ran",
                completed.Count);
            return null;
        }
        return targetKeys.Values;
    }

    /// <summary>
    /// The note quotes what was actually read, which varies by how much history the branch has in
    /// range. Claiming a fixed number would overstate a short branch.
    /// </summary>
    private static void RecordScanFigures(CodeMergingBundle bundle, IReadOnlyCollection<BranchHistoryIndex> indexes)
    {
        bundle.ScannedCommitCount = indexes.Count > 0 ? indexes.Max(i => i.ScannedCommits) : 0;
        bundle.CommitWindowCapped = indexes.Any(i => i.HitWindowCap);
        bundle.ScannedFrom = indexes.Min(i => i.ScannedFrom);
    }

    /// <summary>
    /// A repository whose branch history could not be read is checked one merge commit at a time, which
    /// is far weaker. Said on the sheet, so a clean result there is not taken at face value.
    /// </summary>
    private static void WarnAboutUnreadHistory(
        CodeMergingBundle bundle, IReadOnlyList<MergingPullRequest> prs, IReadOnlyDictionary<string, BranchHistoryIndex> indexes)
    {
        var unread = prs
            .Where(p => !indexes.ContainsKey(p.RepositoryId))
            .Select(p => p.RepositoryName)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (unread.Count > 0 && string.IsNullOrEmpty(bundle.ErrorMessage))
        {
            bundle.ErrorMessage =
                $"Couldn't read the merging branch history of {string.Join(", ", unread)}, so those pull requests were only " +
                "checked by their merge commit. Compare Code checks their code.";
        }
    }

    /// <summary>
    /// A message two commits of the sheet share cannot tell them apart, so it is taken out of play
    /// across the whole sheet, so re-checking one row judges messages the same way the full run does.
    /// </summary>
    private static void MarkSharedMessages(CodeMergingBundle bundle, IEnumerable<BranchHistoryIndex> indexes)
    {
        var shared = bundle.AllPullRequests
            .Distinct()
            .SelectMany(p => p.RequiredCommits)
            .GroupBy(c => BranchHistoryIndex.SubjectOf(c.Message), StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Key.Length > 0 && g.Select(c => c.CommitId).Distinct(StringComparer.OrdinalIgnoreCase).Count() > 1)
            .Select(g => g.Key)
            .ToList();

        foreach (var index in indexes)
        {
            foreach (var subject in shared)
            {
                index.MarkShared(subject);
            }
        }
    }

    private static string BranchFor(IReadOnlyDictionary<string, string> branches, string repoId) =>
        branches.TryGetValue(repoId, out var b) && !string.IsNullOrWhiteSpace(b) ? b.Trim() : "";

    /// <summary>
    /// One batched fetch of the merging branch's recent history per repository, indexed by SHA, author
    /// and message so a cherry-pick is a single lookup, and with reverts applied so a change that was
    /// taken back off the branch no longer reads as present.
    /// </summary>
    private async Task<Dictionary<string, BranchHistoryIndex>> LoadTargetKeysAsync(
        IReadOnlyList<MergingPullRequest> prs, IReadOnlyDictionary<string, string> mergingBranchByRepoId,
        DateTime? since, CancellationToken ct)
    {
        var byRepo = prs
            .GroupBy(p => p.RepositoryId, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var loaded = await Task.WhenAll(byRepo.Select(async g =>
        {
            var pr = g.First();
            var from = ScanFloor(g, since);
            return (pr.RepositoryId,
                    Index: await TryLoadRepoTargetKeysAsync(pr, BranchFor(mergingBranchByRepoId, pr.RepositoryId), from, ct));
        }));

        var map = new Dictionary<string, BranchHistoryIndex>(StringComparer.OrdinalIgnoreCase);
        foreach (var (repoId, index) in loaded)
        {
            if (index is not null)
            {
                map[repoId] = index;
            }
        }
        return map;
    }

    /// <summary>
    /// How far back this repository's branch has to be read to be certain about these pull
    /// requests: the earliest commit among them, less a skew allowance. Code cannot reach a
    /// branch before it was written, so everything that could carry this work lies inside that
    /// range, which is what makes the answer exact rather than "the most recent N commits".
    /// Falls back to the sprint floor when no commit carries a date.
    /// </summary>
    private static DateTime? ScanFloor(IEnumerable<MergingPullRequest> prs, DateTime? sprintFloor)
    {
        DateTime? earliest = null;
        foreach (var pr in prs)
        {
            var candidate = pr.FirstActivityDate;
            if (candidate.HasValue && (earliest is null || candidate < earliest))
            {
                earliest = candidate;
            }
        }

        return earliest?.Subtract(CommitDateSkewBuffer) ?? sprintFloor;
    }

    /// <summary>Returns null when the branch history could not be read, so the caller falls back.</summary>
    private async Task<BranchHistoryIndex?> TryLoadRepoTargetKeysAsync(
        MergingPullRequest pr, string branch, DateTime? since, CancellationToken ct)
    {
        try
        {
            var newestFirst = new List<BranchCommit>();

            // Paged rather than one big request: the API caps a page at 1000 and silently
            // returns that many, which would quietly shrink the window back to where it was.
            for (var skip = 0; skip < TargetBranchCommitWindow; skip += CommitPageSize)
            {
                var page = await FetchCommitPageAsync(pr, branch, since, skip, ct);
                newestFirst.AddRange(page);

                // A short page is the end of the branch, or of the dated range.
                if (page.Count < CommitPageSize)
                {
                    break;
                }
            }

            // TFS returns newest first; the index needs oldest first so a later revert (or a
            // revert of that revert) overwrites the earlier verdict rather than losing to it.
            var index = new BranchHistoryIndex();
            for (var i = newestFirst.Count - 1; i >= 0; i--)
            {
                index.Add(newestFirst[i].Sha, newestFirst[i].Message, newestFirst[i].AuthorKey);
            }

            index.ScannedCommits = newestFirst.Count;
            index.HitWindowCap = newestFirst.Count >= TargetBranchCommitWindow;
            index.ScannedFrom = since;
            return index;
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            _logger.LogError(ex, "Could not read branch {Branch} in repo {Repo}; falling back to per-pull-request probes",
                branch, pr.RepositoryName);
            return null;
        }
    }

    private async Task<List<BranchCommit>> FetchCommitPageAsync(
        MergingPullRequest pr, string branch, DateTime? since, int skip, CancellationToken ct)
    {
        var url = _req.ProjectApi(pr.Project,
            $"git/repositories/{pr.RepositoryId}/commits" +
            $"?searchCriteria.itemVersion.version={Uri.EscapeDataString(branch)}" +
            $"&searchCriteria.itemVersion.versionType=branch" +
            $"&searchCriteria.$top={CommitPageSize}&searchCriteria.$skip={skip}" +
            (since.HasValue
                ? $"&searchCriteria.fromDate={Uri.EscapeDataString(since.Value.ToString("o"))}"
                : "") +
            $"&api-version={_req.Ver}");

        var data = await _req.GetJsonAsync(url, ct);
        return TfsJson.Values(data)
            .Select(c => new BranchCommit(
                TfsJson.Str(c, "commitId"),
                TfsJson.Str(c, "comment"),
                BranchHistoryIndex.AuthorKey(TfsJson.CommitEmail(c, "author"), TfsJson.CommitPerson(c, "author").Date)))
            .ToList();
    }

    /// <summary>
    /// The earliest point a scan needs to reach for the given sprint, or null to scan the
    /// whole window. Kept here so the date policy sits next to the fetch that uses it.
    /// </summary>
    public static DateTime? LookbackFrom(DateTime? earliestSprintStart) =>
        earliestSprintStart?.Subtract(SprintLookbackBuffer);

    /// <param name="AuthorKey">See <see cref="BranchHistoryIndex.AuthorKey"/>; null when the commit carries no author.</param>
    private sealed record BranchCommit(string Sha, string Message, string? AuthorKey);

    /// <summary>One run of the commit check: the sheet, the pull requests to judge, and what against.</summary>
    private sealed record CommitCheck(
        CodeMergingBundle Bundle, IReadOnlyList<MergingPullRequest> Candidates,
        IReadOnlyDictionary<string, string> Branches, DateTime? Since)
    {
        /// <summary>The sheet's generation when the check started; its verdicts are written only while it holds.</summary>
        public long Generation { get; } = Bundle.VerificationGeneration;
    }
}
