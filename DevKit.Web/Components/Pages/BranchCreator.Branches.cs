using DevKit.Web.Models;
using DevKit.Web.Services;
using static DevKit.Web.Services.CancellationSources;

namespace DevKit.Web.Components.Pages;

/// <summary>
/// The branch workflow: the branches a work item already links to across every repository,
/// name validation, the existence check, and creating a branch and linking it back.
/// </summary>
public partial class BranchCreator
{
    // ─── Item Selection ───
    private void SelectItem(WorkItem item)
    {
        selectedItem = item;
        resultMsg = "";
        existingBranches = new();
        unlinkCandidate = null;
        RegenerateBranchName(item);
        _ = LoadExistingBranches(item);
    }

    private void RegenerateBranchName(WorkItem item)
    {
        var team = !string.IsNullOrWhiteSpace(teamName) ? teamName : (selectedArea?.Name ?? "team");
        branchName = BranchNaming.Build(team, item.Sprint, item.Id, item.Title);
        prName = $"{BranchNaming.TeamSegment(team)} : {item.Id} : {item.Title}";
    }

    /// <summary>
    /// Reads the branches the work item is already linked to. One request against the work item,
    /// covering every repository — rather than listing refs in each repository and matching on the
    /// id, which was ~50 paged listings per click and missed branches not named after the id.
    /// </summary>
    private async Task LoadExistingBranches(WorkItem item)
    {
        CancelAndDispose(ref linkLoadCts);
        linkLoadCts = new CancellationTokenSource();
        var ct = linkLoadCts.Token;

        loadingExisting = true; existingErr = ""; StateHasChanged();
        try
        {
            var links = await TfsApi.GetWorkItemBranchLinksAsync(item.Project, item.Id, ct);
            if (ct.IsCancellationRequested) return;

            var byId = repos.ToDictionary(r => r.Id, r => r.Name, StringComparer.OrdinalIgnoreCase);
            foreach (var link in links)
                link.RepositoryName = byId.TryGetValue(link.RepositoryId, out var name) ? name : "";

            existingBranches = links
                .OrderByDescending(l => l.BelongsTo(selectedRepo?.Id))
                .ThenBy(l => l.RepositoryName, StringComparer.OrdinalIgnoreCase)
                .ThenBy(l => l.BranchName, StringComparer.OrdinalIgnoreCase)
                .ToList();
            MarkMissingBranches();
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Could not read branch links for work item {Id}", item.Id);
            existingErr = ex.Message;
        }
        finally
        {
            if (!ct.IsCancellationRequested)
            {
                loadingExisting = false;
                StateHasChanged();
            }
        }
    }

    /// <summary>
    /// Removes a stale link from the work item. Destructive and not undoable from here, so it is
    /// only offered for links whose branch was actually confirmed missing, and only after the row
    /// has been expanded into an explicit confirm.
    /// </summary>
    private async Task RemoveStaleLink(WorkItemBranchLink link)
    {
        var item = selectedItem;
        if (item == null || unlinking) return;

        unlinking = true;
        StateHasChanged();
        try
        {
            var result = await TfsApi.UnlinkWorkItemBranchAsync(
                item.Project, item.Id, link.RepositoryId, link.BranchName);

            resultOk = result.IsUnlinked;
            resultMsg = result.Outcome switch
            {
                WorkItemLinkOutcome.Unlinked =>
                    $"<span style='color:var(--green);'>✓ Stale link removed from #{Enc(item.Id)}: <code>{Enc(link.BranchName)}</code></span>",
                WorkItemLinkOutcome.NotLinked =>
                    $"<span style='color:var(--amber);'>⚠ That link was already gone from #{Enc(item.Id)}.</span>",
                _ =>
                    $"<span style='color:var(--red);'>✕ Could not remove the link: {Enc(result.Detail)}</span>"
            };

            if (result.IsUnlinked) await LoadExistingBranches(item);
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Removing stale link {Branch} from work item {Id} failed", link.BranchName, item.Id);
            resultMsg = $"<span style='color:var(--red);'>✕ Could not remove the link: {Enc(ex.Message)}</span>";
            resultOk = false;
        }
        finally
        {
            unlinking = false;
            unlinkCandidate = null;
            StateHasChanged();
        }
    }

    /// <summary>Reuses a linked branch, switching to its repository when it lives in another one.</summary>
    private async Task UseExistingBranch(WorkItemBranchLink link)
    {
        branchName = link.BranchName;
        resultMsg = "";

        if (link.BelongsTo(selectedRepo?.Id)) return;

        var repo = repos.FirstOrDefault(r => string.Equals(r.Id, link.RepositoryId, StringComparison.OrdinalIgnoreCase));
        if (repo != null) await SelectRepo(repo);
    }

