using System.Text.Json;
using DevKit.Web.Models;

namespace DevKit.Web.Services;

/// <summary>
/// The Tasks / Bugs audits and the Burndown, plus the field parsing the two share. Burndown
/// hours come from each work item's revision history, stamped in IST.
/// </summary>
public partial class TfsApiService
{
    // ═══ AUDIT (Tasks / Bugs tabs) ═══
    public async Task<AuditData> LoadAuditDataAsync(string project, string areaPath, string iterPath)
    {
        var data = new AuditData();

        data.Tasks = await FetchWithRelations(project, await WiqlIds(project,
            $"[System.WorkItemType] = 'Task' AND [System.AreaPath] UNDER '{WiqlEscape(areaPath)}' AND [System.IterationPath] = '{WiqlEscape(iterPath)}'"), "Task");

        data.Bugs = await FetchWithRelations(project, await WiqlIds(project,
            $"[System.WorkItemType] = 'Bug' AND [System.AreaPath] UNDER '{WiqlEscape(areaPath)}' AND [System.IterationPath] = '{WiqlEscape(iterPath)}'"), "Bug");

        // Parent-requirement tags (for prefix-check exclusions).
        var parentIds = data.Tasks.Select(t => t.ParentId).Where(p => int.TryParse(p, out _)).Distinct().Select(int.Parse).ToList();
        if (parentIds.Count > 0)
            foreach (var p in await FetchFieldsByIds(project, parentIds, "System.Tags,System.Title,System.WorkItemType"))
                data.ParentTags[p.Id] = p.Tags;

        // Bug child tasks (reuse already-loaded tasks; fetch any missing).
        var childById = data.Tasks.ToDictionary(t => t.Id, t => t);
        var missing = data.Bugs.SelectMany(b => b.ChildIds).Distinct()
            .Where(id => !childById.ContainsKey(id) && int.TryParse(id, out _)).Select(int.Parse).ToList();
        if (missing.Count > 0)
            foreach (var c in await FetchWithRelations(project, missing, "Task")) childById[c.Id] = c;

        foreach (var b in data.Bugs)
            data.BugChildren[b.Id] = b.ChildIds.Where(childById.ContainsKey).Select(id => childById[id])
                .Where(c => string.Equals(c.Type, "Task", StringComparison.OrdinalIgnoreCase)).ToList();

        return data;
    }

    /// <summary>
    /// §5.3 Flag Provided Date: walks revision history chronologically and returns the timestamp
    /// of the most recent transition into (state = Resolved AND tags contain "Published").
    /// </summary>
    public async Task<DateTime?> GetPublishedFlagDateAsync(string project, string workItemId)
    {
        try
        {
            var data = await GetJson(ProjectApi(project, $"wit/workitems/{workItemId}/updates?api-version={Ver}"));
            if (!data.TryGetProperty("value", out var arr) || arr.ValueKind != JsonValueKind.Array) return null;

            string state = "", tags = "";
            DateTime? flag = null;
            var prevBoth = false;
            foreach (var up in arr.EnumerateArray())
            {
                DateTime? when = null;
                if (up.TryGetProperty("fields", out var f))
                {
                    state = FieldNewValue(f, "System.State") ?? state;
                    tags = FieldNewValue(f, "System.Tags") ?? tags;
                    var cd = FieldNewValue(f, "System.ChangedDate");
                    if (cd != null && DateTime.TryParse(cd, out var dt)) when = dt;
                }
                if (when == null && up.TryGetProperty("revisedDate", out var rd) && rd.ValueKind == JsonValueKind.String
                    && DateTime.TryParse(rd.GetString(), out var rdt)) when = rdt;

                var both = string.Equals(state, "Resolved", StringComparison.OrdinalIgnoreCase) && TagsContain(tags, "Published");
                if (both && !prevBoth && when.HasValue) flag = when;
                prevBoth = both;
            }
            return flag;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Flag-date history fetch failed for {Id}", workItemId);
            return null;
        }
    }

    // ═══ BURNDOWN (Hours Updated by Members, §3) ═══
    private const int BurndownConcurrency = 8;
    private static readonly TimeSpan IstOffset = TimeSpan.FromMinutes(330);

