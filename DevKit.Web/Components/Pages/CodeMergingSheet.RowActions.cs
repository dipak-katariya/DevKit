using System.Globalization;
using DevKit.Web.Models;
using DevKit.Web.Services;
using Microsoft.JSInterop;

namespace DevKit.Web.Components.Pages;

/// <summary>
/// The row buttons that act on one work item or pull request: Copy details, which puts rows of the team's merge sheet
/// on the clipboard, and Update status, which writes the tags and field set up in Settings after showing what changes.
/// </summary>
public sealed partial class CodeMergingSheet
{
    private const string UpdateButtonId = "cmStatusUpdate";
    private const string CopyRichCall = "devKit.copyRichAfterClick";
    private const string DropRichCopyCall = "devKit.dropRichCopy";
    private const string FocusCall = "devKit.focusInDialog";
    private const string ChangesId = "cmStatusChanges";

    /// <summary>
    /// The team's zone, in which a copied DATE is the day the pull request was merged, wherever DevKit runs. The same
    /// offset as the audit dates, so the two never disagree on a day.
    /// </summary>
    private static readonly TimeZoneInfo TeamZone =
        TimeZoneInfo.CreateCustomTimeZone("DevKit team", TfsApiService.IstOffset, "India", "India");

    private StatusUpdateDialog? statusDialog;

    // Set once the dialog has something to update, so the next render puts focus on Update and Enter confirms.
    private bool focusUpdateButton;

    private void OnSettingsChanged() => _ = InvokeAsync(StateHasChanged);

    /// <summary>Update status as the row buttons show it, read each render; a save anywhere redraws the page (OnSettingsChanged).</summary>
    private MergingRow.StatusButton StatusUpdateButton
    {
        get
        {
            var settings = Settings.StatusUpdate;
            return settings.HasWork()
                ? new MergingRow.StatusButton(true, $"Update status: {settings.Describe()}. Shows what changes first.")
                : MergingRow.StatusButton.NotSetUp;
        }
    }

    private Task CopyRowDetails(RequirementMergingRow row) =>
        CopyAsync(new CopyTarget(row, filter.VisiblePrs(row), MergingRow.RowCopyKey(row),
            () => row.CopyAction, action => row.CopyAction = action));

    private Task CopyPrDetails((RequirementMergingRow Row, MergingPullRequest Pr) target) =>
        CopyAsync(new CopyTarget(target.Row, new[] { target.Pr }, MergingRow.PrCopyKey(target.Row, target.Pr),
            () => target.Row.PrCopyAction(target.Pr), action => target.Row.SetPrCopyAction(target.Pr, action)));

    /// <summary>
    /// Puts the sheet rows of the target's pull requests on the clipboard. The work item's commits are read first when
    /// they are not yet, through the read opening the row uses, so a read already running is awaited. Nothing is copied
    /// while a pull request's commits could not be read: an empty Commit IDs cell would read as a pull request without
    /// commits. The clipboard write itself was started by app.js on the click, so the wait does not cost the browser's
    /// permission to copy; every path that copies nothing drops it.
    /// </summary>
    private async Task CopyAsync(CopyTarget target)
    {
        if (target.Current().IsBusy)
        {
            return;
        }

        var row = target.Row;
        var hadToRead = !row.CommitsLoaded || row.CommitsError is not null;

        // Emptied first, so the same result twice in a row is still read out.
        announcement = null;
        target.Show(new RowAction(RowActionState.Busy, hadToRead ? "Reading the commits…" : "Copying…"));
        await ReadRowCommitsAsync(row);

        // Left mid-read, the commits are not all in, and the clipboard would get rows that look complete.
        if (page.Token.IsCancellationRequested)
        {
            await DropRichCopyAsync(target.Key);
            return;
        }

        var refusal = MergeSheetCopy.Refusal(target.Prs);
        var rows = refusal is null ? MergingSheetRows.For(row, target.Prs, RequirementName(row), TeamZone) : new();
        if (refusal is not null || rows.Count == 0)
        {
            await DropRichCopyAsync(target.Key);
            ShowCopyResult(target, RowActionState.Failed, refusal ?? MergeSheetCopy.NothingToCopy);
            return;
        }

        if (!await CopyToClipboardAsync(target, rows))
        {
            Log.LogWarning("The browser refused to copy {Rows} merge sheet rows of work item {WorkItemId}; commits read first: {HadToRead}",
                rows.Count, row.WorkItemId, hadToRead);
            ShowCopyResult(target, RowActionState.Failed, MergeSheetCopy.BrowserRefused);
            return;
        }

        var withoutOwnCommits = MergeSheetCopy.WithoutOwnCommits(target.Prs);
        foreach (var pr in withoutOwnCommits)
        {
            Log.LogInformation("Pull request {PullRequestId} of work item {WorkItemId} was copied with an empty Commit IDs cell: it has only merge or rebase commits",
                pr.PullRequestId, row.WorkItemId);
        }

        Log.LogInformation("Copied {Rows} merge sheet rows for work item {WorkItemId}", rows.Count, row.WorkItemId);
        await ClearAfterAsync(target,
            ShowCopyResult(target, RowActionState.Done, MergeSheetCopy.Copied(rows.Count, row.WorkItemId, withoutOwnCommits)));
    }