    private string ExistingBranchLabel(WorkItemBranchLink link) =>
        link.BelongsTo(selectedRepo?.Id) || string.IsNullOrEmpty(link.RepositoryName)
            ? link.BranchName
            : $"{link.RepositoryName}: {link.BranchName}";

    /// <summary>
    /// Flags links whose branch no longer exists in its repository. Run on load and whenever the
    /// branch list changes rather than during render, so a repository with tens of thousands of
    /// branches is scanned once instead of on every keystroke.
    ///
    /// Only repositories whose branches are already in hand are judged — the selected one, plus
    /// any the cache happens to hold. Nothing is fetched here, so a link in a repository nobody
    /// has opened stays unflagged rather than being guessed at and offered for deletion.
    /// </summary>
    private void MarkMissingBranches()
    {
        foreach (var link in existingBranches)
        {
            if (link.BelongsTo(selectedRepo?.Id))
            {
                link.BranchMissing = branches.Count > 0 && !BranchIndex.Contains(link.BranchName);
                continue;
            }

            var known = BranchCache.Peek(link.RepositoryId);
            link.BranchMissing = known is { Count: > 0 }
                                 && !known.Contains(link.BranchName, StringComparer.OrdinalIgnoreCase);
        }
    }

    private IReadOnlyList<string>? indexedList;
    private HashSet<string> branchIndex = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Case-insensitive membership over the loaded branches, rebuilt only when the list instance
    /// changes. Scanning the list per lookup would be ~18,000 comparisons per link on every
    /// re-render, and the page re-renders on every keystroke in the work-item search box.
    /// </summary>
    private HashSet<string> BranchIndex
    {
        get
        {
            if (ReferenceEquals(indexedList, branches)) return branchIndex;

            branchIndex = new HashSet<string>(branches, StringComparer.OrdinalIgnoreCase);
            indexedList = branches;
            return branchIndex;
        }
    }

    private string ExistingBranchTitle(WorkItemBranchLink link) =>
        string.IsNullOrEmpty(link.RepositoryName)
            ? $"{link.BranchName} — repository not in this collection"
            : $"{link.RepositoryName} — {link.BranchName}";

    // ─── Validation ───
    private string? BranchNameError => ValidateBranchName(branchName);

    private bool CanCheck => selectedRepo != null && BranchNameError == null;

    private bool CanSubmit => CanCheck && selectedItem != null && !string.IsNullOrWhiteSpace(baseBranch);

    /// <summary>
    /// Git ref rules that matter here, plus the 100-character limit the form promises. The name
    /// ends up in a ref path, so it is validated before it reaches TFS rather than after.
    /// </summary>
    private static string? ValidateBranchName(string? raw)
    {
        var name = (raw ?? "").Trim();
        if (name.Length == 0) return "Branch name is required.";

        foreach (var rule in BranchNameRules)
            if (rule.Fails(name)) return rule.Message;

        return null;
    }

    // ─── Check Branch ───
    private async Task CheckBranch()
    {
        if (!CanCheck) return;
        checking = true; resultMsg = ""; StateHasChanged();
        try
        {
            var name = branchName.Trim();
            var existing = await TfsApi.GetRefAsync(selectedRepo!.Project, selectedRepo.Id, $"refs/heads/{name}");
            if (existing != null)
            {
                resultMsg = $"<span style='color:var(--amber);'>⚠ Branch <code>{Enc(name)}</code> already exists (SHA: {Enc(WorkItemBadges.ShortSha(existing.ObjectId))})</span>";
                resultOk = false;
            }
            else
            {
                resultMsg = $"<span style='color:var(--green);'>✓ Branch name <code>{Enc(name)}</code> is available in {Enc(selectedRepo.Name)}.</span>";
                resultOk = true;
            }
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Branch availability check failed in {Repo}", selectedRepo?.Name);
            resultMsg = $"<span style='color:var(--red);'>✕ Check failed: {Enc(ex.Message)}</span>";
            resultOk = false;
        }
        checking = false;
    }

    // ─── Clear ───
    private void ClearForm()
    {
        selectedItem = null;
        branchName = ""; prName = "";
        existingBranches = new();
        existingErr = "";
        resultMsg = "";
        unlinkCandidate = null;
    }

