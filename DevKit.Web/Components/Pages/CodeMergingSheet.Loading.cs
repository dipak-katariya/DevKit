using Microsoft.AspNetCore.Components;
using DevKit.Web.Models;
using DevKit.Web.Services;

namespace DevKit.Web.Components.Pages;

/// <summary>
/// Choosing what to look at — area, sprints, repositories and their branches — then loading it,
/// verifying the merge state, and the unlinked pull-request search.
/// </summary>
public partial class CodeMergingSheet
{
    // ═══ SELECTION ═══

    private async Task OnAreaChange(ChangeEventArgs e)
    {
        var val = e.Value?.ToString();
        if (string.IsNullOrEmpty(val)) { selectedArea = null; return; }
        var area = AreaSprintSelect.Parse(val);
        if (area == null) return;

        selectedArea = area;

        // Sprints and repositories are both project-scoped, so anything belonging to the
        // previous project would otherwise stay selected and silently return nothing.
        DropSprintsOutsideProject();

        var valid = AvailableRepos.Select(r => r.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        selectedRepoIds.RemoveWhere(id => !valid.Contains(id));
        if (selectedRepoIds.Count == 0) RestoreRepoIds();

        await ApplyRepoSelection();
    }

    private void DropSprintsOutsideProject()
    {
        var valid = SprintOptions.Select(o => o.Value).ToHashSet(StringComparer.OrdinalIgnoreCase);
        selectedSprints.RemoveWhere(path => !valid.Contains(path));
    }

    private void OnSprintsChanged(HashSet<string> paths) => Refilter();

    private async Task OnReposChanged(HashSet<string> ids)
    {
        if (ids.Count > MaxSelectableRepos)
        {
            // Trim rather than reject, so the click still does something predictable.
            foreach (var extra in ids.Skip(MaxSelectableRepos).ToList()) ids.Remove(extra);
            bundle.TruncationNotice =
                $"At most {MaxSelectableRepos} repositories can be loaded at once — the selection was trimmed.";
        }

        await ApplyRepoSelection();
    }

    /// <summary>
    /// Applies the current repository selection: remembers it, rebuilds the branch rows and
    /// reloads branch lists, so every picker only ever offers branches that repository has.
    /// Verification is dropped because it described a selection that no longer applies.
    /// </summary>
    private async Task ApplyRepoSelection()
    {
        Settings.SetCodeMergingRepoIds(selectedRepoIds);
        RebuildBranchRows();

        ResetVerification();
        filter.MergeState = "";
        Refilter();

        await LoadBranchesForSelectedRepos();
    }

    /// <summary>Loads each selected repository's branches in parallel, one list per row.</summary>
    private async Task LoadBranchesForSelectedRepos()
    {
        var pending = branchRows.Where(r => r.Branches.Count == 0).ToList();
        if (pending.Count == 0) return;

        branchesLoading = true;
        StateHasChanged();
        try
        {
            await Task.WhenAll(pending.Select(async row =>
                row.Branches = await Merging.GetBranchesAsync(new[] { row.Repo })));
        }
        catch (Exception ex)
        {
            bundle.ErrorMessage = $"Failed to load branches: {ex.Message}";
        }
        finally
        {
            branchesLoading = false;
        }

        DropBranchesTheRepoDoesNotHave();
    }

    /// <summary>
    /// A branch saved in Settings can be renamed or deleted in TFS. Leaving it selected would
    /// silently verify against nothing, so it is cleared once the real branch list is known.
    /// </summary>
    private void DropBranchesTheRepoDoesNotHave()
    {
        foreach (var row in branchRows)
        {
            if (row.Branches.Count == 0) continue;

            if (!string.IsNullOrEmpty(row.QaBranch) && !HasBranch(row, row.QaBranch)) row.QaBranch = "";
            if (!string.IsNullOrEmpty(row.MergingBranch) && !HasBranch(row, row.MergingBranch)) row.MergingBranch = "";
        }
    }

    private static bool HasBranch(RepoBranchSetup.RepoBranchRow row, string branch) =>
        row.Branches.Any(b => string.Equals(b, branch, StringComparison.OrdinalIgnoreCase));

    private List<MultiSelectDropdown.Option> RepoOptions =>
        RepoSelect.Options(AvailableRepos, Settings.MostUsedRepoIds, BranchHint);

    /// <summary>
    /// Surfaces a repository's configured branches in the picker. Without it, a wrong or
    /// missing branch looks like missing data rather than a misconfiguration.
    /// </summary>
    private string BranchHint(TfsRepo repo)
    {
        var saved = Settings.GetRepoBranches(repo.Id);
        if (saved.IsEmpty) return $"{repo.Project} — no branches configured";

        var qa = string.IsNullOrWhiteSpace(saved.QaBranch) ? "all branches" : saved.QaBranch;
        var target = string.IsNullOrWhiteSpace(saved.MergingBranch) ? "no target" : saved.MergingBranch;
        return $"{repo.Project} — PRs → {qa} · verify → {target}";
    }

    /// <summary>A branch edit invalidates whatever was verified against the previous one.</summary>
    private void OnBranchSetupChanged()
    {
        ResetVerification();
        filter.MergeState = "";
        Refilter();
    }

    private void SaveBranchDefaults()
    {
        foreach (var row in branchRows)
        {
            Settings.SetRepoQaBranch(row.Repo.Id, row.QaBranch);
            Settings.SetRepoMergingBranch(row.Repo.Id, row.MergingBranch);
            row.SavedQaBranch = row.QaBranch;
            row.SavedMergingBranch = row.MergingBranch;
        }
    }

    // ═══ LOAD + VERIFY ═══

    private async Task Load()
    {
        if (!CanLoad) return;
        loading = true;

        // A comparison still running belongs to the sheet about to be replaced.
        CancellationSources.CancelAndDispose(ref codeRun);
        bundle = new CodeMergingBundle();
        expandedPrKey = null;
        Refilter();
        StateHasChanged();

        var query = new CodeMergingQuery(
            selectedArea!.Project,
            TfsApiService.ToWiqlPath(selectedArea.Path),
            selectedSprints.Select(TfsApiService.ToWiqlPath).ToList(),
            SelectedRepos,
            deliverableOnly,
            QaBranchByRepoId,
            matchByBranchOrTitle);

        bundle = await Merging.GetMergingDataAsync(query);
        if (bundle.Requirements.Count == 0 && string.IsNullOrEmpty(bundle.ErrorMessage))
            bundle.ErrorMessage = "No work items matched this area, sprint and scope.";

        Refilter();
        loading = false;
        StateHasChanged();

        await LoadBranchesForSelectedRepos();
    }

    /// <summary>
    /// Checks every row's commits against its merging branch. Rows past the first few have not read
    /// their commits yet, so those are read first: without them the check can only ask whether a pull
    /// request's merge commit is on the branch — which a cherry-picked change never is — and the work
    /// would read as not merged until the row was opened.
    /// </summary>
    private async Task Verify()
    {
        if (!CanVerify)
        {
            return;
        }
        verifying = true;
        StateHasChanged();

        try
        {
            await LoadRemainingCommits();
            await Verifier.VerifyAsync(bundle, MergingBranchByRepoId, ScanFrom);
        }
        catch (Exception ex)
        {
            Log.LogError(ex, "Verify All failed for {Rows} work items", bundle.Requirements.Count);
            bundle.ErrorMessage = $"Verification failed: {TfsErrors.Describe(ex)}";
        }
        verifying = false;
        Refilter();
    }

    /// <summary>
    /// Re-checks one requirement against the target branch. Called both from the row's
    /// refresh button and automatically when a row is opened, so someone who has just pushed
    /// a cherry-pick sees the current state without reloading the whole sheet. The row's
    /// commits are read first, for the same reason as in <see cref="Verify"/>.
    /// </summary>
    private async Task ReverifyRow(RequirementMergingRow row)
    {
        if (bundle.TargetBranchName is null || row.IsVerifying)
        {
            return;
        }

        // Set before the commits are read, so a second click on refresh cannot start a second read.
        row.IsVerifying = true;
        StateHasChanged();
        try
        {
            await LoadRowCommits(row);
            await Verifier.VerifyRequirementAsync(bundle, row.WorkItemId, MergingBranchByRepoId, ScanFrom);
        }
        catch (Exception ex)
        {
            Log.LogError(ex, "Re-check failed for work item {WorkItemId}", row.WorkItemId);
            bundle.ErrorMessage = $"Re-check failed for #{row.WorkItemId}: {TfsErrors.Describe(ex)}";
        }
        row.IsVerifying = false;
        Refilter();
    }

    // ═══ UNLINKED PULL REQUESTS ═══

    /// <summary>
    /// Opening the tab searches once, so the first click shows something. Later visits keep
    /// the previous result until Search is pressed again.
    /// </summary>
    private async Task ShowUnlinked()
    {
        showUnlinked = true;
        if (unlinked.HasRun || unlinkedSearching || selectedRepoIds.Count == 0) return;
        await SearchUnlinked();
    }

    private async Task SearchUnlinked()
    {
        if (unlinkedSearching || selectedRepoIds.Count == 0) return;

        unlinkedSearching = true;
        StateHasChanged();
        try
        {
            unlinked = await Merging.GetUnlinkedPullRequestsAsync(
                SelectedRepos, unlinkedDays, QaBranchByRepoId, matchByBranchOrTitle);
        }
        catch (Exception ex)
        {
            unlinked = new UnlinkedPrBundle { HasRun = true, ErrorMessage = $"Search failed: {ex.Message}" };
        }
        unlinkedSearching = false;
    }

    private void OnUnlinkedDaysChanged(ChangeEventArgs e)
    {
        if (!int.TryParse(e.Value?.ToString(), out var days)) return;
        unlinkedDays = Math.Clamp(days, 1, 180);
    }

}
