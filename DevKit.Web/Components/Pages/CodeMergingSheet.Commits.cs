using DevKit.Web.Models;

namespace DevKit.Web.Components.Pages;

/// <summary>
/// The deferred commit reads and opening a row: one shared read per row, used by opening it, Copy details and
/// Load all, and the re-sort that read puts off until the person next changes the view.
/// </summary>
public sealed partial class CodeMergingSheet
{
    private bool loadingRest;

    /// <summary>Rows Load all reads at once.</summary>
    private const int LoadAllChunkRows = 5;

    // The commit read running for each row, so opening, copying and Load all share one instead of each starting one.
    private readonly Dictionary<RequirementMergingRow, Task> rowReads = new(ReferenceEqualityComparer.Instance);

    // Set when commits were read without the re-sort that their dates may call for; the next refilter applies it.
    private bool refilterPending;

    private int PendingRowCount => bundle.Requirements.Count(r => !r.CommitsLoaded);

    private int LoadedRowCount => bundle.Requirements.Count - PendingRowCount;

    /// <summary>Pending rows whose commit read failed rather than was put off; Load all reads them again.</summary>
    private int FailedRowCount => bundle.Requirements.Count(r => r.CommitsError is not null);

    private string PendingTooltip =>
        $"Commits are loaded for {LoadedRowCount} work item(s). The other {PendingRowCount} load when " +
        "you open them, or load them all now." +
        (FailedRowCount > 0 ? $" {FailedRowCount} of them could not be read and are tried again." : "");

    /// <summary>
    /// Fills in every deferred row through the same read as opening a row, so a row being opened or copied meanwhile
    /// is not read twice. Bounded concurrency rather than one big WhenAll: the rest of a large sprint can be hundreds
    /// of pull requests, and the throttle is shared with whatever else the page is doing. The rows are sorted and
    /// filtered again once, at the end, rather than reshuffling under the reader after every chunk.
    /// </summary>
    private async Task LoadRemainingCommits()
    {
        if (loadingRest)
        {
            return;
        }
        loadingRest = true;
        try
        {
            foreach (var chunk in bundle.Requirements.Where(r => !r.CommitsLoaded).Chunk(LoadAllChunkRows))
            {
                if (page.Token.IsCancellationRequested)
                {
                    break;
                }

                await Task.WhenAll(chunk.Select(ReadRowCommitsAsync));
                StateHasChanged();
            }
        }
        finally
        {
            loadingRest = false;
            Refilter();
        }
    }

    private async Task ToggleRow(RequirementMergingRow row)
    {
        row.IsExpanded = !row.IsExpanded;
        if (!row.IsExpanded)
        {
            expandedPrKey = null;
            RefilterIfPending();
            return;
        }

        // Rows past the eager batch have no commits yet; read them before verifying, or the
        // check would run against an empty list and report the row as having nothing to merge.
        await LoadRowCommits(row);

        // Opening a row refreshes its merge status, so the sheet stays current without a reload.
        await ReverifyRow(row);
    }

    /// <summary>
    /// Reads one deferred row's commits, with the spinner in its Commits cell meanwhile. Their dates can move the row
    /// in the commit-wise order, so the rows are sorted and filtered again after, also when an earlier Copy details
    /// read them and left the order as it was.
    /// </summary>
    private async Task LoadRowCommits(RequirementMergingRow row)
    {
        await ReadRowCommitsAsync(row);
        RefilterIfPending();
    }

    /// <summary>Applies the re-sort a read put off, at a moment the person chose to change the view.</summary>
    private void RefilterIfPending()
    {
        if (refilterPending)
        {
            Refilter();
        }
    }

    /// <summary>
    /// The one commit read of a row, shared by opening it, Copy details and Load all: a read already running is awaited
    /// rather than started again. It leaves the order as it is, so a row being copied stays under the pointer; the
    /// re-sort waits for the next refilter. A row marked loaded is read again when one of its pull requests has failed
    /// since, through another row that shares it, so retrying Copy details can clear that failure.
    /// </summary>
    private Task ReadRowCommitsAsync(RequirementMergingRow row)
    {
        if (rowReads.TryGetValue(row, out var running))
        {
            return running;
        }

        if (row.CommitsLoaded && row.CommitsError is null)
        {
            return Task.CompletedTask;
        }

        row.CommitsLoaded = false;
        var read = ReadOnceAsync(row);
        if (!read.IsCompleted)
        {
            rowReads[row] = read;
        }
        return read;
    }

    private async Task ReadOnceAsync(RequirementMergingRow row)
    {
        row.IsLoadingCommits = true;
        StateHasChanged();
        try
        {
            await Merging.LoadRowCommitsAsync(row, page.Token);
            refilterPending = true;
        }
        catch (OperationCanceledException ex) when (page.Token.IsCancellationRequested)
        {
            Log.LogDebug(ex, "Commits of work item {WorkItemId} stopped loading because the page was left", row.WorkItemId);
        }
        finally
        {
            row.IsLoadingCommits = false;
            rowReads.Remove(row);
        }
    }

    // Only one pull request's commits are on screen at a time. A single nullable key is
    // simpler state than a dictionary of booleans, and keeps the rendered tree small.
    private void TogglePr(string key) => expandedPrKey = expandedPrKey == key ? null : key;
}
