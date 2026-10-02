using DevKit.Web.Models;
using DevKit.Web.Services;
using Xunit;

namespace DevKit.Tests;

/// <summary>
/// The guards: whatever TFS does — fail, stall, page oddly, answer half a reply — or whatever the user
/// does mid-run, a pull request is never reported as having its code on the branch when that was not
/// established, and nothing is left showing "Comparing…".
/// </summary>
public sealed partial class CodeCompareServiceTests
{
    [Fact]
    public async Task A_qa_branch_that_cannot_be_read_never_makes_a_missing_file_present()
    {
        var (pr, _) = MergedPr(1023);
        _git.RemoveBranch(QaBranch);
        // Totals.cs, new in the pull request, never reached the release branch.
        _git.Branch(Release, _git.Commit(Files(("/src/Export.cs", ExportAfter))));

        await CompareAsync(CodeCompareScope.Sprint, pr);

        var totals = Assert.Single(pr.CodeCheck!.Files, f => f.Path == "/src/Totals.cs");
        Assert.Equal(FileCodeStatus.Missing, totals.Status);
        Assert.Equal(CodeVerdict.Partial, pr.CodeCheck.Verdict);
        Assert.Contains("QA's current version couldn't be read", pr.CodeCheck.Summary);
    }

    [Fact]
    public async Task A_folder_listing_without_entries_is_an_error_not_an_empty_folder()
    {
        var (pr, _) = MergedPr(1024);
        _git.Branch(Release, _git.Commit(Files(("/src/Export.cs", ExportAfter))));
        _git.BrokenTrees.Add(_git.TreeOf(pr.LastMergeCommitId));

        await CompareAsync(CodeCompareScope.Sprint, pr);

        Assert.NotEqual(CodeVerdict.Present, pr.CodeCheck!.Verdict);
        Assert.DoesNotContain(pr.CodeCheck.Files, f => f.IsPresent);
        Assert.All(pr.CodeCheck.Files, f => Assert.Equal(FileCodeStatus.Error, f.Status));
    }

    [Fact]
    public async Task A_submodule_pointer_is_listed_for_a_manual_check_not_dropped()
    {
        var (pr, after) = MergedPr(1025);
        _git.Submodules[(pr.LastMergeTargetCommitId, pr.LastMergeCommitId)] = new[] { "/lib/shared" };
        _git.Branch(Release, _git.Commit(after));

        await CompareAsync(CodeCompareScope.Sprint, pr);

        var submodule = Assert.Single(pr.CodeCheck!.Files, f => f.Path == "/lib/shared");
        Assert.Equal(FileCodeStatus.Unverified, submodule.Status);
        Assert.Contains("submodule", submodule.Reason);
        Assert.Equal(CodeVerdict.Unverified, pr.CodeCheck.Verdict);
    }

    [Fact]
    public async Task A_pull_request_that_changed_no_files_decides_nothing()
    {
        var files = Files(("/src/Export.cs", ExportBefore));
        var baseCommit = _git.Commit(files);
        var merge = _git.Commit(files, baseCommit);
        _git.Branch(QaBranch, merge);
        _git.Branch(Release, _git.Commit(files));
        var pr = Pr(1026, baseCommit, merge);

        await CompareAsync(CodeCompareScope.Sprint, pr);

        Assert.Equal(CodeVerdict.Skipped, pr.CodeCheck!.Verdict);
        Assert.Null(pr.EffectiveMerged);
    }

    [Fact]
    public async Task A_run_overtaken_by_a_reset_never_writes_its_result()
    {
        var (pr, after) = MergedPr(1027);
        _git.Branch(Release, _git.Commit(after));
        _git.Gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var running = CompareAsync(CodeCompareScope.Sprint, pr);
        await _git.DiffRequested.Task.WaitAsync(TimeSpan.FromSeconds(10));
        pr.CodeCheck = null;
        _git.Gate.SetResult();
        await running;

        Assert.Null(pr.CodeCheck);
    }

    [Fact]
    public async Task Stopping_while_branches_are_read_is_a_stop_even_when_tfs_then_fails()
    {
        var (pr, after) = MergedPr(1028);
        _git.Branch(Release, _git.Commit(after));
        var earlier = PrCodeCheck.NotCompared(CodeVerdict.Missing, "from an earlier run", CodeCompareScope.Sprint, Release);
        pr.CodeCheck = earlier;
        _git.RefsGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var stop = new CancellationTokenSource();

        var running = _service.CompareAsync(new CodeCompareRequest(new[] { pr }, Branches(Release), CodeCompareScope.Sprint), null, stop.Token);
        await _git.RefsRequested.Task.WaitAsync(TimeSpan.FromSeconds(10));
        stop.Cancel();
        _git.RefsGate.SetResult();
        var summary = await running;

        Assert.True(summary.Stopped);
        Assert.Same(earlier, pr.CodeCheck);
    }

