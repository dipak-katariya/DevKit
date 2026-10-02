namespace DevKit.Web.Models;

/// <summary>Merge outcome for a work item, rolled up from its pull requests and their commits.</summary>
public enum MergeRollup
{
    /// <summary>Nothing to verify — no pull requests were discovered for this work item.</summary>
    NoPullRequests,

    /// <summary>No pull request has been checked against a target branch yet.</summary>
    NotChecked,

    /// <summary>Every checked pull request landed on the target branch.</summary>
    Merged,

    /// <summary>Some code reached the target branch and some did not.</summary>
    Partial,

    /// <summary>Nothing reached the target branch.</summary>
    NotMerged,

    /// <summary>
    /// Everything that is missing reached the branch once and was reverted off it. Worth its
    /// own state because it is a regression to chase, not work still to be merged.
    /// </summary>
    RolledBack
}

/// <summary>
/// A work item's merge state together with the pull-request, commit and file counts behind it, so the
/// UI can say <i>how much</i> landed rather than only pass or fail.
/// </summary>
public readonly record struct MergeRollupResult(
    MergeRollup State,
    int MergedPrs,
    int CheckedPrs,
    int MergedCommits,
    int RequiredCommits,
    int RolledBackCommits = 0,
    int CodeFilesPresent = 0,
    int CodeFiles = 0)
{
    public bool HasCommitDetail => RequiredCommits > 0;

    public bool HasRollback => RolledBackCommits > 0;

    /// <summary>At least one pull request's code was compared, so the file counts mean something.</summary>
    public bool HasCodeDetail => CodeFiles > 0;

    public string Label => State switch
    {
        MergeRollup.NoPullRequests => "No PRs",
        MergeRollup.NotChecked => "Not checked",
        MergeRollup.Merged => "Merged",
        MergeRollup.RolledBack => "Rolled back",
        MergeRollup.Partial => $"Partial {MergedPrs}/{CheckedPrs} PRs",
        _ => "Not merged"
    };

    /// <summary>Commit coverage, e.g. "7 / 10 commits" — blank when no commits were loaded.</summary>
    public string CommitSummary
    {
        get
        {
            if (!HasCommitDetail)
            {
                return "";
            }
            var coverage = $"{MergedCommits} / {RequiredCommits} commits";
            return HasRollback ? $"{coverage} · {RolledBackCommits} rolled back" : coverage;
        }
    }

    /// <summary>File coverage from the code comparison, e.g. "12 / 14 files".</summary>
    public string CodeSummary => HasCodeDetail ? $"code: {CodeFilesPresent} / {CodeFiles} files" : "";
}

/// <summary>
/// Rolls a set of pull requests up into one work-item merge state.
///
/// Each pull request counts by <see cref="MergingPullRequest.EffectiveMerged"/>: its code comparison
/// once one has reached an answer, its commit check until then. The roll-up is computed from the pull
/// requests the caller considers visible, so it always agrees with the counts shown next to it.
/// Merge-tracking commits are excluded by <see cref="MergingPullRequest.RequiredCommits"/> and so never
/// drag a work item down.
/// </summary>
public static class MergeRollupCalculator
{
    public static MergeRollupResult For(IReadOnlyCollection<MergingPullRequest> pullRequests)
    {
        if (pullRequests.Count == 0)
        {
            return new MergeRollupResult(MergeRollup.NoPullRequests, 0, 0, 0, 0);
        }

        var checkedPrs = pullRequests.Where(p => p.EffectiveMerged.HasValue).ToList();
        if (checkedPrs.Count == 0)
        {
            return new MergeRollupResult(MergeRollup.NotChecked, 0, 0, 0, 0);
        }

        var compared = checkedPrs.Where(p => p.CodeCheck?.IsConclusive == true).Select(p => p.CodeCheck!).ToList();
        var required = checkedPrs.SelectMany(p => p.RequiredCommits).ToList();
        var counts = new Counts(
            MergedPrs: checkedPrs.Count(p => p.EffectiveMerged == true),
            CheckedPrs: checkedPrs.Count,
            PartlyMergedCode: compared.Count(c => c.Verdict == CodeVerdict.Partial),
            // Commits of a pull request whose code was compared prove nothing more: the code decided it.
            MergedCommitsWithoutCode: checkedPrs
                .Where(p => p.CodeCheck?.IsConclusive != true)
                .Sum(p => p.RequiredCommits.Count(c => c.IsMergedToTargetBranch == true)),
            RolledBackCommits: required.Count(c => c.IsRolledBack),
            MissingCommits: required.Count(c => c.IsMergedToTargetBranch == false),
            CodeCompared: compared.Count > 0);

        return new MergeRollupResult(ResolveState(counts), counts.MergedPrs, counts.CheckedPrs,
            required.Count(c => c.IsMergedToTargetBranch == true), required.Count, counts.RolledBackCommits,
            compared.Sum(c => c.FilesPresent), compared.Sum(c => c.Files.Count));
    }

    private readonly record struct Counts(
        int MergedPrs, int CheckedPrs, int PartlyMergedCode, int MergedCommitsWithoutCode,
        int RolledBackCommits, int MissingCommits, bool CodeCompared);

    private static MergeRollup ResolveState(Counts n)
    {
        if (n.MergedPrs == n.CheckedPrs)
        {
            return MergeRollup.Merged;
        }

        // Everything absent from the branch was on it once, so this is a rollback to chase rather than
        // work still waiting to be merged. Only the commit check can say that: once the code has been
        // compared, missing code is missing however it came to be.
        if (!n.CodeCompared && n.RolledBackCommits > 0 && n.RolledBackCommits == n.MissingCommits)
        {
            return MergeRollup.RolledBack;
        }

        // Nothing landed at any level, so this work item is genuinely absent from the branch.
        var anythingLanded = n.MergedPrs > 0 || n.PartlyMergedCode > 0 || n.MergedCommitsWithoutCode > 0;
        return anythingLanded ? MergeRollup.Partial : MergeRollup.NotMerged;
    }
}
