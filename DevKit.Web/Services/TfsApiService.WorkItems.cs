using System.Text;
using System.Text.Json;
using DevKit.Web.Models;

namespace DevKit.Web.Services;

/// <summary>Reading work items by WIQL, and the field updates and task creation that write them back.</summary>
public partial class TfsApiService
{
    // ═══ WIQL + WORK ITEMS ═══

    /// <summary>
    /// Ids of every work item under the area in any of the sprints, whatever its type — a branch can be
    /// linked from a requirement, a bug or a task alike. Paths may be raw; they are normalised here.
    /// </summary>
    public Task<List<int>> QuerySprintWorkItemIdsAsync(
        string project, string areaPath, IReadOnlyList<string> sprintPaths, CancellationToken ct = default)
    {
        var where = TfsRequestClient.WiqlIterationClause(sprintPaths.Select(ToWiqlPath));
        if (!string.IsNullOrWhiteSpace(areaPath))
            where += $" AND [System.AreaPath] UNDER '{WiqlEscape(ToWiqlPath(areaPath))}'";
        return WiqlIds(project, where, ct);
    }

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

    // ═══ TASK CREATION (Planning Management, §6) ═══
    /// <summary>
    /// Creates a child Task under a Requirement, Change Request or Bug with the given discipline and
    /// tag (see <see cref="PlanningTaskCatalog.TagFor"/>), inheriting the supplied area/iteration.
    /// Returns the new work-item id.
    /// </summary>
    public async Task<int> CreateChildTaskAsync(string project, string parentId, string title, string discipline, string areaPath, string iterPath,
        string? executionType = null, string tag = PlanningTaskCatalog.ProductTag)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tag);
        var parentUrl = $"{Url}/_apis/wit/workitems/{parentId}";
        var patch = new List<object>
        {
            new { op = "add", path = "/fields/System.Title", value = title },
            new { op = "add", path = "/fields/Microsoft.VSTS.Common.Discipline", value = discipline },
            new { op = "add", path = "/fields/System.Tags", value = tag },
            new { op = "add", path = "/fields/System.AreaPath", value = areaPath },
            new { op = "add", path = "/fields/System.IterationPath", value = iterPath }
        };
        if (!string.IsNullOrEmpty(executionType))
            patch.Add(new { op = "add", path = "/fields/Casepoint.TFS.CustomFields.TaskExecutionType", value = executionType });
        patch.Add(new { op = "add", path = "/relations/-", value = new { rel = "System.LinkTypes.Hierarchy-Reverse", url = parentUrl } });
        using var req = new HttpRequestMessage(HttpMethod.Post, ProjectApi(project, $"wit/workitems/$Task?api-version={Ver}"))
        {
            Content = new StringContent(JsonSerializer.Serialize(patch), Encoding.UTF8, "application/json-patch+json")
        };
        using var resp = await SendAuthed(req);
        var el = JsonSerializer.Deserialize<JsonElement>(await resp.Content.ReadAsStringAsync());
        return el.TryGetProperty("id", out var idEl) ? idEl.GetInt32() : 0;
    }

}
