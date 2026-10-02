using DevKit.Web.Models;
using Xunit;

namespace DevKit.Tests;

/// <summary>
/// Once a pull request's code has been compared, the code decides whether it counts as merged — not its
/// commits. These pin that down at every level the sheet shows: the pull request, the work item's
/// roll-up, the summary counts and the "Code differs" filter.
/// </summary>
public class CodeVerdictRollupTests
{
    [Fact]
    public void Commits_decide_until_the_code_has_been_compared()
    {
        var pr = Pr(commitsMerged: true);

        Assert.True(pr.EffectiveMerged);
        Assert.False(pr.CodeContradictsCommits);
    }

    [Fact]
    public void Code_on_the_branch_counts_even_when_no_commit_matched()
    {
        // A cherry-pick squashed under a new message: no commit matches, the code is there.
        var pr = Pr(commitsMerged: false, code: CodeVerdict.Present);

        Assert.True(pr.EffectiveMerged);
    }

    [Fact]
    public void Commits_on_the_branch_do_not_count_when_the_code_is_missing()
    {
        var pr = Pr(commitsMerged: true, code: CodeVerdict.Missing);

        Assert.False(pr.EffectiveMerged);
        Assert.True(pr.CodeContradictsCommits);
    }

    [Theory]
    [InlineData(CodeVerdict.Unverified)]
    [InlineData(CodeVerdict.Skipped)]
    [InlineData(CodeVerdict.TooLarge)]
    [InlineData(CodeVerdict.Failed)]
    [InlineData(CodeVerdict.Checking)]
    public void A_comparison_without_an_answer_leaves_the_commit_verdict_standing(CodeVerdict verdict)
    {
        Assert.False(Pr(commitsMerged: false, code: verdict).EffectiveMerged);
        Assert.True(Pr(commitsMerged: true, code: verdict).EffectiveMerged);
    }

    [Fact]
    public void A_work_item_whose_code_is_all_there_is_merged()
    {
        var rollup = MergeRollupCalculator.For(new[] { Pr(false, CodeVerdict.Present), Pr(true, CodeVerdict.Present) });

        Assert.Equal(MergeRollup.Merged, rollup.State);
        Assert.True(rollup.HasCodeDetail);
        Assert.Equal("code: 2 / 2 files", rollup.CodeSummary);
    }

    [Fact]
    public void Code_lost_in_a_conflict_makes_the_work_item_partial_not_merged()
    {
        var rollup = MergeRollupCalculator.For(new[] { Pr(true, CodeVerdict.Present), Pr(true, CodeVerdict.Partial) });

        Assert.Equal(MergeRollup.Partial, rollup.State);
    }

    [Fact]
    public void Missing_code_is_not_merged_whatever_the_commits_say()
    {
        var rollup = MergeRollupCalculator.For(new[] { Pr(true, CodeVerdict.Missing) });

        Assert.Equal(MergeRollup.NotMerged, rollup.State);
    }

    [Fact]
    public void A_partly_merged_pull_request_alone_still_reads_as_partial()
    {
        var rollup = MergeRollupCalculator.For(new[] { Pr(false, CodeVerdict.Partial) });

        Assert.Equal(MergeRollup.Partial, rollup.State);
    }

    [Fact]
    public void Rolled_back_is_a_commit_check_finding_only()
    {
        var reverted = Pr(false);
        reverted.Commits[0].IsRolledBack = true;
        Assert.Equal(MergeRollup.RolledBack, MergeRollupCalculator.For(new[] { reverted }).State);

        reverted.CodeCheck = Check(CodeVerdict.Missing);
        Assert.Equal(MergeRollup.NotMerged, MergeRollupCalculator.For(new[] { reverted }).State);
    }