    [Fact]
    public async Task A_pull_request_merged_straight_into_the_branch_and_reverted_there_is_missing()
    {
        var baseCommit = _git.Commit(Files(("/src/Export.cs", ExportBefore)));
        var merge = _git.Commit(Files(("/src/Export.cs", ExportAfter)), baseCommit);
        // A hotfix merged straight into release, then reverted on release.
        _git.Branch(Release, _git.Commit(Files(("/src/Export.cs", ExportBefore)), merge));
        var pr = Pr(1029, baseCommit, merge);
        pr.TargetBranch = Release;

        await CompareAsync(CodeCompareScope.Sprint, pr);

        Assert.Equal(CodeVerdict.Missing, pr.CodeCheck!.Verdict);
        Assert.Contains("merged straight into", pr.CodeCheck.Summary);
    }

    [Fact]
    public async Task Short_pages_from_tfs_still_bring_every_file()
    {
        _git.PageLimit = 2;
        var (pr, after) = MergedPr(1030);
        _git.Branch(Release, _git.Commit(after));

        await CompareAsync(CodeCompareScope.Sprint, pr);

        Assert.Equal(CodeVerdict.Present, pr.CodeCheck!.Verdict);
        Assert.Equal(2, pr.CodeCheck.Files.Count);
    }

    [Fact]
    public async Task A_server_that_repeats_its_first_page_is_caught_not_looped()
    {
        _git.PageLimit = 2;
        _git.IgnoreSkip = true;
        var (pr, after) = MergedPr(1031);
        _git.Branch(Release, _git.Commit(after));

        await CompareAsync(CodeCompareScope.Sprint, pr);

        Assert.Equal(CodeVerdict.Failed, pr.CodeCheck!.Verdict);
        Assert.Contains("full list", pr.CodeCheck.Summary);
    }

    [Fact]
    public async Task A_file_list_tfs_cut_short_is_never_read_as_complete()
    {
        _git.CapDiffs = true;
        var (pr, after) = MergedPr(1022);
        _git.Branch(Release, _git.Commit(after));

        await CompareAsync(CodeCompareScope.PullRequest, pr);

        Assert.Equal(CodeVerdict.Failed, pr.CodeCheck!.Verdict);
        Assert.Contains("full list", pr.CodeCheck.Summary);
    }

    [Fact]
    public async Task One_failing_pull_request_does_not_stop_the_others()
    {
        var (good, after) = MergedPr(1009);
        var (bad, _) = MergedPr(1010);
        _git.FailingDiffTargets.Add(bad.LastMergeCommitId);
        _git.Branch(Release, _git.Commit(after));

        var summary = await CompareAsync(CodeCompareScope.Sprint, good, bad);

        Assert.Equal(CodeVerdict.Present, good.CodeCheck!.Verdict);
        Assert.Equal(CodeVerdict.Failed, bad.CodeCheck!.Verdict);
        Assert.Contains("failed", bad.CodeCheck.Summary);
        Assert.Equal(1, summary.Failed);
    }

    [Fact]
    public async Task A_merging_branch_that_does_not_exist_fails_cleanly()
    {
        var (pr, _) = MergedPr(1014);

        await _service.CompareAsync(new CodeCompareRequest(new[] { pr }, Branches("release/gone"), CodeCompareScope.Sprint));

        Assert.Equal(CodeVerdict.Failed, pr.CodeCheck!.Verdict);
        Assert.Contains("release/gone", pr.CodeCheck.Summary);
    }

    [Fact]
    public async Task Stopping_puts_back_what_the_pull_request_showed_before()
    {
        var (pr, after) = MergedPr(1015);
        _git.Branch(Release, _git.Commit(after));
        var earlier = PrCodeCheck.NotCompared(CodeVerdict.Missing, "from an earlier run", CodeCompareScope.Sprint, Release);
        pr.CodeCheck = earlier;
        _git.Gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var stop = new CancellationTokenSource();

        var running = _service.CompareAsync(new CodeCompareRequest(new[] { pr }, Branches(Release), CodeCompareScope.Sprint), null, stop.Token);
        await _git.DiffRequested.Task.WaitAsync(TimeSpan.FromSeconds(10));
        stop.Cancel();
        var summary = await running;

        Assert.True(summary.Stopped);
        Assert.Same(earlier, pr.CodeCheck);
    }
}