    /// <summary>The work item as REQ/CHR TFS ID shows it, with the same team Branch Creator puts in the pull request title.</summary>
    private string RequirementName(RequirementMergingRow row)
    {
        var team = string.IsNullOrWhiteSpace(Settings.TeamName) ? selectedArea?.Name : Settings.TeamName;
        return BranchNaming.PullRequestName(team, row.WorkItemId.ToString(CultureInfo.InvariantCulture), row.Title);
    }

    private RowAction ShowCopyResult(CopyTarget target, RowActionState state, string message)
    {
        var result = new RowAction(state, message);
        target.Show(result);
        announcement = message;
        return result;
    }

    /// <summary>False when the browser would not take it; a closed page copies nothing and answers false too.</summary>
    private async Task<bool> CopyToClipboardAsync(CopyTarget target, List<MergingSheetRows.Row> rows)
    {
        try
        {
            return await JS.InvokeAsync<bool>(CopyRichCall, target.Key, MergingSheetRows.ToText(rows), MergingSheetRows.ToHtml(rows));
        }
        catch (JSDisconnectedException ex)
        {
            Log.LogDebug(ex, "The page closed before the merge sheet rows of work item {WorkItemId} were copied", target.Row.WorkItemId);
            return false;
        }
        catch (JSException ex)
        {
            Log.LogError(ex, "The browser could not copy the merge sheet rows of work item {WorkItemId}", target.Row.WorkItemId);
            return false;
        }
    }

    /// <summary>Ends the clipboard write the click started, so nothing reaches the clipboard. A failure leaves it to time out.</summary>
    private async Task DropRichCopyAsync(string key)
    {
        try
        {
            await JS.InvokeVoidAsync(DropRichCopyCall, key);
        }
        catch (JSDisconnectedException ex)
        {
            Log.LogDebug(ex, "The page closed before an unused copy {CopyKey} was dropped", key);
        }
        catch (JSException ex)
        {
            Log.LogError(ex, "The browser could not drop the unused copy {CopyKey}", key);
        }
    }

    /// <summary>Puts the button back to ready after its tick, unless something newer has replaced it meanwhile.</summary>
    private async Task ClearAfterAsync(CopyTarget target, RowAction shown)
    {
        StateHasChanged();
        try
        {
            await Task.Delay(CopyFeedback.TickMs, page.Token);
        }
        catch (OperationCanceledException ex) when (page.Token.IsCancellationRequested)
        {
            Log.LogDebug(ex, "The page was left before a copy button went back to ready");
            return;
        }

        if (ReferenceEquals(target.Current(), shown))
        {
            target.Show(RowAction.Idle);
        }
    }

