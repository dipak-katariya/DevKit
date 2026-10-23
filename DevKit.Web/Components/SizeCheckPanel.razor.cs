using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using DevKit.Web.Models;
using DevKit.Web.Services;

namespace DevKit.Web.Components;

/// <summary>Requirement sizes checked against the estimates on their child tasks.</summary>
public sealed partial class SizeCheckPanel : IDisposable
{
    /// <summary>Types the grid starts on. Bugs are excluded until the user ticks them in.</summary>
    private static readonly string[] DefaultTypes = { "Change Request", "Requirement" };

    /// <summary>Types the sprint query can return, so the filter is usable before a load.</summary>
    private static readonly string[] KnownTypes = { "Bug", "Change Request", "Requirement" };

    /// <summary>Requirements whose child tasks are read, or whose sizes are written, side by side.</summary>
    private const int ParallelRequirements = 5;

    [Parameter] public TfsArea? Area { get; set; }
    [Parameter] public string? Sprint { get; set; }

    /// <summary>The page's shared area/sprint pickers, rendered inside this panel's config bar.</summary>
    [Parameter] public RenderFragment? Selectors { get; set; }

    private List<PlanningItem> planningItems = new();
    private List<PlanningItem> filteredItems = new();
    private bool loading;
    private string searchQ = "";
    private HashSet<string> typeFilter = new(DefaultTypes, StringComparer.OrdinalIgnoreCase);
    private string sortCol = "id";
    private bool sortAsc = true;
    private bool showMismatchOnly;
    private bool bulkApplying;
    private string statusMsg = "";
    private bool statusOk;

    // What the loaded rows actually describe, so a sprint/area switch can drop stale data
    // instead of showing another sprint's numbers under the new selection.
    private string loadedKey = "";

    // Leaving the tab stops the sprint's reads; a size write already sent is left to finish.
    private readonly PageLifetime page = new();

    private bool AllExpanded => filteredItems.Count > 0 && filteredItems.All(p => p.IsExpanded);
    private int CalcDiffersCount => planningItems.Count(p => p.CalcDiffersFromCurrent);

    private string EmptyGridMessage => typeFilter.Count == 0
        ? "No types selected. Tick at least one type to see items."
        : "No items match the filter.";

    private string SelectionKey => $"{Area?.Project}|{Area?.Path}|{Sprint}";

    protected override void OnParametersSet()
    {
        if (planningItems.Count == 0 || loadedKey == SelectionKey)
        {
            return;
        }
        planningItems = new();
        filteredItems = new();
        showMismatchOnly = false;
    }

    private static void ToggleExpand(PlanningItem pi) => pi.IsExpanded = !pi.IsExpanded;

    private void ToggleExpandAll()
    {
        var expand = !AllExpanded;
        foreach (var pi in filteredItems) pi.IsExpanded = expand;
    }

    private double TotalEffort => planningItems.Sum(p => p.WorkItem.Effort ?? 0);
    private double TotalCalculated => planningItems.Sum(p => p.CalculatedSize);
    private double TotalOriginal => planningItems.Sum(p => p.TaskTotalEstimate);
    private double TotalRemaining => planningItems.Sum(p => p.TaskTotalRemaining);
    private double TotalCompleted => planningItems.Sum(p => p.TaskTotalCompleted);
    private double TotalRevised => planningItems.Sum(p => p.TaskTotalRevised);

    private int MismatchedCount => planningItems.Count(p =>
        p.WorkItem.Effort is not > 0
        || (p.TaskTotalEstimate > 0 && Math.Abs(p.SizeMismatch) > 0.01));

    private void ToggleVerifySize()
    {
        showMismatchOnly = !showMismatchOnly;
        ApplyFilter();
    }

