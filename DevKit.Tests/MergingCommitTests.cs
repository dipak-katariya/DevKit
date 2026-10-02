using DevKit.Web.Models;
using DevKit.Web.Services;
using Xunit;

namespace DevKit.Tests;

/// <summary>
/// Which commits a pull request must land decides whether the merging sheet calls it merged.
/// Classifying a merge commit as required is the largest source of false "not merged" verdicts,
/// because such a commit belongs to the source branch's history and can never appear on a
/// release branch by itself.
/// </summary>
public class MergingCommitTests
{
    private static MergingCommit Commit(string message, bool? merged = null) =>
        new() { CommitId = "abc123def456", Message = message, IsMergedToTargetBranch = merged };

    [Theory]
    [InlineData("Merge remote-tracking branch 'origin/main' into feature/x")]
    [InlineData("Merge branch 'main' into FXCPMGR/PI2026_5_2/1631102_something")]
    [InlineData("Merge branch 'dev-qa-net10'")]
    // Case and leading whitespace both vary in real history.
    [InlineData("merge branch 'main'")]
    [InlineData("MERGE REMOTE-TRACKING BRANCH 'origin/dev'")]
    [InlineData("   Merge branch 'main' into feature/x")]
    public void Branch_merge_commits_are_optional(string message)
    {
        Assert.True(Commit(message).IsBranchMergeCommit);
    }

    [Theory]
    // Real work, whatever it is named.
    [InlineData("FXCPMGR : 1631102 : Excel to CPFOIA Migration - Version Update")]
    [InlineData("Fix merge conflict in migration tool")]
    [InlineData("")]
    // A completed pull request's own merge commit lands ON the target branch, so it is not
    // one of the source branch's internal merges and must still be accounted for.
    [InlineData("Merged PR 92599: FXCPMGR : 1608055 : delivery mode")]
    // Neither of these opens with one of the two git prefixes.
    [InlineData("Merge pull request #42 from org/feature")]
    [InlineData("Merges branch data into the report")]
    [InlineData("Revert \"Merge branch 'main' into feature/x\"")]
    public void Everything_else_still_has_to_reach_the_branch(string message)
    {
        Assert.False(Commit(message).IsBranchMergeCommit);
    }

    [Fact]
    public void An_unmerged_branch_merge_commit_does_not_make_a_pull_request_unmerged()
    {
        // The case the user hits: the only thing missing is a "Merge branch" commit.
        var pr = new MergingPullRequest
        {
            Commits =
            {
                Commit("FXCPMGR : 1631102 : real work", merged: true),
                Commit("Merge branch 'main' into feature/x", merged: false)
            }
        };

        Assert.Single(pr.RequiredCommits);
        Assert.Equal(0, pr.MissingCommitCount);
    }

    [Fact]
    public void A_genuinely_missing_commit_is_still_counted()
    {
        var pr = new MergingPullRequest
        {
            Commits =
            {
                Commit("FXCPMGR : 1631102 : real work", merged: false),
                Commit("Merge branch 'main' into feature/x", merged: false)
            }
        };

        Assert.Single(pr.RequiredCommits);
        Assert.Equal(1, pr.MissingCommitCount);
    }
}

/// <summary>
/// The comparison behind the QA-branch filter. TFS returns a pull request's target as a full
/// ref name while Settings stores a bare branch, so the two have to meet in the middle.
/// </summary>
public class GitRefNameTests
{
    [Theory]
    [InlineData("refs/heads/main", "main")]
    [InlineData("main", "refs/heads/main")]
    [InlineData("refs/heads/main", "refs/heads/main")]
    [InlineData("refs/heads/dev-qa-net10", "DEV-QA-NET10")]
    [InlineData("refs/heads/FXCPMGR/PI2026_5_2/x", "FXCPMGR/PI2026_5_2/x")]
    public void A_ref_matches_its_branch_with_or_without_the_prefix(string refName, string branch)
    {
        Assert.True(GitRefName.Matches(refName, branch));
    }

    [Theory]
    // The exact case from the sheet: a pull request targeting a feature branch is not the QA branch.
    [InlineData("refs/heads/FXCPMGR/PI2026_4_4/1608055_excel_request_deliverymode", "main")]
    [InlineData("refs/heads/dev-qa-net8", "dev-qa-net10")]
    [InlineData("refs/heads/main", "mai")]
    [InlineData("refs/heads/main", "")]
    [InlineData("refs/heads/main", null)]
    public void A_different_branch_does_not_match(string refName, string? branch)
    {
        Assert.False(GitRefName.Matches(refName, branch!));
    }

    [Fact]
    public void Strip_removes_only_the_heads_prefix()
    {
        Assert.Equal("main", GitRefName.Strip("refs/heads/main"));
        Assert.Equal("main", GitRefName.Strip("main"));
        Assert.Equal("refs/tags/v1", GitRefName.Strip("refs/tags/v1"));
    }
}
