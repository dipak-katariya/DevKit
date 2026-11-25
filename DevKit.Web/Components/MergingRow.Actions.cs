using DevKit.Web.Models;
using Microsoft.AspNetCore.Components;

namespace DevKit.Web.Components;

/// <summary>
/// The row's Copy details and Update status buttons, and Copy details on each of its pull requests: what they take
/// from the page, and how each shows where its last click stands.
/// </summary>
public partial class MergingRow
{
    /// <summary>Update status as every row shows it, from what Settings holds for it.</summary>
    /// <param name="Enabled">Whether Settings names tags to add or a field to set.</param>
    /// <param name="Title">What a click does, for the tooltip.</param>
    public sealed record StatusButton(bool Enabled, string Title)
    {
        public static readonly StatusButton NotSetUp = new(false,
            "Update status is not set up. Choose its tags and field in Settings, under Code Merging Sheet: Update Status.");
    }

    [Parameter] public StatusButton Status { get; set; } = StatusButton.NotSetUp;

    [Parameter] public EventCallback<RequirementMergingRow> OnCopyRow { get; set; }

    [Parameter] public EventCallback<(RequirementMergingRow Row, MergingPullRequest Pr)> OnCopyPr { get; set; }

    [Parameter] public EventCallback<RequirementMergingRow> OnUpdateStatus { get; set; }

    // Set with the roll-up in OnParametersSet, since the actions cell reads it twice.
    private bool canCompareRow;

    private int CopyablePrCount => VisiblePrs.Count(pr => !pr.IsAbandoned);

    private string CopyRowTitle
    {
        get
        {
            if (Row.CopyAction.State != RowActionState.Idle)
            {
                return Row.CopyAction.Message;
            }

            var copyable = CopyablePrCount;
            if (copyable == 0)
            {
                return VisiblePrCount == 0 ? "No pull requests to copy" : "Its pull requests were abandoned, so there is nothing to copy";
            }

            var abandoned = VisiblePrCount - copyable;
            return $"Copy details: {Plural(copyable, "row")} for the merge sheet, one per pull request. Paste into the Name cell of an empty row."
                + (abandoned > 0 ? $" {Plural(abandoned, "abandoned pull request")} left out." : "");
        }
    }

    private string StatusTitle => Row.StatusAction.State == RowActionState.Idle
        ? Status.Title
        : $"{Row.StatusAction.Message}\n{Status.Title}";

    private static string CopyPrTitle(RequirementMergingRow row, MergingPullRequest pr)
    {
        if (pr.IsAbandoned)
        {
            return "Abandoned, so it has no row in the merge sheet";
        }

        if (row.PrCopyAction(pr) is { State: not RowActionState.Idle } action)
        {
            return action.Message;
        }

        return pr.IsCompleted
            ? $"Copy details: the merge sheet row of !{pr.PullRequestId}"
            : $"Copy details: the merge sheet row of !{pr.PullRequestId}. It is not merged yet, so DATE stays empty.";
    }

    /// <summary>The row button's key for the clipboard write app.js starts on the click (<c>data-rich-copy</c>).</summary>
    public static string RowCopyKey(RequirementMergingRow row) => $"wi-{row.WorkItemId}";

    /// <summary>A pull request button's key: per row, since the same pull request can sit under two work items.</summary>
    public static string PrCopyKey(RequirementMergingRow row, MergingPullRequest pr) => $"wi-{row.WorkItemId}-{PrKey(pr)}";

    private static string ActionIcon(RowAction action, string idle) => action.State switch
    {
        RowActionState.Done => Icons.Check,
        RowActionState.Failed => Icons.AlertTriangle,
        _ => idle
    };

    // A button at work stays enabled, so keyboard focus stays on it; the page ignores a second click meanwhile.
    private static string? BusyAttribute(RowAction action) => action.IsBusy ? "true" : null;

    private static string ActionClass(RowAction action) => action.State switch
    {
        RowActionState.Done => "cm-act-done",
        RowActionState.Failed => "cm-act-failed",
        _ => ""
    };
}
