using DevKit.Web.Models;

namespace DevKit.Web.Services;

/// <summary>
/// Judging each pull request against the branch history that was read: commit by commit, the probe of
/// commits a capped scan could not see, and the merge-commit fallback used when there is nothing to
/// judge the commits against. Each judgement is held as a verdict and written to the sheet only once the
/// run knows the sheet still wants it.
/// </summary>
public partial class MergeVerificationService
{
    /// <summary>
    /// A capped scan cannot see commits merged before its window, which would then read as missing.
    /// In those repositories each still-missing commit is asked about directly instead.
    /// </summary>
    private async Task ProbeBeyondWindowAsync(
        IReadOnlyList<PrVerdict> verdicts, IReadOnlyDictionary<string, string> branches,
        IReadOnlyDictionary<string, BranchHistoryIndex> indexes, CancellationToken ct)
    {
        var targets = verdicts
            .Where(v => indexes.TryGetValue(v.Pr.RepositoryId, out var index) && index.HitWindowCap)
            .SelectMany(v => v.StillMissing.Select(c => (Verdict: v, Commit: c)))
            .Take(MaxBeyondWindowProbes)
            .ToList();
        if (targets.Count == 0)
        {
            return;
        }

        await Task.WhenAll(targets.Select(async t =>
        {
            var pr = t.Verdict.Pr;
            var onBranch = await DiffBehindCountZeroAsync(pr, t.Commit.Commit.CommitId, BranchFor(branches, pr.RepositoryId), ct);
            if (onBranch == true)
            {
                t.Commit.FoundOnBranch();
            }
        }));

        foreach (var verdict in targets.Select(t => t.Verdict).Distinct())
        {
            verdict.Recompute();
        }
    }

    private async Task<PrVerdict> EvaluateAsync(
        MergingPullRequest pr, string branch, IReadOnlyDictionary<string, BranchHistoryIndex> targetKeys, CancellationToken ct)
    {
        // Commits that could not be read are not "no commits". The merge commit alone would answer for
        // them, and it cannot show one reverted since, so the pull request stays unchecked until they read.
        if (pr.CommitsError is not null)
        {
            return new PrVerdict(pr, null, Array.Empty<CommitVerdict>());
        }

        // A pull request that merged into this very branch put its commits there. That is no
        // longer the whole story (they can have been reverted off it since), so it is used
        // as the floor for a missing verdict rather than as a shortcut past the check.
        var mergedIntoThisBranch = GitRefName.Matches(pr.TargetBranch, branch);

        if (pr.Commits.Count == 0 || !targetKeys.TryGetValue(pr.RepositoryId, out var index))
        {
            var merged = mergedIntoThisBranch ? true : await FallbackMergedAsync(pr, branch, ct);
            return new PrVerdict(pr, merged, Array.Empty<CommitVerdict>());
        }

        var commits = pr.Commits.Select(c => Classify(c, index, branch, mergedIntoThisBranch)).ToList();

        // Merge-tracking commits can never reach a release branch, so they never block.
        return new PrVerdict(pr, commits.TrueForAll(c => c.Merged || c.Commit.IsBranchMergeCommit), commits);
    }

    private static CommitVerdict Classify(MergingCommit c, BranchHistoryIndex index, string branch, bool mergedIntoThisBranch)
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

        var rolledBack = presence == CommitPresence.Reverted;
        return new CommitVerdict(c, presence == CommitPresence.Present, lookup.Match, rolledBack,
            rolledBack ? $"A revert commit on {branch} undoes this change." : "");
    }

    /// <summary>
    /// Used when a pull request has no commits loaded, or the repository's branch history could not be
    /// read: is the merge commit reachable from the branch tip? Asked through the diff's behind count,
    /// which answers exactly that. A commit search by id and branch is not used: the ids criterion is not
    /// documented to combine with a branch, and a server that ignores the branch would answer "it exists",
    /// reading every pull request as merged.
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
            var data = await _req.GetJsonAsync(url, ct);
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

    /// <summary>What the check decided for one pull request and the commits it judged, not yet on the sheet.</summary>
    private sealed class PrVerdict
    {
        private readonly List<CommitVerdict> _commits;

        /// <param name="commits">Empty when the verdict came from the merge-commit fallback, which leaves the commits as they are.</param>
        public PrVerdict(MergingPullRequest pr, bool? merged, IEnumerable<CommitVerdict> commits)
        {
            Pr = pr;
            Merged = merged;
            _commits = commits.ToList();
        }

        public MergingPullRequest Pr { get; }

        public bool? Merged { get; private set; }

        /// <summary>Required commits not found on the branch and not taken back off it either.</summary>
        public IEnumerable<CommitVerdict> StillMissing =>
            _commits.Where(c => !c.Commit.IsBranchMergeCommit && !c.Merged && !c.RolledBack);

        /// <summary>Re-derives the verdict after commits below it were found on the branch.</summary>
        public void Recompute()
        {
            var required = _commits.Where(c => !c.Commit.IsBranchMergeCommit).ToList();
            // No commits judged means the verdict came from the fallback probe, not from these
            // commits; an empty All() would otherwise flip it to merged.
            if (Merged is null || required.Count == 0)
            {
                return;
            }

            Merged = required.TrueForAll(c => c.Merged);
        }

        public void Write()
        {
            Pr.IsMergedToTargetBranch = Merged;
            foreach (var commit in _commits)
            {
                commit.Write();
            }
        }
    }

    /// <summary>What the check decided for one commit, not yet on the sheet.</summary>
    private sealed class CommitVerdict
    {
        public CommitVerdict(MergingCommit commit, bool merged, CommitMatch match, bool rolledBack, string rollbackReason)
        {
            Commit = commit;
            Merged = merged;
            Match = match;
            RolledBack = rolledBack;
            RollbackReason = rollbackReason;
        }

        public MergingCommit Commit { get; }

        public bool Merged { get; private set; }

        public CommitMatch Match { get; private set; }

        public bool RolledBack { get; }

        public string RollbackReason { get; }

        /// <summary>A direct question past a capped scan found the commit itself on the branch.</summary>
        public void FoundOnBranch()
        {
            Merged = true;
            Match = CommitMatch.Sha;
        }

        public void Write()
        {
            Commit.IsMergedToTargetBranch = Merged;
            Commit.MatchKind = Match;
            Commit.IsRolledBack = RolledBack;
            Commit.RollbackReason = RollbackReason;
        }
    }
}
