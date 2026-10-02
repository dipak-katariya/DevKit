using DevKit.Web.Models;

namespace DevKit.Web.Services;

/// <summary>
/// The commit check: did this pull request's commits reach the release branch?
///
/// Code often reaches a release branch by cherry-pick rather than by merge, which produces a different
/// SHA for the same change. The target branch's history is therefore indexed by SHA, by the author's
/// email and time (kept through a cherry-pick, even a reworded one) and by distinctive messages — see
/// <see cref="BranchHistoryIndex"/>.
///
/// Commits being on a branch does not prove their code is: a conflict resolved by dropping lines leaves
/// every commit in place. <see cref="CodeCompareService"/> answers that; this check is the fast first pass.
/// Two limits are surfaced in the UI: only the most recent <see cref="TargetBranchCommitWindow"/> commits
/// are read (still-missing commits past it are then asked about directly), and a commit whose only match
/// is a common message is not counted.
/// </summary>
public class MergeVerificationService
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

    public MergeVerificationService(HttpClient http, SettingsService settings, ILogger<MergeVerificationService> logger)
    {
        _req = new TfsRequestClient(http, settings);
        _logger = logger;
    }

    /// <summary>Most commits looked up one by one after a capped scan, for the accuracy note.</summary>
    public static int BeyondWindowProbeLimit => MaxBeyondWindowProbes;

    /// <summary>Number of target-branch commits examined per repository.</summary>
    public static int CommitWindow => TargetBranchCommitWindow;

    /// <summary>
    /// Verifies every completed pull request in the bundle, each against the merging branch
    /// configured for its own repository. Repositories with no branch configured are skipped
    /// rather than verified against someone else's branch.
    /// </summary>
    public Task VerifyAsync(
        CodeMergingBundle bundle, IReadOnlyDictionary<string, string> mergingBranchByRepoId,
        DateTime? since = null, CancellationToken ct = default) =>
        VerifyPullRequestsAsync(bundle, bundle.AllPullRequests, mergingBranchByRepoId, since, ct);

    /// <summary>
    /// Re-verifies a single requirement. Lets someone who has just pushed a cherry-pick
    /// refresh one row instead of re-verifying the whole sprint.
    /// </summary>
    public Task VerifyRequirementAsync(
        CodeMergingBundle bundle, int workItemId,
        IReadOnlyDictionary<string, string> mergingBranchByRepoId,
        DateTime? since = null, CancellationToken ct = default)
    {
        var row = bundle.Requirements.FirstOrDefault(r => r.WorkItemId == workItemId);
        return row is null
            ? Task.CompletedTask
            : VerifyPullRequestsAsync(bundle, row.PullRequests, mergingBranchByRepoId, since, ct);
    }

    /// <summary>Drops all verification state — commit and code alike — so the Merged column disappears cleanly.</summary>
    public static void ClearVerification(CodeMergingBundle bundle)
    {
        bundle.VerifiedBranchByRepoId.Clear();
        bundle.LastCodeCompare = null;
        foreach (var pr in bundle.AllPullRequests)
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
    }

    private async Task VerifyPullRequestsAsync(
        CodeMergingBundle bundle, IEnumerable<MergingPullRequest> candidates,
        IReadOnlyDictionary<string, string> mergingBranchByRepoId, DateTime? since, CancellationToken ct)
    {
        if (mergingBranchByRepoId.Count == 0)
        {
            return;
        }

        var completed = candidates
            .Where(p => p.IsCompleted && BranchFor(mergingBranchByRepoId, p.RepositoryId).Length > 0)
            .Distinct()
            .ToList();
        if (completed.Count == 0)
        {
            return;
        }

        foreach (var pr in completed)
        {
            bundle.VerifiedBranchByRepoId[pr.RepositoryId] = BranchFor(mergingBranchByRepoId, pr.RepositoryId);
        }

        var targetKeys = await LoadTargetKeysAsync(completed, mergingBranchByRepoId, since, ct);
        WarnAboutUnreadHistory(bundle, completed, targetKeys);
        MarkSharedMessages(bundle, targetKeys.Values);
        await Task.WhenAll(completed.Select(pr =>
            EvaluateAsync(pr, BranchFor(mergingBranchByRepoId, pr.RepositoryId), targetKeys, ct)));
        await ProbeBeyondWindowAsync(completed, mergingBranchByRepoId, targetKeys, ct);

        // The note quotes what was actually read, which varies by how much history the branch
        // has in range — claiming a fixed number would overstate a short branch.
        bundle.ScannedCommitCount = targetKeys.Values.Count > 0 ? targetKeys.Values.Max(i => i.ScannedCommits) : 0;
        bundle.CommitWindowCapped = targetKeys.Values.Any(i => i.HitWindowCap);
        bundle.ScannedFrom = targetKeys.Values.Count > 0
            ? targetKeys.Values.Where(i => i.ScannedFrom.HasValue).Select(i => i.ScannedFrom!.Value).DefaultIfEmpty().Min()
            : null;
        if (bundle.ScannedFrom == default)
        {
            bundle.ScannedFrom = null;
        }
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
    /// A message two commits of the sheet share cannot tell them apart, so it is taken out of play —
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

    /// <summary>
    /// A capped scan cannot see commits merged before its window, which would then read as missing.
    /// In those repositories each still-missing commit is asked about directly instead.
    /// </summary>
    private async Task ProbeBeyondWindowAsync(
        IReadOnlyList<MergingPullRequest> prs, IReadOnlyDictionary<string, string> branches,
        IReadOnlyDictionary<string, BranchHistoryIndex> indexes, CancellationToken ct)
    {
        var targets = prs
            .Where(pr => indexes.TryGetValue(pr.RepositoryId, out var index) && index.HitWindowCap)
            .SelectMany(pr => pr.RequiredCommits
                .Where(c => c.IsMergedToTargetBranch == false && !c.IsRolledBack)
                .Select(c => (Pr: pr, Commit: c)))
            .Take(MaxBeyondWindowProbes)
            .ToList();
        if (targets.Count == 0)
        {
            return;
        }

        await Task.WhenAll(targets.Select(async t =>
        {
            var onBranch = await DiffBehindCountZeroAsync(t.Pr, t.Commit.CommitId, BranchFor(branches, t.Pr.RepositoryId), ct);
            if (onBranch == true)
            {
                t.Commit.IsMergedToTargetBranch = true;
                t.Commit.MatchKind = CommitMatch.Sha;
            }
        }));

        foreach (var pr in targets.Select(t => t.Pr).Distinct())
        {
            RecomputePrState(pr);
        }
    }

    /// <summary>Re-derives the pull-request verdict after commits below it changed.</summary>
    private static void RecomputePrState(MergingPullRequest pr)
    {
        if (pr.IsMergedToTargetBranch is null)
        {
            return;
        }

        var required = pr.RequiredCommits.ToList();
        // No commits loaded means the verdict came from the fallback probe, not from these
        // commits; an empty All() would otherwise flip it to merged.
        if (required.Count == 0)
        {
            return;
        }

        pr.IsMergedToTargetBranch = required.All(c => c.IsMergedToTargetBranch == true);
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
    /// range — which is what makes the answer exact rather than "the most recent N commits".
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

            // TFS returns newest first; the index needs oldest first so a later revert — or a
            // revert of that revert — overwrites the earlier verdict rather than losing to it.
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

        var data = await TfsThrottle.RunAsync(() => _req.GetJsonAsync(url, ct), ct);
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

    private async Task EvaluateAsync(
        MergingPullRequest pr, string branch, IReadOnlyDictionary<string, BranchHistoryIndex> targetKeys, CancellationToken ct)
    {
        // A pull request that merged into this very branch put its commits there. That is no
        // longer the whole story — they can have been reverted off it since — so it is used
        // as the floor for a missing verdict rather than as a shortcut past the check.
        var mergedIntoThisBranch = GitRefName.Matches(pr.TargetBranch, branch);

        if (pr.Commits.Count == 0 || !targetKeys.TryGetValue(pr.RepositoryId, out var index))
        {
            pr.IsMergedToTargetBranch = mergedIntoThisBranch ? true : await FallbackMergedAsync(pr, branch, ct);
            return;
        }

        var allRequiredMerged = true;
        foreach (var c in pr.Commits)
        {
            var lookup = index.Classify(c.CommitId, c.Message, BranchHistoryIndex.AuthorKey(c.AuthorEmail, c.AuthorDate));
            var presence = lookup.Presence;

            // Not finding a commit of a pull request that merged here means it fell out of the
            // commit window, not that it never landed. Only an actual revert overrides that.
            if (mergedIntoThisBranch && presence == CommitPresence.Absent)
            {
                presence = CommitPresence.Present;
                lookup = lookup with { Match = CommitMatch.Sha };
            }

            c.IsMergedToTargetBranch = presence == CommitPresence.Present;
            c.MatchKind = lookup.Match;
            c.IsRolledBack = presence == CommitPresence.Reverted;
            c.RollbackReason = c.IsRolledBack
                ? $"A revert commit on {branch} undoes this change."
                : "";

            // Merge-tracking commits can never reach a release branch, so they never block.
            if (presence != CommitPresence.Present && !c.IsBranchMergeCommit)
            {
                allRequiredMerged = false;
            }
        }
        pr.IsMergedToTargetBranch = allRequiredMerged;
    }

    /// <summary>
    /// Used when a pull request has no commits loaded, or the repository's branch history could not be
    /// read: is the merge commit reachable from the branch tip? Asked through the diff's behind count,
    /// which answers exactly that. A commit search by id and branch is not used: the ids criterion is not
    /// documented to combine with a branch, and a server that ignores the branch would answer "it exists"
    /// — reading every pull request as merged.
    /// </summary>
    private Task<bool?> FallbackMergedAsync(MergingPullRequest pr, string branch, CancellationToken ct)
    {
        var sha = !string.IsNullOrEmpty(pr.LastMergeCommitId) ? pr.LastMergeCommitId : pr.LastMergeSourceCommitId;
        return string.IsNullOrEmpty(sha)
            ? Task.FromResult<bool?>(null)
            : DiffBehindCountZeroAsync(pr, sha, branch, ct);
    }

    private async Task<bool?> DiffBehindCountZeroAsync(MergingPullRequest pr, string sha, string branch, CancellationToken ct)
    {
        var url = _req.ProjectApi(pr.Project,
            $"git/repositories/{pr.RepositoryId}/diffs/commits?baseVersion={sha}&baseVersionType=commit" +
            $"&targetVersion={Uri.EscapeDataString(branch)}&targetVersionType=branch&$top=1&api-version={_req.Ver}");
        try
        {
            var data = await TfsThrottle.RunAsync(() => _req.GetJsonAsync(url, ct), ct);
            // behindCount == 0 means the commit is reachable from the branch tip.
            return data.TryGetProperty("behindCount", out var behind)
                   && behind.TryGetInt32(out var count)
                   && count == 0;
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            _logger.LogError(ex, "Diff probe failed for commit {Sha} of PR {PrId} on {Branch}", sha, pr.PullRequestId, branch);
            return null;
        }
    }

    /// <param name="AuthorKey">See <see cref="BranchHistoryIndex.AuthorKey"/>; null when the commit carries no author.</param>
    private sealed record BranchCommit(string Sha, string Message, string? AuthorKey);
}
