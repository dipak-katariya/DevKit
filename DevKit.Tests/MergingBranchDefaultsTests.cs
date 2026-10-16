using DevKit.Web.Components;
using DevKit.Web.Models;
using DevKit.Web.Services;
using Xunit;

namespace DevKit.Tests;

/// <summary>
/// The branches the Code Merging Sheet starts on when Settings has none for a repository: the newest release branch
/// as the merging target, main as the QA branch, and the repository's own default branch when there is no main.
/// </summary>
public sealed class MergingBranchDefaultsTests
{
    private static readonly string[] Branches =
    {
        "develop", "main", "release/cp-26.4.20", "release/cp-26.5.9", "release/cp-26.5.10", "release/notes", "feature/x"
    };

    [Fact]
    public void The_merging_target_is_the_release_branch_with_the_highest_version()
    {
        Assert.Equal("release/cp-26.5.10", MergingBranchDefaults.MergingTarget(Branches, "main"));
    }

    [Fact]
    public void Of_two_release_branches_with_the_same_version_the_one_directly_under_release_is_taken()
    {
        var branches = new[] { "release/CG/APP_26.4.1.2", "release/APP_26.4.1.2", "release/APP_26.4.1.1", "release/APP-hotfix" };

        Assert.Equal("release/APP_26.4.1.2", MergingBranchDefaults.NewestRelease(branches));
    }

    [Fact]
    public void Release_branches_without_a_version_are_never_picked()
    {
        Assert.Null(MergingBranchDefaults.NewestRelease(new[] { "release/notes", "release/next", "main" }));
    }

    [Fact]
    public void Without_a_release_branch_the_merging_target_is_main()
    {
        Assert.Equal("main", MergingBranchDefaults.MergingTarget(new[] { "develop", "main" }, "develop"));
    }

    [Theory]
    [InlineData("refs/heads/develop")]
    [InlineData("develop")]
    public void Without_main_both_fall_back_to_the_repositorys_default_branch(string repoDefault)
    {
        var branches = new[] { "develop", "feature/x" };

        Assert.Equal("develop", MergingBranchDefaults.QaBranch(branches, repoDefault));
        Assert.Equal("develop", MergingBranchDefaults.MergingTarget(branches, repoDefault));
    }

    [Fact]
    public void The_qa_branch_is_main_as_the_list_spells_it()
    {
        Assert.Equal("Main", MergingBranchDefaults.QaBranch(new[] { "Main", "develop" }, "develop"));
    }

    [Fact]
    public void A_list_with_neither_main_nor_the_default_branch_gives_nothing()
    {
        Assert.Equal("", MergingBranchDefaults.QaBranch(new[] { "feature/x" }, "develop"));
        Assert.Equal("", MergingBranchDefaults.QaBranch(Array.Empty<string>(), ""));
    }

    private static RepoBranchSetup.RepoBranchRow Row(string savedQa = "", string savedMerging = "") => new()
    {
        Repo = new TfsRepo { Id = "r1", Name = "Repo", DefaultBranch = "main" },
        Branches = Branches.ToList(),
        QaBranch = savedQa,
        MergingBranch = savedMerging,
        SavedQaBranch = savedQa,
        SavedMergingBranch = savedMerging
    };

    [Fact]
    public void A_row_with_nothing_saved_starts_on_the_automatic_branches_and_counts_as_default()
    {
        var row = Row();

        row.ApplyAutomaticDefaults();

        Assert.Equal("main", row.QaBranch);
        Assert.Equal("release/cp-26.5.10", row.MergingBranch);
        Assert.True(row.MatchesSaved);
    }

    [Fact]
    public void A_row_keeps_the_branches_saved_in_Settings()
    {
        var row = Row(savedQa: "develop", savedMerging: "release/cp-26.4.20");

        row.ApplyAutomaticDefaults();

        Assert.Equal("develop", row.QaBranch);
        Assert.Equal("release/cp-26.4.20", row.MergingBranch);
        Assert.True(row.MatchesSaved);
        row.MergingBranch = "release/cp-26.5.10";
        Assert.False(row.MatchesSaved);
    }
}
