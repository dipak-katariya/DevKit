using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using DevKit.Web.Models;
using DevKit.Web.Services;

namespace DevKit.Web.Components;

/// <summary>Requirement sizes checked against the estimates on their child tasks.</summary>
public partial class SizeCheckPanel
{
    /// <summary>Types the grid starts on. Bugs are excluded until the user ticks them in.</summary>
    private static readonly string[] DefaultTypes = { "Change Request", "Requirement" };

    /// <summary>Types the sprint query can return, so the filter is usable before a load.</summary>
    private static readonly string[] KnownTypes = { "Bug", "Change Request", "Requirement" };

    [Parameter] public TfsArea? Area { get; set; }
    [Parameter] public string? Sprint { get; set; }

    /// <summary>The hub's shared area/sprint pickers, rendered inside this panel's config bar.</summary>
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

    private bool AllExpanded => filteredItems.Count > 0 && filteredItems.All(p => p.IsExpanded);
    private int CalcDiffersCount => planningItems.Count(p => p.CalcDiffersFromCurrent);

    private string EmptyGridMessage => typeFilter.Count == 0
        ? "No types selected — tick at least one type to see items."
        : "No items match the filter.";

    private string SelectionKey => $"{Area?.Project}|{Area?.Path}|{Sprint}";

    protected override void OnParametersSet()
    {
        if (planningItems.Count == 0 || loadedKey == SelectionKey) return;
        planningItems = new();
        filteredItems = new();
        showMismatchOnly = false;
    }

    private void ToggleExpand(PlanningItem pi) => pi.IsExpanded = !pi.IsExpanded;

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
        !p.WorkItem.Effort.HasValue || p.WorkItem.Effort == 0
        || (p.TaskTotalEstimate > 0 && Math.Abs(p.SizeMismatch) > 0.01));

    private void ToggleVerifySize()
    {
        showMismatchOnly = !showMismatchOnly;
        ApplyFilter();
    }

    private async Task Load()
    {
        if (Area == null || string.IsNullOrEmpty(Sprint)) return;
        loading = true; planningItems.Clear(); filteredItems.Clear(); statusMsg = "";
        loadedKey = SelectionKey;
        StateHasChanged();
        try
        {
            var iterPath = TfsApiService.ToWiqlPath(Sprint);
            var reqs = await TfsApi.LoadPlanningItemsAsync(Area.Project, iterPath);

            var areaPath = TfsApiService.ToWiqlPath(Area.Path);
            reqs = reqs.Where(r => r.Area.Replace("\\Area\\", "\\").Contains(areaPath, StringComparison.OrdinalIgnoreCase)
                              || r.Area.Equals(areaPath, StringComparison.OrdinalIgnoreCase)).ToList();

            planningItems = reqs.Select(r => new PlanningItem { WorkItem = r, CurrentSprintPath = iterPath }).ToList();

            foreach (var chunk in planningItems.Chunk(5))
            {
                var tasks = chunk.Select(async pi =>
                {
                    try { pi.Tasks = await TfsApi.GetChildTasksAsync(pi.WorkItem.Project, pi.WorkItem.Id); }
                    catch { pi.Tasks = new(); }
                }).ToArray();
                await Task.WhenAll(tasks);
                ApplyFilter();
                StateHasChanged();
            }
        }
        catch (Exception ex) { statusMsg = $"✕ Load failed: {ex.Message}"; statusOk = false; }
        loading = false;
    }

    private void StartEditSize(PlanningItem pi)
    {
        pi.EditingSize = pi.WorkItem.Effort?.ToString("0.##") ?? "";
    }

    private async Task OnSizeKeyDown(KeyboardEventArgs e, PlanningItem pi)
    {
        if (e.Key == "Enter") await SaveSize(pi);
        else if (e.Key == "Escape") pi.EditingSize = null;
    }

    private async Task SaveSize(PlanningItem pi)
    {
        if (pi.EditingSize == null) return;
        if (!double.TryParse(pi.EditingSize, out var newSize) || newSize < 0)
        {
            SetStatus("✕ Invalid size value", false, 2500);
            return;
        }

        pi.Saving = true; StateHasChanged();
        try
        {
            await PushSize(pi, newSize);
            pi.EditingSize = null;
            SetStatus($"✓ Updated #{pi.WorkItem.Id} size to {newSize:0.##}", true, 2000);
        }
        catch (Exception ex)
        {
            SetStatus($"✕ Update failed: {ex.Message}", false, 3500);
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
        try
        {
            await PushSize(pi, newSize);
            pi.EditingSize = null;
            SetStatus($"✓ Set #{pi.WorkItem.Id} size to {newSize:0.##} ({pi.PastSprintCompleted:0.##}h done + {pi.CurrentSprintOriginal:0.##}h est)", true, 2800);
        }
        catch (Exception ex)
        {
            SetStatus($"✕ Update failed: {ex.Message}", false, 3500);
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
        var results = new System.Collections.Concurrent.ConcurrentBag<bool>();
        foreach (var chunk in targets.Chunk(5))
        {
            var jobs = chunk.Select(async pi =>
            {
                pi.Saving = true;
                try { await PushSize(pi, pi.CalculatedSize); results.Add(true); }
                catch { results.Add(false); }
                pi.Saving = false;
            }).ToArray();
            await Task.WhenAll(jobs);
            StateHasChanged();
        }
        bulkApplying = false;

        var ok = results.Count(r => r);
        var fail = results.Count - ok;
        SetStatus(fail == 0 ? $"✓ Updated {ok} requirement size(s)" : $"⚠ Updated {ok}, {fail} failed", fail == 0, 3500);
    }

    // Requirement/Change Request "Size" field across the common process templates.
    private static readonly string[] SizeFieldCandidates =
    {
        "Microsoft.VSTS.Scheduling.Size",     // CMMI (Requirement / Change Request)
        "Microsoft.VSTS.Scheduling.Effort",   // Scrum (Backlog Item / Bug)
        "Microsoft.VSTS.Scheduling.StoryPoints" // Agile (User Story)
    };

    /// <summary>Writes the size value to the requirement's Size field (falling back across templates) and updates the local model.</summary>
    private async Task PushSize(PlanningItem pi, double newSize)
    {
        Exception? lastError = null;
        foreach (var field in SizeFieldCandidates)
        {
            try
            {
                await TfsApi.UpdateWorkItemFieldAsync(pi.WorkItem.Project, pi.WorkItem.Id, field, newSize);
                pi.WorkItem.Effort = newSize;
                return;
            }
            catch (Exception ex) { lastError = ex; }
        }
        throw lastError ?? new InvalidOperationException("No size field could be updated.");
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
    private List<MultiSelectDropdown.Option> TypeOptions => KnownTypes
        .Concat(planningItems.Select(p => p.WorkItem.Type))
        .Where(t => !string.IsNullOrWhiteSpace(t))
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .OrderBy(t => t, StringComparer.OrdinalIgnoreCase)
        .Select(t => new MultiSelectDropdown.Option { Value = t, Label = t })
        .ToList();

    private static string TypeCls(string t) => t.ToLower() switch { "bug" => "bug", "change request" => "cr", _ => "" };

    private static string StateCls(string s)
    {
        var l = s.ToLower();
        if (l.Contains("progress") || l.Contains("active")) return "state-active";
        if (l.Contains("resolve") || l.Contains("done") || l.Contains("complete")) return "state-resolved";
        if (l.Contains("closed")) return "state-closed";
        return "state-new";
    }
}
