using DevKit.Web.Models;
using Xunit;

namespace DevKit.Tests;

/// <summary>
/// Every number on the merging sheet is read through <see cref="MergingSheetFilter.VisiblePrs"/>,
/// so whatever that returns has to stay consistent with the counts, the roll-up and the table.
/// The "All PRs" switch widens it, which is exactly where the two could drift apart.
/// </summary>
public class MergingSheetFilterTests
{
    private const string QaBranch = "refs/heads/main";
    private const string FeatureBranch = "refs/heads/FXCPMGR/PI2026_4_4/1608055_delivery";

    private static MergingPullRequest Pr(int id, string target, int day = 1, string author = "Dipak Katariya") =>
        new()
        {
            PullRequestId = id,
            RepositoryId = "repo-1",
            TargetBranch = target,
            CreatedBy = author,
            CreationDate = new DateTime(2026, 9, day, 10, 0, 0, DateTimeKind.Utc)
        };

    /// <summary>One PR on the QA branch (92599) and an earlier one that missed it (92598).</summary>
    private static RequirementMergingRow Row() => new()
    {
        WorkItemId = 1608055,
        Title = "Delivery mode is blank",
        AssignedTo = "Bhavesh Rajpurohit",
        PullRequests = { Pr(92599, QaBranch, day: 2) },
        OffQaBranchPullRequests = { Pr(92598, FeatureBranch, day: 1) }
    };

    [Fact]
    public void Commit_wise_ordering_is_on_when_the_sheet_opens()
    {
        Assert.True(new MergingSheetFilter().CommitWise);
    }

    [Fact]
    public void All_prs_is_off_when_the_sheet_opens()
    {
        Assert.False(new MergingSheetFilter().ShowAllPrs);
    }

    [Fact]
    public void By_default_only_the_qa_branch_pull_requests_are_visible()
    {
        var visible = new MergingSheetFilter().VisiblePrs(Row());

        Assert.Equal(new[] { 92599 }, visible.Select(p => p.PullRequestId));
    }

    [Fact]
    public void All_prs_reveals_the_ones_that_missed_the_qa_branch()
    {
        var filter = new MergingSheetFilter { ShowAllPrs = true };

        var visible = filter.VisiblePrs(Row());

        Assert.Equal(new[] { 92598, 92599 }, visible.Select(p => p.PullRequestId).OrderBy(i => i));
    }

    [Fact]
    public void Revealed_pull_requests_are_ordered_with_the_rest_not_appended_after_them()
    {
        // 92598 was raised first, so it leads once it is shown — the table is read chronologically.
        var visible = new MergingSheetFilter { ShowAllPrs = true }.VisiblePrs(Row());

        Assert.Equal(92598, visible[0].PullRequestId);
    }

    [Fact]
    public void The_counts_follow_the_switch()
    {
        var rows = new[] { Row() };

        Assert.Equal(1, new MergingSheetFilter().CountPrs(rows));
        Assert.Equal(2, new MergingSheetFilter { ShowAllPrs = true }.CountPrs(rows));
    }

    [Fact]
    public void The_author_filter_still_applies_on_top_of_all_prs()
    {
        var row = new RequirementMergingRow
        {
            WorkItemId = 1,
            PullRequests = { Pr(10, QaBranch, author: "Alice") },
            OffQaBranchPullRequests = { Pr(11, FeatureBranch, author: "Bob") }
        };
        var filter = new MergingSheetFilter { ShowAllPrs = true, Author = "Bob" };

        var visible = filter.VisiblePrs(row);

        Assert.Equal(new[] { 11 }, visible.Select(p => p.PullRequestId));
    }

    [Fact]
    public void The_held_back_count_comes_from_the_kept_pull_requests()
    {
        Assert.Equal(1, Row().PrsOffQaBranch);
        Assert.Equal(0, new RequirementMergingRow { WorkItemId = 2 }.PrsOffQaBranch);
    }

    [Fact]
    public void A_row_with_nothing_held_back_reads_the_same_either_way()
    {
        var row = new RequirementMergingRow { WorkItemId = 3, PullRequests = { Pr(20, QaBranch) } };

        Assert.Equal(
            new MergingSheetFilter().VisiblePrs(row).Select(p => p.PullRequestId),
            new MergingSheetFilter { ShowAllPrs = true }.VisiblePrs(row).Select(p => p.PullRequestId));
    }

    [Fact]
    public void Commit_wise_order_holds_before_the_commits_are_read()
    {
        // Neither row's commits are loaded yet, which is most rows straight after a load: the
        // pull requests' creation stands in for them instead of every row sorting last by id.
        var later = new RequirementMergingRow { WorkItemId = 1, PullRequests = { Pr(10, QaBranch, day: 10) } };
        var earlier = new RequirementMergingRow { WorkItemId = 2, PullRequests = { Pr(20, QaBranch, day: 5) } };

        var ordered = new MergingSheetFilter().Apply(new[] { later, earlier });

        Assert.Equal(new[] { 2, 1 }, ordered.Select(r => r.WorkItemId));
    }