    /// <summary>
    /// Per-member CompletedWork deltas for the current iteration's tasks + bugs, classified
    /// regular vs. bug. Revision history is fetched per item (no batch API) with bounded
    /// concurrency, so this is the heaviest load in the hub.
    /// </summary>
    public async Task<List<BurndownContribution>> LoadBurndownAsync(string project, string areaPath, string iterPath)
    {
        var taskIds = await WiqlIds(project, $"[System.WorkItemType] = 'Task' AND [System.AreaPath] UNDER '{WiqlEscape(areaPath)}' AND [System.IterationPath] = '{WiqlEscape(iterPath)}'");
        var bugIds = await WiqlIds(project, $"[System.WorkItemType] = 'Bug' AND [System.AreaPath] UNDER '{WiqlEscape(areaPath)}' AND [System.IterationPath] = '{WiqlEscape(iterPath)}'");
        var items = await FetchWithRelations(project, taskIds.Concat(bugIds).ToList(), "Task");

        var bugSet = new HashSet<string>(bugIds.Select(i => i.ToString()));
        bool IsBugWork(WorkItem w) =>
            BugWork.Is(w, !string.IsNullOrEmpty(w.ParentId) && bugSet.Contains(w.ParentId));

        var contributions = new List<BurndownContribution>();
        foreach (var chunk in items.Chunk(BurndownConcurrency))
        {
            var batches = await Task.WhenAll(chunk.Select(async w =>
            {
                var deltas = await GetCompletedWorkDeltasAsync(project, w.Id);
                var isBug = IsBugWork(w);
                return deltas.Select(d => new BurndownContribution
                {
                    Date = d.Date,
                    MemberDisplay = d.Who,
                    MemberKey = PlanningCapacityCalculator.NormalizeMemberName(d.Who),
                    ItemId = w.Id,
                    ItemTitle = w.Title,
                    ItemArea = w.Area,
                    ItemType = w.Type,
                    IsBug = isBug,
                    Delta = d.Delta
                }).ToList();
            }));
            foreach (var b in batches) contributions.AddRange(b);
        }
        return contributions;
    }

    /// <summary>CompletedWork changes from a work item's revision history, dated in IST (§1.1, §3.2).</summary>
    public async Task<List<(DateTime Date, string Who, double Delta)>> GetCompletedWorkDeltasAsync(string project, string id)
    {
        var result = new List<(DateTime, string, double)>();
        try
        {
            var data = await GetJson(ProjectApi(project, $"wit/workitems/{id}/updates?api-version={Ver}"));
            if (!data.TryGetProperty("value", out var arr) || arr.ValueKind != JsonValueKind.Array) return result;

            foreach (var up in arr.EnumerateArray())
            {
                if (!up.TryGetProperty("fields", out var f)) continue;
                if (!f.TryGetProperty("Microsoft.VSTS.Scheduling.CompletedWork", out var cw)) continue;

                var delta = FieldNum(cw, "newValue") - FieldNum(cw, "oldValue");
                if (Math.Abs(delta) < 0.001) continue;

                var who = ChangedByName(f) ?? (up.TryGetProperty("revisedBy", out var rb) && rb.TryGetProperty("displayName", out var rbn) ? rbn.GetString() ?? "" : "");
                var when = ChangedDateIst(f) ?? RevisedDateIst(up);
                if (when == null || string.IsNullOrEmpty(who)) continue;

                result.Add((when.Value, who, delta));
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Revision history fetch failed for {Id}", id);
        }
        return result;
    }

    private static double FieldNum(JsonElement el, string prop)
    {
        if (!el.TryGetProperty(prop, out var v)) return 0;
        if (v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out var d)) return d;
        if (v.ValueKind == JsonValueKind.String && double.TryParse(v.GetString(), out var s)) return s;
        return 0;
    }

    private static string? ChangedByName(JsonElement fields)
    {
        if (!fields.TryGetProperty("System.ChangedBy", out var cb) || !cb.TryGetProperty("newValue", out var nv)) return null;
        if (nv.ValueKind == JsonValueKind.Object && nv.TryGetProperty("displayName", out var dn)) return dn.GetString();
        if (nv.ValueKind == JsonValueKind.String)
        {
            var s = nv.GetString() ?? "";
            return s.Contains('<') ? s.Split('<')[0].Trim() : s;
        }
        return null;
    }