    /// <summary>Opens the dialog and reads the work item, so it shows what Update would write to it now.</summary>
    private async Task OpenStatusUpdate(RequirementMergingRow row)
    {
        var settings = Settings.StatusUpdate;
        if (!settings.HasWork() || statusDialog is not null || row.StatusAction.IsBusy)
        {
            return;
        }

        var dialog = statusDialog = new StatusUpdateDialog(row, settings);
        focusUpdateButton = false;
        StateHasChanged();
        try
        {
            var plan = await StatusUpdates.PreviewAsync(row.WorkItemId, settings, page.Token);
            dialog.ShowPlan(plan);

            // A dialog closed while it read is gone: its answer must not move focus in one opened since.
            focusUpdateButton = ReferenceEquals(statusDialog, dialog) && plan.HasChanges;
        }
        catch (OperationCanceledException ex) when (page.Token.IsCancellationRequested)
        {
            Log.LogDebug(ex, "The page was left while work item {WorkItemId} was read for Update status", row.WorkItemId);
        }
        catch (Exception ex)
        {
            Log.LogError(ex, "Work item {WorkItemId} could not be read for Update status", row.WorkItemId);
            dialog.ShowReadFailure($"Couldn't read #{row.WorkItemId}: {TfsErrors.Describe(ex)}");
        }
    }

    /// <summary>
    /// Writes what the dialog showed. A write started by a click takes no page token: leaving the page must not cut it
    /// in half. The dialog closes once it is written, or when there turns out to be nothing to change. When the work
    /// item changed in another way meanwhile, it stays open with the new change for another click. A failure stays in
    /// it, with the reason.
    /// </summary>
    private async Task ConfirmStatusUpdate()
    {
        if (statusDialog is not { CanConfirm: true, Plan: { } plan } dialog)
        {
            return;
        }

        dialog.StartSaving();
        StateHasChanged();
        try
        {
            var (message, closes) = dialog.Finish(await StatusUpdates.ApplyAsync(plan, dialog.Settings, CancellationToken.None));
            announcement = message;
            if (closes)
            {
                statusDialog = null;
            }
        }
        catch (Exception ex)
        {
            Log.LogError(ex, "Update status failed for work item {WorkItemId}", dialog.Row.WorkItemId);
            announcement = dialog.Fail(ex);
        }
    }

    /// <summary>Closes the dialog, except while it writes: the result is shown when it is done.</summary>
    private void CloseStatusDialog()
    {
        if (statusDialog is { Saving: true })
        {
            return;
        }

        statusDialog = null;
        focusUpdateButton = false;
    }

    /// <summary>The field's line in the dialog: what it becomes, that it is already that, or why it is left alone.</summary>
    private static string StatusFieldLine(StatusFieldChange field) => field.Describe();

    /// <summary>
    /// Moves focus to Update once the dialog can update, so Enter confirms, but only while focus is still on the dialog
    /// itself: someone who has moved to Cancel keeps it there. A failure only loses that help.
    /// </summary>
    private async Task FocusUpdateButtonAsync()
    {
        if (!focusUpdateButton || statusDialog is not { Reading: false })
        {
            return;
        }

        focusUpdateButton = false;
        try
        {
            await JS.InvokeVoidAsync(FocusCall, UpdateButtonId);
        }
        catch (JSDisconnectedException ex)
        {
            Log.LogDebug(ex, "The page closed before focus moved to Update");
        }
        catch (JSException ex)
        {
            Log.LogError(ex, "Focus could not move to the Update button");
        }
    }

    /// <summary>One Copy details click: the work item, the pull requests it copies, and its button's state.</summary>
    /// <param name="Key">The button's <c>data-rich-copy</c> value, which ties the copy to the write app.js started on the click.</param>
    private sealed record CopyTarget(
        RequirementMergingRow Row, IReadOnlyList<MergingPullRequest> Prs, string Key, Func<RowAction> Current, Action<RowAction> Show);
}
