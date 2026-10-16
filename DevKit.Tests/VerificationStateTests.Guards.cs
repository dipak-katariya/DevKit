using DevKit.Web.Models;
using DevKit.Web.Services;
using Xunit;

namespace DevKit.Tests;

/// <summary>
/// A check that outlives the settings it ran with. A branch change or a new load clears the sheet while
/// the branch history is still being read; whatever the check then decides belongs to settings that no
/// longer apply, so none of it may land on the sheet: not a verdict, not a branch, not a scan figure.
/// Load itself waits while a check runs, so a check cannot carry on against a sheet it never read.
/// </summary>
public sealed partial class VerificationStateTests
{
    private static readonly TimeSpan MidRunWait = TimeSpan.FromSeconds(10);

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_commit_check_overtaken_by_a_reset_writes_no_verdicts(bool commitsRead)
    {
        const string Sha = "b5c0ffee00000000000000000000000000000001";
        _tfs.HistoryByRepo[WebRepoId] = new List<string>
        {
            BranchCommit(Sha, "Validate order totals before posting", DevOneEmail, AuthorTime)
        };
        var pr = Pr(5001, WebRepoId, "Contoso.Web", Commit(Sha, "Validate order totals before posting", AuthorTime));
        if (!commitsRead)
        {
            // Judged by its merge commit alone, which the probe finds on the branch.
            pr.Commits.Clear();
        }
        var bundle = Sheet(pr);
        _tfs.Gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var running = _service.VerifyAsync(bundle, Branches());
        await _tfs.Reached.Task.WaitAsync(MidRunWait);
        MergeVerificationService.ClearVerification(bundle);
        _tfs.Gate.SetResult();
        await running;

        Assert.Null(pr.IsMergedToTargetBranch);
        Assert.All(pr.Commits, c => Assert.Null(c.IsMergedToTargetBranch));
        Assert.All(pr.Commits, c => Assert.Equal(CommitMatch.None, c.MatchKind));
        Assert.Null(bundle.TargetBranchName);
        Assert.Equal(0, bundle.ScannedCommitCount);
        Assert.Null(bundle.ScannedFrom);
        Assert.Equal(MergeRollup.NotChecked, MergeRollupCalculator.For(bundle.Requirements[0].PullRequests).State);
    }

    [Fact]
    public async Task A_row_re_check_overtaken_by_a_reset_writes_no_verdicts()
    {
        const string Sha = "b5c0ffee00000000000000000000000000000002";
        _tfs.HistoryByRepo[WebRepoId] = new List<string>
        {
            BranchCommit(Sha, "Validate order totals before posting", DevOneEmail, AuthorTime)
        };
        var pr = Pr(5001, WebRepoId, "Contoso.Web", Commit(Sha, "Validate order totals before posting", AuthorTime));
        var bundle = Sheet(pr);
        _tfs.Gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var running = _service.VerifyRequirementAsync(bundle, 101, Branches());
        await _tfs.Reached.Task.WaitAsync(MidRunWait);
        MergeVerificationService.ClearVerification(bundle);
        _tfs.Gate.SetResult();
        await running;

        Assert.Null(pr.IsMergedToTargetBranch);
        var commit = Assert.Single(pr.Commits);
        Assert.Null(commit.IsMergedToTargetBranch);
        Assert.Equal(CommitMatch.None, commit.MatchKind);
        Assert.Null(bundle.TargetBranchName);
    }

    [Fact]
    public async Task A_commit_check_started_after_a_reset_writes_its_verdicts()
    {
        const string Sha = "b5c0ffee00000000000000000000000000000003";
        _tfs.HistoryByRepo[WebRepoId] = new List<string>
        {
            BranchCommit(Sha, "Validate order totals before posting", DevOneEmail, AuthorTime)
        };
        var pr = Pr(5001, WebRepoId, "Contoso.Web", Commit(Sha, "Validate order totals before posting", AuthorTime));
        var bundle = Sheet(pr);
        MergeVerificationService.ClearVerification(bundle);

        await _service.VerifyAsync(bundle, Branches());

        Assert.True(pr.IsMergedToTargetBranch);
        Assert.Equal(CommitMatch.Sha, Assert.Single(pr.Commits).MatchKind);
        Assert.Equal(Release, bundle.TargetBranchName);
        Assert.Equal(1, bundle.ScannedCommitCount);
    }

    [Fact]
    public void Clearing_verification_refuses_writes_tagged_with_the_generation_before_it()
    {
        var (bundle, _, _) = VerifiedSheet();
        var before = bundle.VerificationGeneration;
        var wrote = false;

        MergeVerificationService.ClearVerification(bundle);

        Assert.False(bundle.TryWriteResults(before, () => wrote = true));
        Assert.False(wrote);
        Assert.True(bundle.TryWriteResults(bundle.VerificationGeneration, () => wrote = true));
        Assert.True(wrote);
    }

    [Fact]
    public void A_new_sheet_refuses_writes_tagged_with_an_earlier_sheets_generation()
    {
        var earlier = new CodeMergingBundle();
        var later = new CodeMergingBundle();
        var wrote = false;

        Assert.True(later.VerificationGeneration > earlier.VerificationGeneration);
        Assert.False(later.TryWriteResults(earlier.VerificationGeneration, () => wrote = true));
        Assert.False(wrote);
    }

    [Theory]
    [InlineData(true, false, false, false)]
    [InlineData(false, true, false, false)]
    [InlineData(false, false, true, false)]
    [InlineData(false, false, false, true)]
    public void Load_waits_for_a_load_verify_all_the_remaining_commits_or_a_code_comparison(
        bool loading, bool verifying, bool loadingRest, bool comparingCode)
    {
        Assert.False(new MergingSheetActivity(loading, verifying, loadingRest, comparingCode).IsIdle);
    }

    [Fact]
    public void Load_is_offered_when_nothing_is_running()
    {
        var idle = new MergingSheetActivity(Loading: false, Verifying: false, LoadingRest: false, ComparingCode: false);

        Assert.True(idle.IsIdle);
        Assert.False(idle.IsChecking);
    }

    [Theory]
    [InlineData(false, true, false, false, true)]
    [InlineData(false, false, true, false, true)]
    [InlineData(false, false, false, true, true)]
    [InlineData(true, false, false, false, false)]
    public void Verify_all_the_remaining_commits_and_a_code_comparison_count_as_a_check_and_a_load_does_not(
        bool loading, bool verifying, bool loadingRest, bool comparingCode, bool checking)
    {
        Assert.Equal(checking, new MergingSheetActivity(loading, verifying, loadingRest, comparingCode).IsChecking);
    }
}