    [Fact]
    public void The_filter_and_counts_find_the_pull_requests_that_need_a_look()
    {
        var fine = Row(1, Pr(true, CodeVerdict.Present));
        var lost = Row(2, Pr(true, CodeVerdict.Partial));
        var notCompared = Row(3, Pr(true));
        var rows = new[] { fine, lost, notCompared };
        var filter = new MergingSheetFilter();

        Assert.Equal(2, filter.CountCodeCompared(rows));
        Assert.Equal(1, filter.CountCodeIssues(rows));
        Assert.Equal(1, filter.CountCodeLost(rows));

        filter.MergeState = MergingSheetFilter.CodeIssues;
        Assert.Equal(new[] { 2 }, filter.Apply(rows).Select(r => r.WorkItemId));
    }

    [Theory]
    [InlineData(new[] { FileCodeStatus.Present, FileCodeStatus.MatchesQa }, CodeVerdict.Present)]
    [InlineData(new[] { FileCodeStatus.Missing, FileCodeStatus.Missing }, CodeVerdict.Missing)]
    [InlineData(new[] { FileCodeStatus.Missing, FileCodeStatus.Unverified }, CodeVerdict.Missing)]
    [InlineData(new[] { FileCodeStatus.Present, FileCodeStatus.Missing }, CodeVerdict.Partial)]
    [InlineData(new[] { FileCodeStatus.Partial }, CodeVerdict.Partial)]
    [InlineData(new[] { FileCodeStatus.Present, FileCodeStatus.Error }, CodeVerdict.Unverified)]
    [InlineData(new FileCodeStatus[0], CodeVerdict.Present)]
    public void A_pull_requests_verdict_follows_from_its_files(FileCodeStatus[] files, CodeVerdict expected)
    {
        var check = PrCodeCheck.FromFiles(
            files.Select((s, i) => new FileCodeCheck { Path = $"/f{i}.cs", Status = s }).ToList(),
            CodeCompareScope.Sprint, "release/26.4", "abc");

        Assert.Equal(expected, check.Verdict);
        Assert.False(string.IsNullOrWhiteSpace(check.Summary));
    }

    [Fact]
    public void Line_detail_replaces_only_its_own_file()
    {
        var check = PrCodeCheck.FromFiles(
            new[] { new FileCodeCheck { Path = "/a.cs", Status = FileCodeStatus.Missing }, new FileCodeCheck { Path = "/b.cs" } },
            CodeCompareScope.Sprint, "release/26.4", "abc");
        var detailed = check.Files[0] with { Hunks = new[] { new HunkCheck { Status = HunkStatus.Missing } } };

        var updated = check.WithFile(detailed);

        Assert.Same(detailed, updated.Files[0]);
        Assert.Same(check.Files[1], updated.Files[1]);
        Assert.Equal(check.Verdict, updated.Verdict);
    }

    private static MergingPullRequest Pr(bool commitsMerged, CodeVerdict? code = null) => new()
    {
        PullRequestId = 1,
        Status = "completed",
        IsMergedToTargetBranch = commitsMerged,
        Commits = { new MergingCommit { CommitId = "abc", Message = "work", IsMergedToTargetBranch = commitsMerged } },
        CodeCheck = code is { } verdict ? Check(verdict) : null
    };

    private static PrCodeCheck Check(CodeVerdict verdict) => verdict switch
    {
        CodeVerdict.Present => PrCodeCheck.FromFiles(new[] { new FileCodeCheck { Path = "/a.cs" } }, CodeCompareScope.Sprint, "r", "c"),
        CodeVerdict.Missing => PrCodeCheck.FromFiles(new[] { new FileCodeCheck { Path = "/a.cs", Status = FileCodeStatus.Missing } }, CodeCompareScope.Sprint, "r", "c"),
        CodeVerdict.Partial => PrCodeCheck.FromFiles(new[] { new FileCodeCheck { Path = "/a.cs", Status = FileCodeStatus.Partial } }, CodeCompareScope.Sprint, "r", "c"),
        _ => PrCodeCheck.NotCompared(verdict, "", CodeCompareScope.Sprint, "r")
    };

    private static RequirementMergingRow Row(int id, MergingPullRequest pr) => new() { WorkItemId = id, PullRequests = { pr } };
}
