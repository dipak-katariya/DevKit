using System.Net;
using DevKit.Web.Models;
using DevKit.Web.Services;
using Xunit;

namespace DevKit.Tests;

/// <summary>
/// What Copy details lets onto the clipboard and what it says afterwards, where a pull request's copy state is kept,
/// and how the Update status dialog acts on each answer of the write.
/// </summary>
public sealed class MergingCopyAndStatusDialogTests
{
    private static MergingPullRequest Pr(int id, string status = "completed", string? commitsError = null, bool withCommit = true) => new()
    {
        PullRequestId = id,
        RepositoryId = "repo",
        Status = status,
        CommitsError = commitsError,
        Commits = withCommit ? new List<MergingCommit> { new() { CommitId = "abc" + id, Message = "Fix the login page" } } : new()
    };

    private static RequirementMergingRow Row() => new() { WorkItemId = 1001, Title = "Login page" };

    private static StatusUpdatePlan Plan(bool changes = true) => new(
        new WorkItemForUpdate(1001, 7, "Contoso", "Requirement", "Login page", "", "In Dev"),
        changes ? new[] { "PT Merged" } : Array.Empty<string>(),
        null);

    private static StatusUpdateDialog ReadDialog(StatusUpdatePlan? plan = null)
    {
        var dialog = new StatusUpdateDialog(Row(), new StatusUpdateSettings());
        dialog.ShowPlan(plan ?? Plan());
        return dialog;
    }

    [Fact]
    public void A_failed_commit_read_on_a_pull_request_that_is_not_abandoned_refuses_the_copy()
    {
        var refusal = MergeSheetCopy.Refusal(new[] { Pr(1), Pr(2, commitsError: "TFS took too long to answer.") });

        Assert.Equal("The commits of !2 could not be read, so nothing was copied. Click Copy details again to try again.", refusal);
    }

    [Fact]
    public void An_abandoned_pull_request_with_a_failed_read_does_not_block_the_others()
    {
        Assert.Null(MergeSheetCopy.Refusal(new[] { Pr(1), Pr(2, "abandoned", "TFS took too long to answer.") }));
    }

    [Fact]
    public void A_pull_request_without_commits_of_its_own_is_named_in_the_copy_message()
    {
        var without = MergeSheetCopy.WithoutOwnCommits(new[] { Pr(1), Pr(2, withCommit: false), Pr(3, "abandoned", withCommit: false) });

        Assert.Equal(2, Assert.Single(without).PullRequestId);
        Assert.Equal(
            "Copied 2 rows for #1001. Click the Name cell of an empty row in your sheet and paste. Pull request !2 has only merge or rebase commits, so its Commit IDs is empty.",
            MergeSheetCopy.Copied(2, 1001, without));
        Assert.Equal("Copied 1 row for #1001. Click the Name cell of an empty row in your sheet and paste.",
            MergeSheetCopy.Copied(1, 1001, Array.Empty<MergingPullRequest>()));
    }

    [Fact]
    public void A_pull_request_under_two_work_items_keeps_a_copy_state_in_each_row()
    {
        var shared = Pr(500);
        var first = Row();
        var second = new RequirementMergingRow { WorkItemId = 1002 };

        first.SetPrCopyAction(shared, new RowAction(RowActionState.Busy, "Copying…"));

        Assert.True(first.PrCopyAction(shared).IsBusy);
        Assert.Same(RowAction.Idle, second.PrCopyAction(shared));
    }

    [Fact]
    public void The_dialog_is_reading_until_the_plan_comes_and_only_then_can_confirm()
    {
        var dialog = new StatusUpdateDialog(Row(), new StatusUpdateSettings());
        Assert.Equal(StatusDialogPhase.Reading, dialog.Phase);
        Assert.False(dialog.CanConfirm);

        dialog.ShowPlan(Plan());
        Assert.True(dialog.CanConfirm);

        dialog.StartSaving();
        Assert.True(dialog.Saving);
        Assert.False(dialog.CanConfirm);
        Assert.True(dialog.Row.StatusAction.IsBusy);
    }

    [Fact]
    public void A_plan_with_nothing_to_write_cannot_be_confirmed()
    {
        Assert.False(ReadDialog(Plan(changes: false)).CanConfirm);
    }

    [Fact]
    public void Written_closes_the_dialog_and_the_row_says_what_was_written()
    {
        var dialog = ReadDialog();
        dialog.StartSaving();

        var (announcement, closes) = dialog.Finish(new StatusUpdateOutcome(StatusUpdateResult.Written, Plan()));

        Assert.True(closes);
        Assert.Equal("Updated #1001: added PT Merged.", announcement);
        Assert.Equal(RowActionState.Done, dialog.Row.StatusAction.State);
        Assert.Equal(StatusDialogPhase.Ready, dialog.Phase);
    }

    [Fact]
    public void Nothing_to_change_closes_the_dialog_and_says_nothing_was_written()
    {
        var dialog = ReadDialog();
        dialog.StartSaving();

        var (announcement, closes) = dialog.Finish(new StatusUpdateOutcome(StatusUpdateResult.NothingToChange, Plan(changes: false)));

        Assert.True(closes);
        Assert.Equal("#1001 already has these values. Nothing was written.", announcement);
        Assert.Equal(RowActionState.Done, dialog.Row.StatusAction.State);
    }

    [Fact]
    public void Changed_meanwhile_keeps_the_dialog_open_with_the_new_plan_for_another_click()
    {
        var dialog = ReadDialog();
        dialog.StartSaving();
        var fresh = Plan();

        var (announcement, closes) = dialog.Finish(new StatusUpdateOutcome(StatusUpdateResult.ChangedMeanwhile, fresh));

        Assert.False(closes);
        Assert.Same(fresh, dialog.Plan);
        Assert.Equal(announcement, dialog.Error);
        Assert.Contains("changed after it was read", announcement, StringComparison.Ordinal);
        Assert.Same(RowAction.Idle, dialog.Row.StatusAction);
        Assert.True(dialog.CanConfirm);
    }

    [Fact]
    public void A_write_whose_answer_never_came_is_reported_as_not_sure()
    {
        var dialog = ReadDialog();
        dialog.StartSaving();

        var timeout = new TaskCanceledException("timed out");
        TfsErrors.NoteWriteSent(timeout);

        var error = dialog.Fail(timeout);

        Assert.StartsWith("Not sure #1001 was updated:", error, StringComparison.Ordinal);
        Assert.Equal(RowActionState.Failed, dialog.Row.StatusAction.State);
        Assert.True(dialog.CanConfirm);
    }

    [Fact]
    public void A_refused_write_is_reported_as_not_updated_with_the_reason()
    {
        var dialog = ReadDialog();
        dialog.StartSaving();

        var error = dialog.Fail(new HttpRequestException("refused", null, HttpStatusCode.Forbidden));

        Assert.StartsWith("Not updated:", error, StringComparison.Ordinal);
        Assert.Contains("403", error, StringComparison.Ordinal);
        Assert.Equal(error, dialog.Error);
        Assert.Equal(StatusDialogPhase.Ready, dialog.Phase);
    }
}