    [Fact]
    public void Commit_wise_order_follows_the_commits_once_they_are_read()
    {
        var raisedLater = new RequirementMergingRow { WorkItemId = 1, PullRequests = { Pr(10, QaBranch, day: 10) } };
        var other = new RequirementMergingRow { WorkItemId = 2, PullRequests = { Pr(20, QaBranch, day: 5) } };
        raisedLater.PullRequests[0].Commits.Add(new MergingCommit
        {
            CommitId = "c1",
            Message = "work",
            Date = new DateTime(2026, 9, 1, 9, 0, 0, DateTimeKind.Utc)
        });

        var ordered = new MergingSheetFilter().Apply(new[] { other, raisedLater });

        Assert.Equal(new[] { 1, 2 }, ordered.Select(r => r.WorkItemId));
    }

    [Fact]
    public void Commit_wise_order_counts_the_pull_requests_all_prs_reveals()
    {
        // Row() has a QA pull request raised on the 2nd and one that missed the QA branch on the 1st.
        var other = new RequirementMergingRow { WorkItemId = 2, PullRequests = { Pr(30, QaBranch, day: 2) } };
        var rows = new[] { other, Row() };

        Assert.Equal(new[] { 2, 1608055 }, new MergingSheetFilter().Apply(rows).Select(r => r.WorkItemId));
        Assert.Equal(new[] { 1608055, 2 }, new MergingSheetFilter { ShowAllPrs = true }.Apply(rows).Select(r => r.WorkItemId));
    }

    [Fact]
    public void Work_items_without_pull_requests_come_last_in_commit_wise_order()
    {
        var none = new RequirementMergingRow { WorkItemId = 1 };
        var some = new RequirementMergingRow { WorkItemId = 2, PullRequests = { Pr(10, QaBranch, day: 20) } };

        var ordered = new MergingSheetFilter().Apply(new[] { none, some });

        Assert.Equal(new[] { 2, 1 }, ordered.Select(r => r.WorkItemId));
    }

    [Fact]
    public void Picking_a_merge_state_leaves_the_other_states_counted()
    {
        var rows = new[] { VerifiedRow(1, merged: true), VerifiedRow(2, merged: false), new RequirementMergingRow { WorkItemId = 3 } };
        var filter = new MergingSheetFilter { MergeState = MergingSheetFilter.Merged };

        var scope = filter.ApplyAllButMergeState(rows);
        var counts = filter.CountByRollup(scope);

        Assert.Equal(new[] { 1 }, filter.Apply(rows).Select(r => r.WorkItemId));
        Assert.Equal(new[] { 1, 2, 3 }, scope.Select(r => r.WorkItemId).OrderBy(i => i));
        Assert.Equal(1, counts[MergeRollup.Merged]);
        Assert.Equal(1, counts[MergeRollup.NotMerged]);
        Assert.Equal(1, counts[MergeRollup.NoPullRequests]);
        Assert.False(counts.ContainsKey(MergeRollup.Partial));
    }

    [Fact]
    public void The_rows_before_the_merge_state_still_follow_every_other_filter()
    {
        var mine = VerifiedRow(1, merged: false, assignee: "Alice");
        var theirs = VerifiedRow(2, merged: false, assignee: "Bob");
        var filter = new MergingSheetFilter { Member = "Alice", MergeState = MergingSheetFilter.Merged };

        Assert.Equal(new[] { 1 }, filter.ApplyAllButMergeState(new[] { mine, theirs }).Select(r => r.WorkItemId));
        Assert.Empty(filter.Apply(new[] { mine, theirs }));
    }

    [Fact]
    public void Rows_with_prs_are_counted_through_the_visible_pull_requests()
    {
        var rows = new[] { Row(), new RequirementMergingRow { WorkItemId = 2 } };

        Assert.Equal(1, new MergingSheetFilter().CountWithPrs(rows));
        Assert.Equal(0, new MergingSheetFilter { Author = "Nobody" }.CountWithPrs(rows));
    }

    private static RequirementMergingRow VerifiedRow(int id, bool merged, string assignee = "Dipak Katariya")
    {
        var pr = Pr(id * 10, QaBranch);
        pr.Status = "completed";
        pr.IsMergedToTargetBranch = merged;
        pr.Commits.Add(new MergingCommit { CommitId = $"c{id}", Message = "work", IsMergedToTargetBranch = merged });
        return new RequirementMergingRow { WorkItemId = id, AssignedTo = assignee, PullRequests = { pr } };
    }
}