    private async Task Load()
    {
        var area = Area;
        var sprint = Sprint;
        if (area == null || string.IsNullOrEmpty(sprint))
        {
            return;
        }
        loading = true; planningItems.Clear(); filteredItems.Clear(); statusMsg = "";
        loadedKey = SelectionKey;
        StateHasChanged();
        try
        {
            var iterPath = TfsApiService.ToWiqlPath(sprint);
            var reqs = await TfsApi.LoadPlanningItemsAsync(area.Project, iterPath, page.Token);

            var areaPath = TfsApiService.ToWiqlPath(area.Path);
            reqs = reqs.Where(r => r.Area.Replace("\\Area\\", "\\").Contains(areaPath, StringComparison.OrdinalIgnoreCase)
                              || r.Area.Equals(areaPath, StringComparison.OrdinalIgnoreCase)).ToList();

            planningItems = reqs.Select(r => new PlanningItem { WorkItem = r, CurrentSprintPath = iterPath }).ToList();

            var unread = await LoadChildTasksAsync(page.Token);
            if (unread.Count > 0)
            {
                statusMsg = $"⚠ Tasks of {unread.Count} requirement(s) could not be read, so their hours show as 0: {unread[0]}";
                statusOk = false;
            }
        }
        catch (OperationCanceledException ex) when (page.Token.IsCancellationRequested)
        {
            Logger.LogDebug(ex, "Size Check load for {Area} / {Sprint} stopped because the tab was left", area.Path, sprint);
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Size Check load failed for {Area} / {Sprint}", area.Path, sprint);
            statusMsg = $"✕ Load failed: {TfsErrors.Describe(ex)}";
            statusOk = false;
        }
        loading = false;
    }

    /// <summary>
    /// Reads each requirement's child tasks a batch at a time, repainting as each batch lands. A requirement
    /// whose tasks could not be read is left with none, and its failure is handed back, already worded, so the
    /// page can say its hours are missing rather than zero.
    /// </summary>
    private async Task<List<string>> LoadChildTasksAsync(CancellationToken ct)
    {
        var failures = new System.Collections.Concurrent.ConcurrentQueue<string>();
        foreach (var chunk in planningItems.Chunk(ParallelRequirements))
        {
            await Task.WhenAll(chunk.Select(async pi =>
            {
                try
                {
                    pi.Tasks = await TfsApi.GetChildTasksAsync(pi.WorkItem.Project, pi.WorkItem.Id, ct);
                }
                catch (Exception ex) when (!TfsErrors.IsRequestedCancellation(ex, ct))
                {
                    Logger.LogError(ex, "Size Check could not read the child tasks of #{Id}", pi.WorkItem.Id);
                    pi.Tasks = new();
                    failures.Enqueue(TfsErrors.Describe(ex));
                }
            }));
            ApplyFilter();
            StateHasChanged();
        }
        return failures.ToList();
    }

    private async Task OnSizeKeyDown(KeyboardEventArgs e, PlanningItem pi)
    {
        if (e.Key == "Enter") await SaveSize(pi);
        else if (e.Key == "Escape") pi.EditingSize = null;
    }

    private async Task SaveSize(PlanningItem pi)
    {
        if (pi.EditingSize == null)
        {
            return;
        }
        if (pi.EditedSize is not { } newSize)
        {
            SetStatus("✕ Invalid size value", false, 2500);
            return;
        }

        pi.Saving = true; StateHasChanged();
        if (await PushSize(pi, newSize) is { } failure)
        {
            SetStatus($"✕ Update failed: {failure}", false, 3500);
        }
        else
        {
            pi.EditingSize = null;
            SetStatus($"✓ Updated #{pi.WorkItem.Id} size to {newSize:0.##}", true, 2000);
        }
        pi.Saving = false;
    }

    /// <summary>Applies the calculated size (past completed + current estimate) to a single requirement.</summary>
    private async Task ApplyCalculatedSize(PlanningItem pi)
    {
        var newSize = pi.CalculatedSize;
        if (newSize <= 0)
        {
            SetStatus("✕ No task hours available to calculate a size", false, 2500);
            return;
        }

        pi.Saving = true; StateHasChanged();
        if (await PushSize(pi, newSize) is { } failure)
        {
            SetStatus($"✕ Update failed: {failure}", false, 3500);
        }
        else
        {
            pi.EditingSize = null;
            SetStatus($"✓ Set #{pi.WorkItem.Id} size to {newSize:0.##} ({pi.PastSprintCompleted:0.##}h done + {pi.CurrentSprintOriginal:0.##}h est)", true, 2800);
        }
        pi.Saving = false;
    }