    // ─── Create Branch ───
    private async Task CreateBranch()
    {
        if (!CanSubmit) return;

        var repo = selectedRepo!;
        var item = selectedItem!;
        var name = branchName.Trim();

        creating = true; resultMsg = ""; StateHasChanged();
        try
        {
            var baseRef = await TfsApi.GetRefAsync(repo.Project, repo.Id, $"refs/heads/{baseBranch}");
            if (baseRef == null) throw new InvalidOperationException($"Base branch \"{baseBranch}\" not found in {repo.Name}.");

            var existing = await TfsApi.GetRefAsync(repo.Project, repo.Id, $"refs/heads/{name}");
            if (existing != null)
                throw new InvalidOperationException($"Branch \"{name}\" already exists ({WorkItemBadges.ShortSha(existing.ObjectId)}).");

            await TfsApi.CreateBranchAsync(repo.Project, repo.Id, name, baseRef.ObjectId);
            AdoptCreatedBranch(repo, name);

            resultMsg = BuildSuccessMessage(repo, item, name, baseRef.ObjectId, await LinkWorkItemAsync(repo, item, name));
            resultOk = true;

            await LoadExistingBranches(item);
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Branch creation failed: {Branch} in {Repo}", name, repo.Name);
            resultMsg = $"<span style='color:var(--red);font-weight:700;'>✕ {Enc(ex.Message)}</span>";
            resultOk = false;
        }
        creating = false;
    }

    /// <summary>
    /// Folds the new branch into the list the picker is showing and drops the cached copy, so the
    /// next read comes from TFS. Re-listing here instead would cost a full paged fetch — seconds on
    /// a large repository — to learn one branch name we already know.
    /// </summary>
    private void AdoptCreatedBranch(TfsRepo repo, string branch)
    {
        BranchCache.Invalidate(repo.Id);

        if (branches.Contains(branch, StringComparer.OrdinalIgnoreCase)) return;
        branches = branches.Append(branch).OrderBy(b => b, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>
    /// Links the new branch to the work item. The work item is addressed in its own project while
    /// the artifact carries the repository's, so a branch in a repository belonging to another
    /// project links just as well as one alongside the work item.
    /// </summary>
    private async Task<string> LinkWorkItemAsync(TfsRepo repo, WorkItem item, string branch)
    {
        var repoProjectId = ResolveProjectId(repo.Project);
        if (repoProjectId == null)
        {
            Logger.LogWarning("Project {Project} was not in the loaded list, so {Branch} could not be linked",
                repo.Project, branch);
            return LinkRow("var(--amber)", $"⚠ Link skipped — project &quot;{Enc(repo.Project)}&quot; was not found. Reconnect in Settings and link it from the work item.");
        }

        try
        {
            var result = await TfsApi.LinkWorkItemAsync(item.Project, item.Id, repoProjectId, repo.Id, branch);
            return result.Outcome switch
            {
                WorkItemLinkOutcome.Linked =>
                    LinkRow("var(--green)", $"✓ Branch linked to #{Enc(item.Id)} ({Enc(item.Type)})"),
                WorkItemLinkOutcome.AlreadyLinked =>
                    LinkRow("var(--green)", $"✓ Already linked to #{Enc(item.Id)} — the work item kept this branch's link"),
                _ => LinkRow("var(--amber)", $"⚠ Link failed: {Enc(result.Detail)}")
            };
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Could not link branch {Branch} to work item {Id}", branch, item.Id);
            return LinkRow("var(--amber)", $"⚠ Link failed: {Enc(ex.Message)}");
        }
    }

    private string? ResolveProjectId(string projectName) =>
        PageState.TfsData.Projects
            .FirstOrDefault(p => string.Equals(p.Name, projectName, StringComparison.OrdinalIgnoreCase))?.Id;

    /// <summary>Content is pre-encoded by the caller; this only wraps it in the result row markup.</summary>
    private static string LinkRow(string colour, string encodedContent) =>
        $"<div class='modal-row'><span class='label'>Linked</span><span style='color:{colour};'>{encodedContent}</span></div>";

    private string BuildSuccessMessage(TfsRepo repo, WorkItem item, string branch, string sha, string linkedMsg)
    {
        var areaDisp = selectedArea != null
            ? $"\\{selectedArea.Project}\\Area\\{TfsApiService.AreaDisplayPath(selectedArea.Path)}"
            : "";

        return $@"
<div style='font-size:.78rem;font-weight:700;color:var(--green);margin-bottom:8px;'>✅ Branch created successfully!</div>
<div class='modal-row'><span class='label'>Branch</span><span style='font-family:var(--mono);font-size:.68rem;'>{Enc(branch)}</span></div>
<div class='modal-row'><span class='label'>Repo</span><span>{Enc(repo.Project)} → {Enc(repo.Name)}</span></div>
<div class='modal-row'><span class='label'>Area</span><span>{Enc(areaDisp)}</span></div>
<div class='modal-row'><span class='label'>Base</span><span><code>{Enc(baseBranch)}</code> @ {Enc(WorkItemBadges.ShortSha(sha))}</span></div>
<div class='modal-row'><span class='label'>PR Name</span><span>{Enc(prName)}</span></div>
<div class='modal-row'><span class='label'>New SHA</span><span style='font-family:var(--mono);'>{Enc(WorkItemBadges.ShortSha(sha))}</span></div>
{linkedMsg}";
    }

}
