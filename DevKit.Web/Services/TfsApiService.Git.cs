using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using DevKit.Web.Models;

namespace DevKit.Web.Services;

/// <summary>
/// Branches, the work-item links that point at them, and pull requests. A branch link is an
/// artifact-link relation on the work item, so linking and unlinking are work-item patches.
/// </summary>
public partial class TfsApiService
{
    // ═══ BRANCHES ═══
    // Branch listings go through BranchCacheService, which pages the refs endpoint. The unpaged
    // GetBranchesAsync that used to live here asked for every ref in one request and timed out on
    // repositories holding tens of thousands of them.

    /// <summary>
    /// Branches already linked to a work item, across every repository, read from the work item's
    /// own artifact links. This is one request — the alternative, scanning every repository's refs
    /// for the id, is ~50 paged listings per click and still misses a branch whose name omits the id.
    /// </summary>
    public async Task<List<WorkItemBranchLink>> GetWorkItemBranchLinksAsync(
        string project, string workItemId, CancellationToken ct = default)
    {
        // The id lands in a URL path segment, so it is confirmed numeric rather than escaped.
        if (!int.TryParse(workItemId, out var numericId) || numericId <= 0)
            return new List<WorkItemBranchLink>();

        try
        {
            var data = await _req.GetJsonAsync(
                ProjectApi(project, $"wit/workitems/{numericId}?$expand=relations&api-version={Ver}"), ct);
            return ParseBranchLinks(data);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not read branch links for work item {Id}", numericId);
            throw;
        }
    }

    /// <summary>
    /// Work items and the branches each links, for many ids at once. Batches are read in parallel under
    /// the shared gate and collection-wide, so an id from any project resolves; an id that no longer
    /// exists is left out (errorPolicy=omit) rather than failing its whole batch. A failed batch
    /// throws — a partial answer would make linked branches look unattached.
    /// </summary>
    public async Task<List<WorkItemBranches>> GetBranchLinksAsync(IEnumerable<int> workItemIds, CancellationToken ct = default)
    {
        var ids = workItemIds.Where(id => id > 0).Distinct().ToList();
        if (ids.Count == 0) return new List<WorkItemBranches>();

        var pages = await Task.WhenAll(ids.Chunk(WorkItemBatchSize).Select(chunk => GetJsonThrottled(
            Api($"wit/workitems?ids={string.Join(",", chunk)}&$expand=relations&errorPolicy=omit&api-version={Ver}"), ct)));

        return pages
            .SelectMany(TfsJson.Values)
            .Where(wi => wi.ValueKind == JsonValueKind.Object && wi.TryGetProperty("fields", out _))
            .Select(wi => new WorkItemBranches(
                ParseAuditItem(wi, GetStr(wi.GetProperty("fields"), "System.TeamProject", ""), ""),
                ParseBranchLinks(wi)))
            .ToList();
    }