    /// <summary>Applies the calculated size to every loaded requirement whose size differs from the calculation.</summary>
    private async Task ApplyAllCalculated()
    {
        var targets = planningItems.Where(p => p.CalcDiffersFromCurrent).ToList();
        if (targets.Count == 0)
        {
            SetStatus("✓ All sizes already match the calculated values", true, 2500);
            return;
        }

        bulkApplying = true; StateHasChanged();
        var failures = new System.Collections.Concurrent.ConcurrentQueue<string>();
        foreach (var chunk in targets.Chunk(ParallelRequirements))
        {
            var jobs = chunk.Select(async pi =>
            {
                pi.Saving = true;
                if (await PushSize(pi, pi.CalculatedSize) is { } failure)
                {
                    failures.Enqueue(failure);
                }
                pi.Saving = false;
            }).ToArray();
            await Task.WhenAll(jobs);
            StateHasChanged();
        }
        bulkApplying = false;

        var ok = targets.Count - failures.Count;
        SetStatus(failures.TryPeek(out var first) ? $"⚠ Updated {ok}, {failures.Count} failed: {first}" : $"✓ Updated {ok} requirement size(s)",
            failures.IsEmpty, 3500);
    }

    // Requirement/Change Request "Size" field across the common process templates.
    private static readonly string[] SizeFieldCandidates =
    {
        "Microsoft.VSTS.Scheduling.Size",     // CMMI (Requirement / Change Request)
        "Microsoft.VSTS.Scheduling.Effort",   // Scrum (Backlog Item / Bug)
        "Microsoft.VSTS.Scheduling.StoryPoints" // Agile (User Story)
    };

