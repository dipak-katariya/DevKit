using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using DevKit.Web.Models;

namespace DevKit.Web.Services;

public class TfsApiService
{
    private const int MaxWorkItems = 200;
    private const int WorkItemBatchSize = 50;
    private const int MaxPullRequests = 200;
    private const string EmptyObjectId = "0000000000000000000000000000000000000000";

    private readonly HttpClient _http;
    private readonly SettingsService _settings;
    private readonly ILogger<TfsApiService> _logger;

    private static readonly string[] ExcludedAreas = {
        "CasepointARA", "508 Compliance Team", "Core Framework Web", "DA Tools Utilities",
        "DataSite Team", "Mobile App", "Review Utilities", "Document Export", "AI", "ARA core"
    };

    public TfsApiService(HttpClient http, SettingsService settings, ILogger<TfsApiService> logger)
    {
        _http = http;
        _settings = settings;
        _logger = logger;
    }

    private string Url => _settings.Tfs.Url.TrimEnd('/');
    private string Ver => _settings.Tfs.ApiVersion;

    private string Api(string path) => $"{Url}/_apis/{path}";
    private string ProjectApi(string project, string path) => $"{Url}/{Uri.EscapeDataString(project)}/_apis/{path}";

    // WIQL string literals are single-quoted; an embedded apostrophe must be doubled,
    // both to keep the query valid for legitimate names (e.g. "Q1'26") and to prevent
    // a crafted path from altering the query.
    private static string WiqlEscape(string value) => (value ?? "").Replace("'", "''");

    private AuthenticationHeaderValue AuthHeader()
    {
        var cred = Convert.ToBase64String(Encoding.ASCII.GetBytes($":{_settings.Tfs.Pat}"));
        return new AuthenticationHeaderValue("Basic", cred);
    }

    private async Task<HttpResponseMessage> SendAuthed(HttpRequestMessage req)
    {
        req.Headers.Authorization = AuthHeader();
        var resp = await _http.SendAsync(req);
        try
        {
            await EnsureSuccess(resp);
        }
        catch
        {
            resp.Dispose();
            throw;
        }
        return resp;
    }

    // EnsureSuccessStatusCode() throws with only the status code; TFS returns a useful
    // "message" in the body. Surface it so the page-level handlers can show a real error.
    private static async Task EnsureSuccess(HttpResponseMessage resp)
    {
        if (resp.IsSuccessStatusCode) return;
        var detail = "";
        try
        {
            var body = await resp.Content.ReadAsStringAsync();
            if (!string.IsNullOrWhiteSpace(body))
            {
                using var doc = JsonDocument.Parse(body);
                if (doc.RootElement.TryGetProperty("message", out var m) && m.ValueKind == JsonValueKind.String)
                    detail = m.GetString() ?? "";
            }
        }
        catch (JsonException)
        {
            // Body was not JSON — fall back to the status line below.
        }
        throw new HttpRequestException(
            $"TFS request failed ({(int)resp.StatusCode} {resp.ReasonPhrase})" +
            (string.IsNullOrEmpty(detail) ? "" : $": {detail}"));
    }

    private async Task<JsonElement> GetJson(string url)
    {
        // Force a fresh read: TFS/proxies can serve stale work-item GETs, which makes
        // freshly-saved sizes appear unchanged after reloading the Planning tab.
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.CacheControl = new CacheControlHeaderValue { NoCache = true, NoStore = true, MustRevalidate = true };
        req.Headers.Pragma.ParseAdd("no-cache");
        using var resp = await SendAuthed(req);
        var json = await resp.Content.ReadAsStringAsync();
        return JsonSerializer.Deserialize<JsonElement>(json);
    }

