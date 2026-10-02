using DevKit.Web.Models;
using Xunit;

namespace DevKit.Tests;

/// <summary>
/// Deciding whether a work item already points at a branch is what keeps a re-created branch from
/// being reported as a link failure: TFS rejects a duplicate relation, and a work item keeps its
/// branch link after the branch is deleted.
/// </summary>
public class WorkItemBranchLinkTests
{
    private const string RepoA = "8a1b2c3d-4e5f-6071-8293-a4b5c6d7e8f9";
    private const string RepoB = "11112222-3333-4444-5555-666677778888";
    private const string Branch = "FXCPMGR/PI2026_5_2/1631102_excel_to_cpfoia_migration_verify";

    private static WorkItemBranchLink Link(string repoId = RepoA, string branch = Branch) =>
        new() { RepositoryId = repoId, BranchName = branch };

    [Fact]
    public void Matches_the_same_branch_in_the_same_repository()
    {
        Assert.True(Link().Matches(RepoA, Branch));
    }

    [Fact]
    public void Ignores_case_in_both_the_repository_id_and_the_branch()
    {
        Assert.True(Link().Matches(RepoA.ToUpperInvariant(), Branch.ToUpperInvariant()));
    }

    [Fact]
    public void Tolerates_surrounding_whitespace_on_the_branch()
    {
        Assert.True(Link().Matches(RepoA, $"  {Branch}  "));
    }

    [Fact]
    public void Does_not_match_the_same_branch_name_in_a_different_repository()
    {
        // Two repositories genuinely hold branches of this name, and both are linked to the work
        // item — treating them as one would suppress a link that still needs creating.
        Assert.False(Link().Matches(RepoB, Branch));
    }

    [Fact]
    public void Does_not_match_a_different_branch_in_the_same_repository()
    {
        Assert.False(Link().Matches(RepoA, "FXCPMGR/PI2026_5_2/1631102_something_else"));
    }

    [Theory]
    [InlineData(null, Branch)]
    [InlineData("", Branch)]
    [InlineData(RepoA, null)]
    [InlineData(RepoA, "")]
    [InlineData(RepoA, "   ")]
    public void Missing_inputs_never_count_as_a_match(string? repoId, string? branch)
    {
        Assert.False(Link().Matches(repoId, branch));
    }

    [Fact]
    public void Linked_and_already_linked_both_count_as_linked()
    {
        Assert.True(WorkItemLinkResult.Linked().IsLinked);
        Assert.True(WorkItemLinkResult.AlreadyLinked().IsLinked);
        Assert.False(WorkItemLinkResult.Failed("nope").IsLinked);
    }

    [Fact]
    public void Unlinked_and_not_linked_both_count_as_unlinked()
    {
        Assert.True(WorkItemLinkResult.Unlinked().IsUnlinked);
        Assert.True(WorkItemLinkResult.NotLinked().IsUnlinked);
        Assert.False(WorkItemLinkResult.Failed("nope").IsUnlinked);
    }

    [Fact]
    public void A_failed_removal_is_neither_linked_nor_unlinked()
    {
        // The work item's state is unknown after a failure, so neither claim may be made.
        var failed = WorkItemLinkResult.Failed("TFS request failed (412 Precondition Failed)");

        Assert.False(failed.IsLinked);
        Assert.False(failed.IsUnlinked);
    }

    [Fact]
    public void Adding_and_removing_never_report_the_same_outcome()
    {
        Assert.NotEqual(WorkItemLinkResult.Linked().Outcome, WorkItemLinkResult.Unlinked().Outcome);
        Assert.NotEqual(WorkItemLinkResult.AlreadyLinked().Outcome, WorkItemLinkResult.NotLinked().Outcome);
    }

    [Fact]
    public void A_link_is_not_treated_as_stale_until_it_is_actually_checked()
    {
        // Default false covers "repository not loaded", which is what keeps an unverified link
        // from being offered for deletion.
        Assert.False(Link().BranchMissing);
    }

    [Fact]
    public void A_failure_carries_the_reason_so_the_ui_can_show_it()
    {
        var result = WorkItemLinkResult.Failed("TFS request failed (404 Not Found)");

        Assert.Equal(WorkItemLinkOutcome.Failed, result.Outcome);
        Assert.Contains("404", result.Detail);
    }
}