    /// <summary>
    /// Writes the size value to the requirement's Size field (falling back across templates) and updates the local
    /// model. Answers null once a field took it, or the last field's failure, worded for the page. The write is started
    /// by a click, so it is not cancelled when the page closes.
    /// </summary>
    private async Task<string?> PushSize(PlanningItem pi, double newSize)
    {
        var failure = "No size field could be updated.";
        foreach (var field in SizeFieldCandidates)
        {
            try
            {
                await TfsApi.UpdateWorkItemFieldAsync(pi.WorkItem.Project, pi.WorkItem.Id, field, newSize, CancellationToken.None);
                pi.WorkItem.Effort = newSize;
                return null;
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "Setting {Field} of #{Id} to {Size} failed; any other size field is tried next",
                    field, pi.WorkItem.Id, newSize);
                failure = TfsErrors.Describe(ex);
            }
        }
        return failure;
    }

    private void SetStatus(string msg, bool ok, int clearMs)
    {
        statusMsg = msg;
        statusOk = ok;
        _ = ClearStatusAfter(clearMs);
    }

    private async Task ClearStatusAfter(int ms)
    {
        await Task.Delay(ms);
        statusMsg = "";
        await InvokeAsync(StateHasChanged);
    }

    public void Dispose() => page.Dispose();

    // ─── Filtering ───
    private void OnSearch(ChangeEventArgs e) { searchQ = (e.Value?.ToString() ?? "").ToLower(); ApplyFilter(); }

    private void OnTypeFilterChanged(HashSet<string> selected)
    {
        typeFilter = selected;
        ApplyFilter();
    }

    private void ApplyFilter()
    {
        filteredItems = planningItems.Where(pi =>
        {
            if (!string.IsNullOrEmpty(searchQ)
                && !pi.WorkItem.Id.Contains(searchQ)
                && !pi.WorkItem.Title.ToLower().Contains(searchQ))
                return false;
            if (!typeFilter.Contains(pi.WorkItem.Type)) return false;
            if (showMismatchOnly)
            {
                var hasSize = pi.WorkItem.Effort.HasValue && pi.WorkItem.Effort > 0;
                var hasMismatch = hasSize && pi.TaskTotalEstimate > 0 && Math.Abs(pi.SizeMismatch) > 0.01;
                // Show: no size OR size mismatch
                if (hasSize && !hasMismatch) return false;
            }
            return true;
        }).ToList();
        ApplySort();
    }

    private void Sort(string col)
    {
        if (sortCol == col) sortAsc = !sortAsc; else { sortCol = col; sortAsc = true; }
        ApplySort();
    }

    private void ApplySort()
    {
        filteredItems = sortCol switch
        {
            "id" => sortAsc ? filteredItems.OrderBy(p => int.TryParse(p.WorkItem.Id, out var n) ? n : 0).ToList() : filteredItems.OrderByDescending(p => int.TryParse(p.WorkItem.Id, out var n) ? n : 0).ToList(),
            "title" => sortAsc ? filteredItems.OrderBy(p => p.WorkItem.Title).ToList() : filteredItems.OrderByDescending(p => p.WorkItem.Title).ToList(),
            "state" => sortAsc ? filteredItems.OrderBy(p => p.WorkItem.State).ToList() : filteredItems.OrderByDescending(p => p.WorkItem.State).ToList(),
            "assigned" => sortAsc ? filteredItems.OrderBy(p => p.WorkItem.Assigned).ToList() : filteredItems.OrderByDescending(p => p.WorkItem.Assigned).ToList(),
            "size" => sortAsc ? filteredItems.OrderBy(p => p.WorkItem.Effort ?? 0).ToList() : filteredItems.OrderByDescending(p => p.WorkItem.Effort ?? 0).ToList(),
            "calc" => sortAsc ? filteredItems.OrderBy(p => p.CalculatedSize).ToList() : filteredItems.OrderByDescending(p => p.CalculatedSize).ToList(),
            "orig" => sortAsc ? filteredItems.OrderBy(p => p.TaskTotalEstimate).ToList() : filteredItems.OrderByDescending(p => p.TaskTotalEstimate).ToList(),
            "rem" => sortAsc ? filteredItems.OrderBy(p => p.TaskTotalRemaining).ToList() : filteredItems.OrderByDescending(p => p.TaskTotalRemaining).ToList(),
            "done" => sortAsc ? filteredItems.OrderBy(p => p.TaskTotalCompleted).ToList() : filteredItems.OrderByDescending(p => p.TaskTotalCompleted).ToList(),
            "rev" => sortAsc ? filteredItems.OrderBy(p => p.TaskTotalRevised).ToList() : filteredItems.OrderByDescending(p => p.TaskTotalRevised).ToList(),
            _ => filteredItems
        };
    }

    /// <summary>Known types plus anything the sprint actually returned, so the list cannot drift from the query.</summary>
    private List<MultiSelectDropdown.Option> TypeOptions() => KnownTypes
        .Concat(planningItems.Select(p => p.WorkItem.Type))
        .Where(t => !string.IsNullOrWhiteSpace(t))
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .OrderBy(t => t, StringComparer.OrdinalIgnoreCase)
        .Select(t => new MultiSelectDropdown.Option { Value = t, Label = t })
        .ToList();

    /// <summary>A requirement row's background: red without a size, amber when its tasks disagree with it, else green.</summary>
    private static string RowBackground(bool hasSize, bool mismatch) => (hasSize, mismatch) switch
    {
        (false, _) => "rgba(248,113,113,.06)",
        (_, true) => "rgba(251,191,36,.06)",
        _ => "rgba(16,185,129,.05)"
    };

    /// <summary>The colour of the size difference: amber when the tasks disagree with the size, green when they agree.</summary>
    private static string SizeDifferenceColour(bool hasSize, bool mismatch) => (hasSize, mismatch) switch
    {
        (_, true) => "var(--amber)",
        (true, _) => "var(--green)",
        _ => "var(--muted)"
    };
}