    private async Task<JsonElement> PostJson(string url, object body)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json")
        };
        using var resp = await SendAuthed(req);
        var json = await resp.Content.ReadAsStringAsync();
        return JsonSerializer.Deserialize<JsonElement>(json);
    }

    // ═══ PROJECTS ═══
    public async Task<List<TfsProject>> GetProjectsAsync()
    {
        var data = await GetJson(Api($"projects?api-version={Ver}"));
        return data.GetProperty("value").EnumerateArray()
            .Select(p => new TfsProject { Id = p.GetProperty("id").GetString()!, Name = p.GetProperty("name").GetString()! })
            .OrderBy(p => p.Name).ToList();
    }

    // ═══ REPOSITORIES ═══
    public async Task<List<TfsRepo>> GetRepositoriesAsync()
    {
        try
        {
            var data = await GetJson(Api($"git/repositories?api-version={Ver}"));
            return data.GetProperty("value").EnumerateArray()
                .Where(r => r.TryGetProperty("id", out _) && r.TryGetProperty("name", out _))
                .Select(r => new TfsRepo
                {
                    Id = r.GetProperty("id").GetString()!,
                    Name = r.GetProperty("name").GetString()!,
                    Project = r.TryGetProperty("project", out var proj) && proj.TryGetProperty("name", out var pn) ? pn.GetString()! : "",
                    RemoteUrl = r.TryGetProperty("remoteUrl", out var ru) ? ru.GetString() ?? "" : ""
                })
                .Where(r => !string.IsNullOrEmpty(r.Project))
                .OrderBy(r => $"{r.Project}/{r.Name}").ToList();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to load repositories from TFS");
            return new List<TfsRepo>();
        }
    }

    // ═══ AREAS ═══
    public async Task<List<TfsArea>> GetAreasAsync(string project)
    {
        try
        {
            var data = await GetJson(ProjectApi(project, $"wit/classificationnodes/areas?$depth=10&api-version={Ver}"));
            var nodes = FlattenNodes(data);
            return nodes
                .Where(n => !ExcludedAreas.Any(ex => string.Equals(n.Name, ex, StringComparison.OrdinalIgnoreCase)))
                .Select(n => new TfsArea { Id = n.Id, Name = n.Name, Path = n.Path, Project = project })
                .ToList();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to load areas for project {Project}", project);
            return new List<TfsArea>();
        }
    }

    // ═══ ITERATIONS ═══
    public async Task<List<TfsIteration>> GetIterationsAsync(string project)
    {
        try
        {
            var data = await GetJson(ProjectApi(project, $"wit/classificationnodes/iterations?$depth=5&api-version={Ver}"));
            return FlattenNodes(data)
                .Select(n => new TfsIteration { Id = n.Id, Name = n.Name, Path = n.Path, Project = project })
                .ToList();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to load iterations for project {Project}", project);
            return new List<TfsIteration>();
        }
    }

    // ═══ WIQL + WORK ITEMS ═══
    public async Task<List<WorkItem>> LoadWorkItemsAsync(string project, string areaPath, string? iterPath)
    {
        var areaFilter = $"[System.AreaPath] = '{WiqlEscape(areaPath)}'";
        var iterFilter = !string.IsNullOrEmpty(iterPath)
            ? $"[System.IterationPath] = '{WiqlEscape(iterPath)}'"
            : $"[System.IterationPath] UNDER '{WiqlEscape(project)}'";

        var wiql = $"SELECT [System.Id] FROM WorkItems WHERE [System.WorkItemType] IN ('Requirement','Change Request','Bug') AND {areaFilter} AND {iterFilter} ORDER BY [System.Id]";

        var wiqlResult = await PostJson(ProjectApi(project, $"wit/wiql?api-version={Ver}"), new { query = wiql });

        var ids = wiqlResult.GetProperty("workItems").EnumerateArray()
            .Select(w => w.GetProperty("id").GetInt32())
            .Take(MaxWorkItems).ToList();

        if (ids.Count == 0) return new List<WorkItem>();

        var fields = "System.Id,System.Title,System.State,System.AssignedTo,System.AreaPath,System.IterationPath,System.WorkItemType";
        var items = new List<WorkItem>();

        foreach (var chunk in ids.Chunk(WorkItemBatchSize))
        {
            var idsParam = string.Join(",", chunk);
            var data = await GetJson(ProjectApi(project, $"wit/workitems?ids={idsParam}&fields={fields}&api-version={Ver}"));
            foreach (var wi in data.GetProperty("value").EnumerateArray())
            {
                var f = wi.GetProperty("fields");
                items.Add(new WorkItem
                {
                    Id = wi.GetProperty("id").GetInt32().ToString(),
                    Title = GetStr(f, "System.Title", $"#{wi.GetProperty("id")}"),
                    State = GetStr(f, "System.State", "Active"),
                    Assigned = GetAssigned(f),
                    Area = GetStr(f, "System.AreaPath", areaPath),
                    Sprint = GetStr(f, "System.IterationPath", "").Split('\\').LastOrDefault() ?? "",
                    Type = GetStr(f, "System.WorkItemType", "Requirement"),
                    Project = project
                });
            }
        }
        return items;
    }

    // ═══ PLANNING — REQUIREMENTS WITH TASKS ═══

    /// <summary>
    /// Loads all Requirements/Change Requests/Bugs in a sprint with their effort fields.
    /// </summary>
    public async Task<List<WorkItem>> LoadPlanningItemsAsync(string project, string iterPath)
    {
        var wiql = $@"SELECT [System.Id] FROM WorkItems
            WHERE [System.WorkItemType] IN ('Requirement','Change Request','Bug')
            AND [System.IterationPath] = '{WiqlEscape(iterPath)}'
            ORDER BY [System.Id]";

        var wiqlResult = await PostJson(ProjectApi(project, $"wit/wiql?api-version={Ver}"), new { query = wiql });

        var ids = wiqlResult.GetProperty("workItems").EnumerateArray()
            .Select(w => w.GetProperty("id").GetInt32())
            .ToList();

        if (ids.Count == 0) return new List<WorkItem>();

        // Include effort / size / story points fields
        var fields = "System.Id,System.Title,System.State,System.AssignedTo,System.AreaPath,System.IterationPath,System.WorkItemType,Microsoft.VSTS.Scheduling.Effort,Microsoft.VSTS.Scheduling.OriginalEstimate,Microsoft.VSTS.Scheduling.RemainingWork,Microsoft.VSTS.Scheduling.CompletedWork,Microsoft.VSTS.Scheduling.StoryPoints,Microsoft.VSTS.Scheduling.Size";
        var items = new List<WorkItem>();

        foreach (var chunk in ids.Chunk(WorkItemBatchSize))
        {
            var idsParam = string.Join(",", chunk);
            var data = await GetJson(ProjectApi(project, $"wit/workitems?ids={idsParam}&fields={fields}&api-version={Ver}"));
            foreach (var wi in data.GetProperty("value").EnumerateArray())
            {
                var f = wi.GetProperty("fields");
                items.Add(new WorkItem
                {
                    Id = wi.GetProperty("id").GetInt32().ToString(),
                    Title = GetStr(f, "System.Title", "(no title)"),
                    State = GetStr(f, "System.State", "Active"),
                    Assigned = GetAssigned(f),
                    Area = GetStr(f, "System.AreaPath", ""),
                    Sprint = GetStr(f, "System.IterationPath", "").Split('\\').LastOrDefault() ?? "",
                    Type = GetStr(f, "System.WorkItemType", "Requirement"),
                    Project = project,
                    // Read priority must match the write order in PushSize (Size first for CMMI Requirements/Change Requests).
                    Effort = GetEffortNum(f, "Microsoft.VSTS.Scheduling.Size")
                        ?? GetEffortNum(f, "Microsoft.VSTS.Scheduling.Effort")
                        ?? GetEffortNum(f, "Microsoft.VSTS.Scheduling.StoryPoints")
                });
            }
        }
        return items;
    }

    /// <summary>
    /// Gets all child Tasks of a work item via WorkItemLinks WIQL query.
    /// </summary>
    public async Task<List<WorkItem>> GetChildTasksAsync(string project, string parentId)
    {
        if (!int.TryParse(parentId, out var parentIdNum))
            return new List<WorkItem>();

        var wiql = $@"SELECT [System.Id] FROM WorkItemLinks
            WHERE [Source].[System.Id] = {parentIdNum}
            AND [System.Links.LinkType] = 'System.LinkTypes.Hierarchy-Forward'
            MODE (Recursive)";

        var wiqlResult = await PostJson(ProjectApi(project, $"wit/wiql?api-version={Ver}"), new { query = wiql });

        if (!wiqlResult.TryGetProperty("workItemRelations", out var rels))
            return new List<WorkItem>();

        var childIds = rels.EnumerateArray()
            .Where(r => r.TryGetProperty("target", out var t) && t.TryGetProperty("id", out _)
                     && r.TryGetProperty("source", out var s) && s.ValueKind == JsonValueKind.Object)
            .Select(r => r.GetProperty("target").GetProperty("id").GetInt32())
            .Where(id => id != parentIdNum)
            .Distinct()
            .ToList();

        if (childIds.Count == 0) return new List<WorkItem>();

        var fields = "System.Id,System.Title,System.State,System.AssignedTo,System.IterationPath,System.WorkItemType,Microsoft.VSTS.Scheduling.OriginalEstimate,Microsoft.VSTS.Scheduling.RemainingWork,Microsoft.VSTS.Scheduling.CompletedWork";
        var items = new List<WorkItem>();

        foreach (var chunk in childIds.Chunk(WorkItemBatchSize))
        {
            var idsParam = string.Join(",", chunk);
            var data = await GetJson(ProjectApi(project, $"wit/workitems?ids={idsParam}&fields={fields}&api-version={Ver}"));
            foreach (var wi in data.GetProperty("value").EnumerateArray())
            {
                var f = wi.GetProperty("fields");
                var type = GetStr(f, "System.WorkItemType", "Task");
                if (type != "Task") continue;
                var iterPath = GetStr(f, "System.IterationPath", "");
                items.Add(new WorkItem
                {
                    Id = wi.GetProperty("id").GetInt32().ToString(),
                    Title = GetStr(f, "System.Title", "(no title)"),
                    State = GetStr(f, "System.State", "Active"),
                    Assigned = GetAssigned(f),
                    IterationPath = iterPath,
                    Sprint = iterPath.Split('\\').LastOrDefault() ?? "",
                    Type = type,
                    Project = project,
                    OriginalEstimate = GetEffortNum(f, "Microsoft.VSTS.Scheduling.OriginalEstimate"),
                    RemainingWork = GetEffortNum(f, "Microsoft.VSTS.Scheduling.RemainingWork"),
                    CompletedWork = GetEffortNum(f, "Microsoft.VSTS.Scheduling.CompletedWork")
                });
            }
        }
        return items.OrderBy(t => ParseIdOrZero(t.Id)).ToList();
    }

    /// <summary>
    /// Updates a single field on a work item using JSON Patch.
    /// </summary>
    public async Task UpdateWorkItemFieldAsync(string project, string workItemId, string fieldName, object value)
    {
        var patchBody = new[] {
            new {
                op = "add",
                path = $"/fields/{fieldName}",
                value
            }
        };
        using var req = new HttpRequestMessage(HttpMethod.Patch, ProjectApi(project, $"wit/workitems/{workItemId}?api-version={Ver}"))
        {
            Content = new StringContent(JsonSerializer.Serialize(patchBody), Encoding.UTF8, "application/json-patch+json")
        };
        using var resp = await SendAuthed(req);
    }

    // ═══ BRANCHES ═══
    public async Task<List<string>> GetBranchesAsync(string project, string repoId)
    {
        var data = await GetJson(ProjectApi(project, $"git/repositories/{repoId}/refs?filter=heads/&api-version={Ver}"));
        return data.GetProperty("value").EnumerateArray()
            .Select(r => r.GetProperty("name").GetString()!.Replace("refs/heads/", ""))
            .OrderBy(b => b).ToList();
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

    // ═══ DELETE BRANCH ═══
    public async Task DeleteBranchAsync(string project, string repoId, string branchName, string objectId)
    {
        var body = new[] { new { name = $"refs/heads/{branchName}", newObjectId = EmptyObjectId, oldObjectId = objectId } };
        using var req = new HttpRequestMessage(HttpMethod.Post, ProjectApi(project, $"git/repositories/{repoId}/refs?api-version={Ver}"))
        {
            Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json")
        };
        using var resp = await SendAuthed(req);
    }

    /// <summary>Gets all refs with full details (name + objectId) for a repo.</summary>
    public async Task<List<TfsRef>> GetRefsAsync(string project, string repoId)
    {
        var data = await GetJson(ProjectApi(project, $"git/repositories/{repoId}/refs?filter=heads/&api-version={Ver}"));
        return data.GetProperty("value").EnumerateArray()
            .Select(r => new TfsRef { Name = r.GetProperty("name").GetString()!.Replace("refs/heads/", ""), ObjectId = r.GetProperty("objectId").GetString()! })
            .OrderBy(r => r.Name).ToList();
    }

    // ═══ LINK WORK ITEM ═══
    public async Task LinkWorkItemAsync(string project, string projectId, string repoId, string workItemId, string branchName)
    {
        // The Git branch artifact ref is "GB" + the branch name WITHOUT the
        // "refs/heads/" prefix. Including the prefix makes TFS resolve a ref
        // named "refs/heads/refs/heads/<branch>", which doesn't exist — the work
        // item then shows "Branch not found or no permission to access it".
        // Slashes inside the branch name belong to that single ref segment, so
        // they must be percent-encoded; this matches the artifact link TFS
        // generates when a branch is created from the work item directly.
        var artifactUri = $"vstfs:///Git/Ref/{Uri.EscapeDataString(projectId)}/{Uri.EscapeDataString(repoId)}/GB{Uri.EscapeDataString(branchName)}";
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
        using var resp = await SendAuthed(req);
    }

    // ═══ PULL REQUESTS ═══
    public async Task<List<TfsPullRequest>> GetPullRequestsAsync(string project, string repoId)
    {
        try
        {
            var data = await GetJson(ProjectApi(project, $"git/repositories/{repoId}/pullrequests?api-version={Ver}&status=all&$top={MaxPullRequests}"));
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
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to load pull requests for repo {RepoId}", repoId);
            return new List<TfsPullRequest>();
        }
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

    // ═══ URL BUILDERS ═══
    public string WorkItemUrl(string project, string id) =>
        $"{Url}/{Uri.EscapeDataString(project)}/_workitems/edit/{id}";

    public string CommitUrl(string project, string repoName, string sha) =>
        $"{Url}/{Uri.EscapeDataString(project)}/_git/{Uri.EscapeDataString(repoName)}/commit/{sha}";

    // ═══ HELPERS ═══
    private static int ParseIdOrZero(string id) => int.TryParse(id, out var n) ? n : 0;

    private static string GetStr(JsonElement el, string prop, string fallback) =>
        el.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? fallback : fallback;

    private static double? GetEffortNum(JsonElement fields, string prop)
    {
        if (!fields.TryGetProperty(prop, out var v)) return null;
        if (v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out var d)) return d;
        if (v.ValueKind == JsonValueKind.String && double.TryParse(v.GetString(), out var s)) return s;
        return null;
    }

    private static string GetAssigned(JsonElement fields)
    {
        if (!fields.TryGetProperty("System.AssignedTo", out var a)) return "-";
        if (a.ValueKind == JsonValueKind.Object && a.TryGetProperty("displayName", out var dn))
            return dn.GetString() ?? "-";
        if (a.ValueKind == JsonValueKind.String)
        {
            var s = a.GetString() ?? "-";
            return s.Contains('<') ? s.Split('<')[0].Trim() : s;
        }
        return "-";
    }

    private static TfsCommitPerson? ParsePerson(JsonElement el, string prop)
    {
        if (!el.TryGetProperty(prop, out var p)) return null;
        return new TfsCommitPerson
        {
            Name = p.TryGetProperty("name", out var n) ? n.GetString() : null,
            Date = p.TryGetProperty("date", out var d) ? d.GetDateTime() : null
        };
    }

    public static string ToWiqlPath(string rawPath)
    {
        var p = rawPath.TrimStart('\\');
        // Remove TFS internal "Area" or "Iteration" structural node
        var parts = p.Split('\\').ToList();
        if (parts.Count > 1 && (parts[1] == "Area" || parts[1] == "Iteration"))
            parts.RemoveAt(1);
        return string.Join("\\", parts);
    }

    public static string AreaDisplayPath(string path)
    {
        var parts = path.TrimStart('\\').Split('\\').Where(s => !string.IsNullOrEmpty(s)).ToList();
        if (parts.Count > 1) parts = parts.Skip(1).ToList();
        if (parts.Count > 0 && parts[0] == "Area") parts = parts.Skip(1).ToList();
        return parts.Count > 0 ? string.Join(" > ", parts) : "(root)";
    }

    private List<(string Id, string Name, string Path)> FlattenNodes(JsonElement node)
    {
        var result = new List<(string, string, string)>();
        FlattenNodesRecursive(node, result);
        return result;
    }

    private void FlattenNodesRecursive(JsonElement node, List<(string Id, string Name, string Path)> result)
    {
        if (node.TryGetProperty("name", out var name))
        {
            var id = node.TryGetProperty("id", out var idEl) ? idEl.ToString() : name.GetString()!;
            var path = node.TryGetProperty("path", out var pathEl) ? pathEl.GetString()! : name.GetString()!;
            result.Add((id, name.GetString()!, path));
        }
        if (node.TryGetProperty("children", out var children))
        {
            foreach (var child in children.EnumerateArray())
                FlattenNodesRecursive(child, result);
        }
    }
}
