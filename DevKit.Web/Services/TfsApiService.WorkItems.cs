using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using DevKit.Web.Models;

namespace DevKit.Web.Services;

/// <summary>Reading work items by WIQL, and the field updates and task creation that write them back.</summary>
public partial class TfsApiService
{
    /// <summary>The project tag list exists from API 6.0 on, so it is asked for at that version whatever the server's default.</summary>
    private const string TagsApiVersion = "6.0-preview.1";

    private const string JsonPatchMediaType = "application/json-patch+json";

    /// <summary>The fields Update status reads before it writes; rev comes back with any field list.</summary>
    private const string UpdateReadFields = "System.TeamProject,System.WorkItemType,System.Title,System.Tags";

    // ═══ WIQL + WORK ITEMS ═══

    /// <summary>
    /// Ids of every work item under the area in any of the sprints, whatever its type: a branch can be
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

    public async Task<List<WorkItem>> LoadWorkItemsAsync(string project, string areaPath, string? iterPath, CancellationToken ct = default)
    {
        var areaFilter = $"[System.AreaPath] = '{WiqlEscape(areaPath)}'";
        var iterFilter = !string.IsNullOrEmpty(iterPath)
            ? $"[System.IterationPath] = '{WiqlEscape(iterPath)}'"
            : $"[System.IterationPath] UNDER '{WiqlEscape(project)}'";

        var wiql = $"SELECT [System.Id] FROM WorkItems WHERE [System.WorkItemType] IN ('Requirement','Change Request','Bug') AND {areaFilter} AND {iterFilter} ORDER BY [System.Id]";

        var wiqlResult = await PostJson(ProjectApi(project, $"wit/wiql?api-version={Ver}"), new { query = wiql }, ct);

        var ids = wiqlResult.GetProperty("workItems").EnumerateArray()
            .Select(w => w.GetProperty("id").GetInt32())
            .Take(MaxWorkItems).ToList();

        if (ids.Count == 0) return new List<WorkItem>();

        var fields = "System.Id,System.Title,System.State,System.AssignedTo,System.AreaPath,System.IterationPath,System.WorkItemType";
        var items = new List<WorkItem>();

        foreach (var chunk in ids.Chunk(WorkItemBatchSize))
        {
            var idsParam = string.Join(",", chunk);
            var data = await GetJson(ProjectApi(project, $"wit/workitems?ids={idsParam}&fields={fields}&api-version={Ver}"), ct);
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

    // ═══ PLANNING: REQUIREMENTS WITH TASKS ═══

    /// <summary>
    /// Loads all Requirements/Change Requests/Bugs in a sprint with their effort fields.
    /// </summary>
    public async Task<List<WorkItem>> LoadPlanningItemsAsync(string project, string iterPath, CancellationToken ct = default)
    {
        var wiql = $@"SELECT [System.Id] FROM WorkItems
            WHERE [System.WorkItemType] IN ('Requirement','Change Request','Bug')
            AND [System.IterationPath] = '{WiqlEscape(iterPath)}'
            ORDER BY [System.Id]";

        var wiqlResult = await PostJson(ProjectApi(project, $"wit/wiql?api-version={Ver}"), new { query = wiql }, ct);

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
            var data = await GetJson(ProjectApi(project, $"wit/workitems?ids={idsParam}&fields={fields}&api-version={Ver}"), ct);
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
    public async Task<List<WorkItem>> GetChildTasksAsync(string project, string parentId, CancellationToken ct = default)
    {
        if (!int.TryParse(parentId, out var parentIdNum))
            return new List<WorkItem>();

        var wiql = $@"SELECT [System.Id] FROM WorkItemLinks
            WHERE [Source].[System.Id] = {parentIdNum}
            AND [System.Links.LinkType] = 'System.LinkTypes.Hierarchy-Forward'
            MODE (Recursive)";

        var wiqlResult = await PostJson(ProjectApi(project, $"wit/wiql?api-version={Ver}"), new { query = wiql }, ct);

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
            var data = await GetJson(ProjectApi(project, $"wit/workitems?ids={idsParam}&fields={fields}&api-version={Ver}"), ct);
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
    public async Task UpdateWorkItemFieldAsync(
        string project, string workItemId, string fieldName, object value, CancellationToken ct = default)
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
            Content = new StringContent(JsonSerializer.Serialize(patchBody), Encoding.UTF8, JsonPatchMediaType)
        };
        using var resp = await SendAuthed(req, ct);
    }

    // ═══ TASK CREATION (Planning Management, §6) ═══
    /// <summary>Creates the child Task <paramref name="task"/> describes and returns its work-item id.</summary>
    public async Task<int> CreateChildTaskAsync(ChildTaskRequest task, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(task);
        ArgumentException.ThrowIfNullOrWhiteSpace(task.Tag);
        var parentUrl = $"{Url}/_apis/wit/workitems/{task.ParentId}";
        var patch = new List<object>
        {
            new { op = "add", path = "/fields/System.Title", value = task.Title },
            new { op = "add", path = "/fields/Microsoft.VSTS.Common.Discipline", value = task.Discipline },
            new { op = "add", path = "/fields/System.Tags", value = task.Tag },
            new { op = "add", path = "/fields/System.AreaPath", value = task.AreaPath },
            new { op = "add", path = "/fields/System.IterationPath", value = task.IterationPath }
        };
        if (!string.IsNullOrEmpty(task.ExecutionType))
        {
            patch.Add(new { op = "add", path = "/fields/Casepoint.TFS.CustomFields.TaskExecutionType", value = task.ExecutionType });
        }

        patch.Add(new { op = "add", path = "/relations/-", value = new { rel = "System.LinkTypes.Hierarchy-Reverse", url = parentUrl } });
        using var req = new HttpRequestMessage(HttpMethod.Post, ProjectApi(task.Project, $"wit/workitems/$Task?api-version={Ver}"))
        {
            Content = new StringContent(JsonSerializer.Serialize(patch), Encoding.UTF8, JsonPatchMediaType)
        };
        using var resp = await SendAuthed(req, ct);
        var el = JsonSerializer.Deserialize<JsonElement>(await resp.Content.ReadAsStringAsync(ct));
        return el.TryGetProperty("id", out var idEl) ? idEl.GetInt32() : 0;
    }

    /// <summary>
    /// The TFS connection the lists read here belong to, so a list kept for one server is never served for another.
    /// </summary>
    internal string ConnectionKey => Url;

    /// <summary>The tags used in <paramref name="project"/>, by name, as TFS offers them when tagging a work item.</summary>
    public async Task<List<string>> GetProjectTagsAsync(string project, CancellationToken ct = default)
    {
        var data = await GetJson(ProjectApi(project, $"wit/tags?api-version={TagsApiVersion}"), ct);
        return TfsJson.ObjectValues(data)
            .Select(tag => TfsJson.Str(tag, "name").Trim())
            .Where(name => name.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>The names of the work item types <paramref name="project"/> has.</summary>
    public async Task<List<string>> GetWorkItemTypeNamesAsync(string project, CancellationToken ct = default)
    {
        var data = await GetJson(ProjectApi(project, $"wit/workitemtypes?api-version={Ver}"), ct);
        return TfsJson.ObjectValues(data).Select(type => TfsJson.Str(type, "name")).Where(name => name.Length > 0).ToList();
    }

    /// <summary>The fields of the work item type <paramref name="type"/>, each with the values it allows.</summary>
    public async Task<List<WorkItemTypeField>> GetWorkItemTypeFieldsAsync(string project, string type, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(type);
        var data = await GetJson(
            ProjectApi(project, $"wit/workitemtypes/{Uri.EscapeDataString(type)}/fields?$expand=allowedValues&api-version={Ver}"), ct);
        return TfsJson.ObjectValues(data)
            .Select(field => new WorkItemTypeField(TfsJson.Str(field, "referenceName"), TfsJson.Str(field, "name"), AllowedValues(field)))
            .Where(field => field.ReferenceName.Length > 0)
            .ToList();
    }

    /// <summary>
    /// One work item as Update status needs it before writing: its revision, project, type, title, tags and the value
    /// of <paramref name="field"/> (empty when it has none, or no field is given). Only those fields are read. A field
    /// the collection does not have is read as empty, as TFS refuses a field list that names one.
    /// </summary>
    public async Task<WorkItemForUpdate> GetWorkItemForUpdateAsync(int id, string? field, CancellationToken ct = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(id);
        var asked = StatusUpdateSettings.IsFieldReference(field) ? field : null;
        JsonElement data;
        try
        {
            data = await GetJson(WorkItemForUpdateUrl(id, asked), ct);
        }
        catch (HttpRequestException ex) when (asked is not null && ex.StatusCode == HttpStatusCode.BadRequest)
        {
            _logger.LogError(ex, "TFS refused field {Field} when reading work item {WorkItemId} for Update status; it is read without it",
                asked, id);
            asked = null;
            data = await GetJson(WorkItemForUpdateUrl(id, asked), ct);
        }

        return ReadForUpdate(id, data, asked);
    }

    private string WorkItemForUpdateUrl(int id, string? field)
    {
        var fields = field is null ? UpdateReadFields : $"{UpdateReadFields},{field}";
        return Api($"wit/workitems/{id}?fields={Uri.EscapeDataString(fields)}&api-version={Ver}");
    }

    private static WorkItemForUpdate ReadForUpdate(int id, JsonElement data, string? field)
    {
        if (!data.TryGetProperty("fields", out var fields) || fields.ValueKind != JsonValueKind.Object)
        {
            throw new TfsResponseException($"TFS answered without the fields of work item #{id}.");
        }

        var rev = TfsJson.Int(data, "rev");
        var project = TfsJson.Str(fields, "System.TeamProject");
        var type = TfsJson.Str(fields, "System.WorkItemType");
        if (rev <= 0 || project.Length == 0 || type.Length == 0)
        {
            throw new TfsResponseException($"TFS answered without the revision, project or type of work item #{id}.");
        }

        return new WorkItemForUpdate(id, rev, project, type, TfsJson.Str(fields, "System.Title"), TfsJson.Str(fields, "System.Tags"),
            FieldText(fields, field));
    }

    /// <summary>
    /// Writes what <paramref name="plan"/> changes in one request, guarded by the revision it was read at, so a change
    /// someone made meanwhile is never overwritten: TFS then refuses the write (409 or 412) and nothing changes. A
    /// success answer is enough: its body is not read, so a reply DevKit cannot parse never turns a write that was made
    /// into a failure.
    /// </summary>
    public async Task UpdateWorkItemStatusAsync(StatusUpdatePlan plan, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(plan.Item.Id);
        var operations = new JsonArray { PatchOperation("test", "/rev", plan.Item.Rev) };
        if (plan.NewTags is { } tags)
        {
            operations.Add(PatchOperation("add", "/fields/System.Tags", tags));
        }

        if (plan.Field is { NewValue: { } value } field)
        {
            // The reference name goes into the path of the write, so only a real one passes.
            if (!StatusUpdateSettings.IsFieldReference(field.Reference))
            {
                throw new ArgumentException($"\"{field.Reference}\" is not a field reference name.", nameof(plan));
            }

            operations.Add(PatchOperation("add", $"/fields/{field.Reference}", value));
        }

        using var req = new HttpRequestMessage(HttpMethod.Patch, ProjectApi(plan.Item.Project, $"wit/workitems/{plan.Item.Id}?api-version={Ver}"))
        {
            Content = new StringContent(operations.ToJsonString(), Encoding.UTF8, JsonPatchMediaType)
        };
        using var resp = await SendAuthed(req, ct);
    }

    private static JsonObject PatchOperation(string op, string path, JsonNode? value) =>
        new() { ["op"] = op, ["path"] = path, ["value"] = value };

    private static List<string> AllowedValues(JsonElement field) =>
        field.TryGetProperty("allowedValues", out var values) && values.ValueKind == JsonValueKind.Array
            ? values.EnumerateArray().Where(v => v.ValueKind == JsonValueKind.String).Select(v => v.GetString()!).ToList()
            : new List<string>();

    /// <summary>
    /// A field's value as text: numbers and flags as TFS writes them, nothing as empty. The name is matched in any case,
    /// as the planner and Settings match reference names.
    /// </summary>
    private static string FieldText(JsonElement fields, string? field)
    {
        if (string.IsNullOrEmpty(field))
        {
            return "";
        }

        var value = fields.EnumerateObject()
            .FirstOrDefault(p => string.Equals(p.Name, field, StringComparison.OrdinalIgnoreCase))
            .Value;
        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString() ?? "",
            JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False => value.GetRawText(),
            _ => ""
        };
    }
}