    private static DateTime? ChangedDateIst(JsonElement fields)
    {
        if (fields.TryGetProperty("System.ChangedDate", out var cd) && cd.TryGetProperty("newValue", out var nv)
            && nv.ValueKind == JsonValueKind.String && DateTimeOffset.TryParse(nv.GetString(), out var dto))
            return dto.ToOffset(IstOffset).Date;
        return null;
    }

    private static DateTime? RevisedDateIst(JsonElement update)
        => update.TryGetProperty("revisedDate", out var rd) && rd.ValueKind == JsonValueKind.String
           && DateTimeOffset.TryParse(rd.GetString(), out var dto) && dto.Year > 1
            ? dto.ToOffset(IstOffset).Date : null;

    // ─── audit helpers ───
    private async Task<List<int>> WiqlIds(string project, string where, CancellationToken ct = default)
    {
        var wiql = $"SELECT [System.Id] FROM WorkItems WHERE {where} ORDER BY [System.Id]";
        var res = await PostJson(ProjectApi(project, $"wit/wiql?api-version={Ver}"), new { query = wiql }, ct);
        return res.GetProperty("workItems").EnumerateArray().Select(w => w.GetProperty("id").GetInt32()).ToList();
    }

    private async Task<List<WorkItem>> FetchWithRelations(string project, List<int> ids, string defaultType)
    {
        var items = new List<WorkItem>();
        foreach (var chunk in ids.Chunk(WorkItemBatchSize))
        {
            var data = await GetJson(ProjectApi(project, $"wit/workitems?ids={string.Join(",", chunk)}&$expand=relations&api-version={Ver}"));
            foreach (var wi in data.GetProperty("value").EnumerateArray())
                items.Add(ParseAuditItem(wi, project, defaultType));
        }
        return items;
    }

    /// <summary>
    /// Batches fetched concurrently under the shared gate, flattened back in batch order. The ids come
    /// from other items' links, which can point anywhere: the read is collection-wide so an item in another
    /// project resolves, and one TFS cannot return (deleted, or not readable with this PAT) is left out
    /// and logged (errorPolicy=omit) instead of failing its whole batch.
    /// </summary>
    private async Task<List<WorkItem>> FetchFieldsByIds(string project, List<int> ids, string fields, CancellationToken ct = default)
    {
        var pages = await Task.WhenAll(ids.Chunk(WorkItemBatchSize).Select(chunk =>
            GetJsonThrottled(Api($"wit/workitems?ids={string.Join(",", chunk)}&fields={fields}&errorPolicy=omit&api-version={Ver}"), ct)));

        var items = pages
            .SelectMany(TfsJson.Values)
            .Where(wi => wi.ValueKind == JsonValueKind.Object && wi.TryGetProperty("fields", out _))
            .Select(ParseFieldsItem)
            .ToList();
        LogUnreturned(project, ids, items);
        return items;
    }

    private static WorkItem ParseFieldsItem(JsonElement wi)
    {
        var f = wi.GetProperty("fields");
        return new WorkItem
        {
            Id = wi.GetProperty("id").GetInt32().ToString(),
            Tags = GetStr(f, "System.Tags", ""),
            Title = GetStr(f, "System.Title", ""),
            Type = GetStr(f, "System.WorkItemType", "")
        };
    }

    /// <summary>Names the linked items TFS left out, so a dangling link can be found and fixed in TFS.</summary>
    private void LogUnreturned(string project, List<int> requested, List<WorkItem> returned)
    {
        var returnedIds = returned.Select(w => w.Id).ToHashSet(StringComparer.Ordinal);
        var missing = requested.Select(id => id.ToString()).Where(id => !returnedIds.Contains(id)).ToList();
        if (missing.Count == 0)
        {
            return;
        }

        _logger.LogWarning(
            "{Count} work item(s) linked from {Project} could not be read and were left out (deleted, or not readable with this PAT): {Ids}",
            missing.Count, project, string.Join(", ", missing));
    }