    /// <summary>The distinct branches a work item's artifact links point at.</summary>
    private static List<WorkItemBranchLink> ParseBranchLinks(JsonElement workItem)
    {
        var links = new List<WorkItemBranchLink>();
        if (!workItem.TryGetProperty("relations", out var rels) || rels.ValueKind != JsonValueKind.Array) return links;

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var rel in rels.EnumerateArray())
        {
            var parsed = GitRefArtifactLink.TryParse(GetStr(rel, "url", ""));
            if (parsed != null && seen.Add($"{parsed.RepositoryId}\u0000{parsed.BranchName}")) links.Add(parsed);
        }
        return links;
    }

    public async Task<TfsRef?> GetRefAsync(string project, string repoId, string refName)
    {
        var filter = refName.Replace("refs/", "");
        var data = await GetJson(ProjectApi(project, $"git/repositories/{repoId}/refs?filter={Uri.EscapeDataString(filter)}&api-version={Ver}"));
        foreach (var r in data.GetProperty("value").EnumerateArray())
        {
            if (r.GetProperty("name").GetString() == refName)
                return new TfsRef { Name = r.GetProperty("name").GetString()!, ObjectId = r.GetProperty("objectId").GetString()! };
        }
        return null;
    }

    // ═══ CREATE BRANCH ═══
    public async Task<JsonElement> CreateBranchAsync(string project, string repoId, string branchName, string baseSha)
    {
        var body = new[] { new { name = $"refs/heads/{branchName}", newObjectId = baseSha, oldObjectId = EmptyObjectId } };
        using var req = new HttpRequestMessage(HttpMethod.Post, ProjectApi(project, $"git/repositories/{repoId}/refs?api-version={Ver}"))
        {
            Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json")
        };
        using var resp = await SendAuthed(req);
        return JsonSerializer.Deserialize<JsonElement>(await resp.Content.ReadAsStringAsync());
    }

    // ═══ LIST + DELETE BRANCHES (Branch Delete) ═══

    /// <summary>Most branches one delete request carries, so one refusal never spans a huge batch.</summary>
    public const int MaxBranchesPerDelete = 100;

    /// <summary>Every branch in the repository with its object id and creator, paged. See <see cref="GitRefListing"/>.</summary>
    public Task<GitRefListing.Result> ListBranchRefsAsync(TfsRepo repo, CancellationToken ct = default) =>
        GitRefListing.ListBranchesAsync(_req, repo, ct);

    /// <summary>
    /// Deletes branches in one request and reports what TFS did with each. Every ref carries the object
    /// id it had when it was listed, so a branch someone has pushed to since is refused rather than
    /// deleted from under them.
    /// </summary>
    public async Task<List<BranchDeleteOutcome>> DeleteBranchesAsync(TfsRepo repo, IReadOnlyList<TfsRef> refs, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(repo);
        ArgumentNullException.ThrowIfNull(refs);
        if (refs.Count == 0) return new List<BranchDeleteOutcome>();
        if (refs.Count > MaxBranchesPerDelete)
            throw new ArgumentOutOfRangeException(nameof(refs), $"At most {MaxBranchesPerDelete} branches can be deleted per request.");

        var body = refs.Select(r => new { name = $"refs/heads/{r.Name}", oldObjectId = r.ObjectId, newObjectId = EmptyObjectId }).ToArray();
        var response = await _req.PostJsonAsync(
            ProjectApi(repo.Project, $"git/repositories/{Uri.EscapeDataString(repo.Id)}/refs?api-version={Ver}"), body, ct);
        return GitRefUpdate.Outcomes(repo, refs, response);
    }

    // ═══ LINK WORK ITEM ═══

    /// <summary>
    /// Links a branch to a work item.
    ///
    /// Two details make this more than a single PATCH. A work item and the repository holding the
    /// branch can live in different projects, so the work item is addressed under
    /// <paramref name="workItemProject"/> while the artifact URI carries the repository's project —
    /// using the repository's project for both is what made linking fail whenever the chosen
    /// repository belonged to another project. And TFS refuses a relation that already exists: a
    /// work item keeps its branch link after the branch is deleted, so recreating that same branch
    /// produces a duplicate. That is reported as already-linked rather than as a failure.
    /// </summary>
    public async Task<WorkItemLinkResult> LinkWorkItemAsync(
        string workItemProject, string workItemId, string repoProjectId, string repoId,
        string branchName, CancellationToken ct = default)
    {
        var branch = (branchName ?? "").Trim();

        if (!int.TryParse(workItemId, out var numericId) || numericId <= 0)
            return WorkItemLinkResult.Failed($"\"{workItemId}\" is not a valid work item id.");
        if (string.IsNullOrWhiteSpace(workItemProject))
            return WorkItemLinkResult.Failed("The work item's project is unknown.");
        if (string.IsNullOrWhiteSpace(repoProjectId) || string.IsNullOrWhiteSpace(repoId))
            return WorkItemLinkResult.Failed("The repository's project could not be resolved.");
        if (branch.Length == 0)
            return WorkItemLinkResult.Failed("The branch name is empty.");

        if (await HasBranchLinkAsync(workItemProject, workItemId, repoId, branch, ct))
            return WorkItemLinkResult.AlreadyLinked();

        try
        {
            await SendBranchLinkPatchAsync(workItemProject, numericId, repoProjectId, repoId, branch, ct);
            return WorkItemLinkResult.Linked();
        }
        catch (Exception ex)
        {
            // TFS rejects a duplicate relation with a message whose wording is not contractual,
            // so the relations are re-read rather than the error text matched.
            if (await HasBranchLinkAsync(workItemProject, workItemId, repoId, branch, ct))
                return WorkItemLinkResult.AlreadyLinked();

            _logger.LogWarning(ex, "Linking branch {Branch} to work item {Id} failed", branch, numericId);
            return WorkItemLinkResult.Failed(ex.Message);
        }
    }

    /// <summary>
    /// Whether the work item already points at this branch. A read failure answers false so the
    /// PATCH below is still attempted; it is the PATCH that decides.
    /// </summary>
    private async Task<bool> HasBranchLinkAsync(
        string project, string workItemId, string repoId, string branch, CancellationToken ct)
    {
        try
        {
            var links = await GetWorkItemBranchLinksAsync(project, workItemId, ct);
            return links.Any(l => l.Matches(repoId, branch));
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not read existing branch links for work item {Id}", workItemId);
            return false;
        }
    }

    /// <summary>
    /// Removes the relation linking a work item to a branch — the stale link TFS leaves behind
    /// when a branch is deleted.
    ///
    /// JSON Patch addresses a relation by its index in the work item's relations array, which
    /// makes a blind remove dangerous: any concurrent edit shifts the indexes and the wrong
    /// relation goes. The work item's revision is therefore read alongside the index and sent as
    /// a "test" operation, so TFS rejects the whole patch if anything changed in between.
    /// </summary>
    public async Task<WorkItemLinkResult> UnlinkWorkItemBranchAsync(
        string workItemProject, string workItemId, string repoId, string branchName,
        CancellationToken ct = default)
    {
        var branch = (branchName ?? "").Trim();

        if (!int.TryParse(workItemId, out var numericId) || numericId <= 0)
            return WorkItemLinkResult.Failed($"\"{workItemId}\" is not a valid work item id.");
        if (string.IsNullOrWhiteSpace(workItemProject))
            return WorkItemLinkResult.Failed("The work item's project is unknown.");
        if (string.IsNullOrWhiteSpace(repoId) || branch.Length == 0)
            return WorkItemLinkResult.Failed("The branch to unlink was not identified.");

        try
        {
            var (rev, index) = await FindBranchRelationAsync(workItemProject, numericId, repoId, branch, ct);
            if (index < 0) return WorkItemLinkResult.NotLinked();

            // Built as nodes rather than anonymous objects because the two operations have
            // different shapes — "remove" must not carry a value property.
            var patch = new JsonArray
            {
                new JsonObject { ["op"] = "test", ["path"] = "/rev", ["value"] = rev },
                new JsonObject { ["op"] = "remove", ["path"] = $"/relations/{index}" }
            };

            using var req = new HttpRequestMessage(HttpMethod.Patch,
                ProjectApi(workItemProject, $"wit/workitems/{numericId}?api-version={Ver}"))
            {
                Content = new StringContent(patch.ToJsonString(), Encoding.UTF8, "application/json-patch+json")
            };
            using var resp = await SendAuthed(req, ct);

            // Relations are removed by index, so success is confirmed against the work item rather
            // than assumed from a 200 — if the intended link is somehow still there, that is
            // reported instead of a false success.
            if (await HasBranchLinkAsync(workItemProject, workItemId, repoId, branch, ct))
                return WorkItemLinkResult.Failed("TFS accepted the change but the link is still on the work item.");

            _logger.LogInformation("Removed stale branch link {Branch} from work item {Id}", branch, numericId);
            return WorkItemLinkResult.Unlinked();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Removing branch link {Branch} from work item {Id} failed", branch, numericId);
            return WorkItemLinkResult.Failed(ex.Message);
        }
    }

    /// <summary>
    /// The work item's current revision, and the index of the relation pointing at this branch
    /// (-1 when there is none). The index counts every relation, not just branch links, because
    /// that is what the patch path addresses.
    /// </summary>
    private async Task<(int Rev, int Index)> FindBranchRelationAsync(
        string project, int workItemId, string repoId, string branch, CancellationToken ct)
    {
        var data = await _req.GetJsonAsync(
            ProjectApi(project, $"wit/workitems/{workItemId}?$expand=relations&api-version={Ver}"), ct);

        var rev = data.TryGetProperty("rev", out var r) && r.TryGetInt32(out var revValue) ? revValue : 0;
        if (!data.TryGetProperty("relations", out var rels) || rels.ValueKind != JsonValueKind.Array)
            return (rev, -1);

        var index = 0;
        foreach (var rel in rels.EnumerateArray())
        {
            var parsed = GitRefArtifactLink.TryParse(GetStr(rel, "url", ""));
            if (parsed != null && parsed.Matches(repoId, branch)) return (rev, index);
            index++;
        }
        return (rev, -1);
    }

    private async Task SendBranchLinkPatchAsync(
        string project, int workItemId, string repoProjectId, string repoId, string branch, CancellationToken ct)
    {
        // The Git branch artifact ref is "GB" + the branch name WITHOUT the
        // "refs/heads/" prefix. Including the prefix makes TFS resolve a ref
        // named "refs/heads/refs/heads/<branch>", which doesn't exist — the work
        // item then shows "Branch not found or no permission to access it".
        // Slashes inside the branch name belong to that single ref segment, so
        // they must be percent-encoded; this matches the artifact link TFS
        // generates when a branch is created from the work item directly.
        var artifactUri = $"vstfs:///Git/Ref/{Uri.EscapeDataString(repoProjectId)}/{Uri.EscapeDataString(repoId)}/GB{Uri.EscapeDataString(branch)}";
        var patchBody = new[] {
            new {
                op = "add",
                path = "/relations/-",
                value = new {
                    rel = "ArtifactLink",
                    url = artifactUri,
                    attributes = new { name = "Branch" }
                }
            }
        };
        using var req = new HttpRequestMessage(HttpMethod.Patch, ProjectApi(project, $"wit/workitems/{workItemId}?api-version={Ver}"))
        {
            Content = new StringContent(JsonSerializer.Serialize(patchBody), Encoding.UTF8, "application/json-patch+json")
        };
        using var resp = await SendAuthed(req, ct);
    }

    // ═══ PULL REQUESTS ═══

    /// <summary>A repository's pull requests; empty when TFS cannot be read, which the Merge Tool shows as none.</summary>
    public async Task<List<TfsPullRequest>> GetPullRequestsAsync(string project, string repoId)
    {
        try
        {
            return await FetchPullRequestsAsync(project, repoId, $"status=all&$top={MaxPullRequests}", default);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to load pull requests for repo {RepoId}", repoId);
            return new List<TfsPullRequest>();
        }
    }

    /// <summary>
    /// Source branches of the repository's active pull requests. Throws when TFS cannot be read: an empty
    /// answer would let a branch with an open pull request be deleted from under it.
    /// </summary>
    public async Task<HashSet<string>> GetActivePullRequestBranchesAsync(TfsRepo repo, CancellationToken ct = default)
    {
        var prs = await FetchPullRequestsAsync(repo.Project, repo.Id, $"searchCriteria.status=active&$top={MaxActivePullRequests}", ct);
        return prs.Select(pr => GitRefName.Strip(pr.SourceRefName))
                  .Where(branch => branch.Length > 0)
                  .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    private async Task<List<TfsPullRequest>> FetchPullRequestsAsync(string project, string repoId, string criteria, CancellationToken ct)
    {
        var data = await GetJson(ProjectApi(project, $"git/repositories/{Uri.EscapeDataString(repoId)}/pullrequests?api-version={Ver}&{criteria}"), ct);
        return data.GetProperty("value").EnumerateArray().Select(pr => new TfsPullRequest
        {
            PullRequestId = pr.GetProperty("pullRequestId").GetInt32(),
            Title = GetStr(pr, "title", ""),
            Description = pr.TryGetProperty("description", out var desc) ? desc.GetString() : null,
            Status = GetStr(pr, "status", "active"),
            SourceRefName = GetStr(pr, "sourceRefName", ""),
            TargetRefName = GetStr(pr, "targetRefName", ""),
            CreationDate = pr.TryGetProperty("creationDate", out var cd) ? cd.GetDateTime() : null,
            CreatedBy = pr.TryGetProperty("createdBy", out var cb) && cb.TryGetProperty("displayName", out var dn)
                ? new TfsPrAuthor { DisplayName = dn.GetString() } : null
        }).ToList();
    }

    public async Task<List<TfsCommit>> GetPrCommitsAsync(string project, string repoId, int prId)
    {
        try
        {
            var data = await GetJson(ProjectApi(project, $"git/repositories/{repoId}/pullrequests/{prId}/commits?api-version={Ver}"));
            return data.GetProperty("value").EnumerateArray().Select(c => new TfsCommit
            {
                CommitId = GetStr(c, "commitId", ""),
                Comment = c.TryGetProperty("comment", out var cm) ? cm.GetString() : null,
                Author = ParsePerson(c, "author"),
                Committer = ParsePerson(c, "committer")
            }).ToList();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to load commits for PR {PrId}", prId);
            return new List<TfsCommit>();
        }
    }

}
