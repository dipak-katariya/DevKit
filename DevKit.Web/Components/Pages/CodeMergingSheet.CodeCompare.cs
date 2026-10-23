using DevKit.Web.Models;
using DevKit.Web.Services;

namespace DevKit.Web.Components.Pages;

/// <summary>
/// Comparing the code itself with each repository's merging branch, in three scopes: every pull request
/// on the sheet, one work item's, or one pull request's. The last allows far larger pull requests, so
/// one too big for the sprint-wide run can still be checked. One comparison runs at a time.
/// </summary>
public partial class CodeMergingSheet
{
    private CancellationTokenSource? codeRun;
    private CodeCompareProgress codeProgress;

    /// <summary>The pull request (by row key) and file whose lines are being read on demand.</summary>
    private (string PrKey, string Path)? explaining;

    private bool ComparingCode => codeRun is not null;

    private bool CanCompareCode =>
        bundle.Requirements.Count > 0 && MergingBranchByRepoId.Count > 0 && Activity.IsIdle;

    /// <summary>The Merged column shows once something can be checked, not only after a check ran.</summary>
    private bool ShowMergeColumns =>
        bundle.TargetBranchName is not null || (bundle.Requirements.Count > 0 && MergingBranchByRepoId.Count > 0);

    private const string CompareAllTooltip =
        "Compare the code of every completed pull request shown with its repository's merging branch, " +
        "file by file. Reads any commits not loaded yet and runs Verify All first.";

    private MergingRow.CodeState RowCodeState =>
        new(ComparingCode, MergingBranchByRepoId.Keys.ToHashSet(StringComparer.OrdinalIgnoreCase), explaining);

    private async Task CompareAllCode()
    {
        if (!CanCompareCode)
        {
            return;
        }

        // The commit check is quick, and with it in place the sheet can point out pull requests whose
        // commits are all on the branch while their code is not. It needs every row's commits, which
        // Verify reads first, so it runs again whenever some were still unread.
        var sheet = bundle;
        if (sheet.TargetBranchName is null || PendingRowCount > 0)
        {
            await Verify();
        }

        // A sheet loaded meanwhile is not the one this click was for, and its commits were not read.
        if (!ReferenceEquals(sheet, bundle))
        {
            return;
        }

        var prs = visibleRows
            .SelectMany(r => filter.VisiblePrs(r).Where(p => r.PullRequests.Contains(p)))
            .Distinct()
            .ToList();
        await RunCodeCompare(sheet, prs, CodeCompareScope.Sprint);
    }

    private async Task CompareRowCode(RequirementMergingRow row)
    {
        var sheet = bundle;
        await LoadRowCommits(row);
        if (!ReferenceEquals(sheet, bundle))
        {
            return;
        }

        await RunCodeCompare(sheet, filter.VisiblePrs(row).Where(p => row.PullRequests.Contains(p)).ToList(), CodeCompareScope.WorkItem);

        // Same reason as the sheet-wide run: without the commit check the row cannot say its commits
        // are there while its code is not.
        if (row.PullRequests.All(p => p.IsMergedToTargetBranch is null))
        {
            await ReverifyRow(row);
        }
    }

    private Task ComparePrCode(MergingPullRequest pr) =>
        RunCodeCompare(bundle, new[] { pr }, CodeCompareScope.PullRequest);

    /// <summary>
    /// Compares the given pull requests of <paramref name="target"/>, the sheet the click was on. Each pull
    /// request's result is kept out after a reset or a reload by the in-progress marker the reset clears;
    /// the sheet's generation does the same for the run summary and error, which describe the settings the
    /// run started with.
    /// </summary>
    private async Task RunCodeCompare(CodeMergingBundle target, IReadOnlyList<MergingPullRequest> prs, CodeCompareScope scope)
    {
        if (ComparingCode || prs.Count == 0)
        {
            return;
        }

        var generation = target.VerificationGeneration;
        var branches = MergingBranchByRepoId;
        foreach (var repoId in prs.Select(p => p.RepositoryId).Where(branches.ContainsKey))
        {
            target.VerifiedBranchByRepoId[repoId] = branches[repoId];
        }

        var cts = new CancellationTokenSource();
        codeRun = cts;
        codeProgress = new CodeCompareProgress(0, prs.Count, 0, 0, 0, TimeSpan.Zero, "");
        var progress = new Progress<CodeCompareProgress>(p =>
        {
            codeProgress = p;
            _ = InvokeAsync(StateHasChanged);
        });
        StateHasChanged();

        CodeCompareRunSummary? summary = null;
        try
        {
            summary = await CodeCompare.CompareAsync(new CodeCompareRequest(prs, branches, scope), progress, cts.Token);
        }
        catch (Exception ex)
        {
            Log.LogError(ex, "Code compare ({Scope}) failed for {PullRequests} pull requests", scope, prs.Count);
            target.TryWriteResults(generation, () => target.ErrorMessage = $"Code compare failed: {TfsErrors.Describe(ex)}");
        }

        // Still the current run, unless a reload or a branch change cancelled it and moved on. Its
        // summary would then describe settings that no longer apply. Stop leaves it current.
        var current = ReferenceEquals(codeRun, cts);
        if (current)
        {
            codeRun = null;
            target.TryWriteResults(generation, () => target.LastCodeCompare = summary ?? target.LastCodeCompare);
        }
        cts.Dispose();

        if (ReferenceEquals(target, bundle))
        {
            Refilter();
        }
    }

    private void StopCodeCompare() => codeRun?.Cancel();

    private async Task ExplainFile((MergingPullRequest Pr, FileCodeCheck File) request)
    {
        var (pr, file) = request;
        if (explaining is not null)
        {
            return;
        }

        explaining = (MergingRow.PrKey(pr), file.Path);
        var explained = pr.CodeCheck;
        StateHasChanged();
        try
        {
            var detailed = await CodeCompare.ExplainAsync(pr, file, page.Token);

            // Attached only to the result it was read for: a comparison that finished meanwhile has
            // its own files, and an old one must not be slipped into it.
            if (explained is not null)
            {
                pr.TryReplaceCodeCheck(explained, explained.WithFile(detailed));
            }
        }
        catch (OperationCanceledException ex) when (page.Token.IsCancellationRequested)
        {
            Log.LogDebug(ex, "Reading the lines of {Path} stopped because the page was left", file.Path);
        }
        catch (Exception ex)
        {
            Log.LogError(ex, "Reading the lines of {Path} in pull request {PrId} failed", file.Path, pr.PullRequestId);
            bundle.ErrorMessage = $"Couldn't load the lines of {file.Path}: {TfsErrors.Describe(ex)}";
        }
        finally
        {
            explaining = null;
        }
    }

    /// <summary>Branch settings or the repository selection changed, so every result is out of date.</summary>
    private void ResetVerification()
    {
        CancellationSources.CancelAndDispose(ref codeRun);
        MergeVerificationService.ClearVerification(bundle);
    }
}