    private WorkItem ParseAuditItem(JsonElement wi, string project, string defaultType)
    {
        var f = wi.GetProperty("fields");
        var iter = GetStr(f, "System.IterationPath", "");
        var item = new WorkItem
        {
            Id = wi.GetProperty("id").GetInt32().ToString(),
            Title = GetStr(f, "System.Title", ""),
            State = GetStr(f, "System.State", ""),
            Assigned = GetAssigned(f),
            Area = GetStr(f, "System.AreaPath", ""),
            IterationPath = iter,
            Sprint = iter.Split('\\').LastOrDefault() ?? "",
            Type = GetStr(f, "System.WorkItemType", defaultType),
            Project = project,
            Discipline = GetStr(f, "Microsoft.VSTS.Common.Discipline", ""),
            Tags = GetStr(f, "System.Tags", ""),
            Reason = GetStr(f, "System.Reason", ""),
            RootCause = GetStr(f, "Microsoft.VSTS.CMMI.RootCause", ""),
            ProposedFix = GetStr(f, "Microsoft.VSTS.CMMI.ProposedFix", ""),
            Severity = GetStr(f, "Microsoft.VSTS.Common.Severity", ""),
            CreatedBy = GetPersonField(f, "System.CreatedBy"),
            TaskExecutionType = GetStr(f, "Casepoint.TFS.CustomFields.TaskExecutionType", ""),
            OriginalEstimate = GetEffortNum(f, "Microsoft.VSTS.Scheduling.OriginalEstimate"),
            RemainingWork = GetEffortNum(f, "Microsoft.VSTS.Scheduling.RemainingWork"),
            CompletedWork = GetEffortNum(f, "Microsoft.VSTS.Scheduling.CompletedWork"),
            RevisedEstimateField = GetEffortNum(f, "Microsoft.VSTS.Scheduling.RevisedEstimate")
                ?? GetEffortNum(f, "Casepoint.TFS.CustomFields.RevisedEstimate")
                ?? GetEffortNum(f, "Custom.RevisedEstimate")
        };
        ApplyRelations(wi, item);
        return item;
    }

    private static void ApplyRelations(JsonElement wi, WorkItem item)
    {
        if (!wi.TryGetProperty("relations", out var rels) || rels.ValueKind != JsonValueKind.Array) return;
        foreach (var r in rels.EnumerateArray())
        {
            var rel = r.TryGetProperty("rel", out var rl) ? rl.GetString() ?? "" : "";
            var id = IdFromUrl(r.TryGetProperty("url", out var u) ? u.GetString() ?? "" : "");
            if (id == null) continue;
            if (rel == "System.LinkTypes.Hierarchy-Reverse") item.ParentId = id;
            else if (rel == "System.LinkTypes.Hierarchy-Forward") item.ChildIds.Add(id);
            else if (rel.StartsWith("System.LinkTypes.Duplicate", StringComparison.Ordinal)) item.DuplicateLinkIds.Add(id);
        }
    }

    private static string? IdFromUrl(string url)
    {
        var i = url.LastIndexOf('/');
        return i >= 0 && i < url.Length - 1 && int.TryParse(url[(i + 1)..], out _) ? url[(i + 1)..] : null;
    }

    private static string GetPersonField(JsonElement fields, string prop)
    {
        if (!fields.TryGetProperty(prop, out var a)) return "";
        if (a.ValueKind == JsonValueKind.Object && a.TryGetProperty("displayName", out var dn)) return dn.GetString() ?? "";
        if (a.ValueKind == JsonValueKind.String)
        {
            var s = a.GetString() ?? "";
            return s.Contains('<') ? s.Split('<')[0].Trim() : s;
        }
        return "";
    }

    private static string? FieldNewValue(JsonElement fields, string prop)
        => fields.TryGetProperty(prop, out var fld) && fld.TryGetProperty("newValue", out var nv) && nv.ValueKind == JsonValueKind.String
            ? nv.GetString() : null;

    private static bool TagsContain(string? tags, string tag) =>
        !string.IsNullOrEmpty(tags)
        && tags.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
               .Any(t => string.Equals(t, tag, StringComparison.OrdinalIgnoreCase));

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

}
