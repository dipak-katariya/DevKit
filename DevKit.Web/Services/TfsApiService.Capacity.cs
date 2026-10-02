using System.Text.Json;
using DevKit.Web.Models;

namespace DevKit.Web.Services;

/// <summary>
/// Teams, sprint capacity and the tasks the Capacity Planning tab plans against. Each team is
/// read in parallel and merged in team order, so the numbers do not depend on which reply lands first.
/// </summary>
public partial class TfsApiService
{
    // ═══ TEAMS + CAPACITY (Insight Hub) ═══
    public async Task<List<TfsTeam>> GetTeamsAsync(string project, CancellationToken ct = default)
    {
        try
        {
            var data = await GetJson(Api($"projects/{Uri.EscapeDataString(project)}/teams?api-version={Ver}"), ct);
            return data.GetProperty("value").EnumerateArray()
                .Select(t => new TfsTeam { Id = t.GetProperty("id").GetString()!, Name = t.GetProperty("name").GetString()! })
                .ToList();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Failed to load teams for project {Project}", project);
            return new List<TfsTeam>();
        }
    }

    /// <summary>
    /// Resolves per-member capacity for a sprint by scanning every team's iteration settings,
    /// matching the selected iteration, and merging capacities + team days-off across teams.
    /// Degrades gracefully (Loaded=false + Error) so the caller can fall back to default capacity.
    ///
    /// Teams are independent, so they are read concurrently under the shared TFS gate instead of
    /// one after another — this scan was most of the wait on Load. The reads are merged afterwards
    /// in team order, so the outcome is exactly that of a sequential scan: the first matching team
    /// supplies the sprint dates, and a tie in capacity keeps the earlier team's figure.
    /// </summary>
    public async Task<SprintCapacity> GetSprintCapacityAsync(string project, string iterationPath, CancellationToken ct = default)
    {
        var result = new SprintCapacity();
        try
        {
            var teams = await GetTeamsAsync(project, ct);
            result.TeamsScanned = teams.Count;

            var reads = await Task.WhenAll(teams.Select(team => ReadTeamCapacityAsync(project, team, iterationPath, ct)));
            foreach (var read in reads)
                if (read != null) MergeTeamCapacity(read, result);

            result.Loaded = result.Members.Count > 0 && result.Start.HasValue && result.Finish.HasValue;
            if (!result.Loaded)
                result.Error = result.Members.Count == 0
                    ? "No team capacity is configured for this sprint."
                    : "Sprint start/finish dates were not found.";
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Capacity resolution failed for {Iter}", iterationPath);
            result.Error = ex.Message;
        }
        return result;
    }

    /// <summary>One team's contribution to the sprint's capacity, before merging.</summary>
    private sealed record TeamCapacityRead(IterationMatch Iteration, JsonElement Capacities, List<DateRange> DaysOff);

    /// <summary>
    /// Reads one team's settings for the sprint, or null when the team does not plan that sprint
    /// or its settings cannot be read. A team that fails is skipped rather than failing the load:
    /// the others still describe most of the people in the sprint.
    /// </summary>
    private async Task<TeamCapacityRead?> ReadTeamCapacityAsync(string project, TfsTeam team, string iterationPath, CancellationToken ct)
    {
        try
        {
            var iters = await GetJsonThrottled(ProjectTeamApi(project, team.Id, $"work/teamsettings/iterations?api-version={Ver}"), ct);
            var iteration = FindIteration(iters, iterationPath);
            if (iteration == null) return null;

            var caps = await GetJsonThrottled(ProjectTeamApi(project, team.Id, $"work/teamsettings/iterations/{iteration.Id}/capacities?api-version={Ver}"), ct);
            return new TeamCapacityRead(iteration, caps, await ReadTeamDaysOffAsync(project, team, iteration.Id, ct));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "capacity fetch failed for team {Team}", team.Name);
            return null;
        }
    }

    /// <summary>Team days off are optional in TFS; missing ones leave capacity unaffected.</summary>
    private async Task<List<DateRange>> ReadTeamDaysOffAsync(string project, TfsTeam team, string iterationId, CancellationToken ct)
    {
        try
        {
            var tdo = await GetJsonThrottled(ProjectTeamApi(project, team.Id, $"work/teamsettings/iterations/{iterationId}/teamdaysoff?api-version={Ver}"), ct);
            return ParseDateRanges(tdo, "daysOff");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "team days-off fetch failed for {Team}", team.Name);
            return new List<DateRange>();
        }
    }

    private static void MergeTeamCapacity(TeamCapacityRead read, SprintCapacity result)
    {
        // First matching team with dates wins, exactly as the sequential scan behaved.
        if (result.Start == null)
        {
            if (read.Iteration.Start.HasValue) result.Start = read.Iteration.Start;
            if (read.Iteration.Finish.HasValue) result.Finish = read.Iteration.Finish;
        }
        MergeCapacities(read.Capacities, result);
        result.TeamDaysOff.AddRange(read.DaysOff);
    }

    /// <summary>
    /// Loads all Tasks in an area+iteration with every field (no field filter), so the
    /// instance-specific Revised Estimate, Tags, and Discipline are available.
    /// </summary>
    public async Task<List<WorkItem>> LoadTasksForCapacityAsync(string project, string areaPath, string iterPath, CancellationToken ct = default)
    {
        var wiql = $@"SELECT [System.Id] FROM WorkItems
            WHERE [System.WorkItemType] = 'Task'
            AND [System.AreaPath] UNDER '{WiqlEscape(areaPath)}'
            AND [System.IterationPath] = '{WiqlEscape(iterPath)}'
            ORDER BY [System.Id]";

        var wiqlResult = await PostJson(ProjectApi(project, $"wit/wiql?api-version={Ver}"), new { query = wiql }, ct);
        var ids = wiqlResult.GetProperty("workItems").EnumerateArray().Select(w => w.GetProperty("id").GetInt32()).ToList();
        if (ids.Count == 0) return new List<WorkItem>();

        // Batches are fetched concurrently under the shared gate and flattened in batch order, so
        // the result keeps the WIQL's id order. $expand=relations brings each task's parent link,
        // which deliverable classification needs.
        var pages = await Task.WhenAll(ids.Chunk(WorkItemBatchSize).Select(chunk =>
            GetJsonThrottled(ProjectApi(project, $"wit/workitems?ids={string.Join(",", chunk)}&$expand=relations&api-version={Ver}"), ct)));

        return pages
            .SelectMany(page => page.GetProperty("value").EnumerateArray())
            .Select(wi => ParseCapacityTask(wi, project, areaPath, iterPath))
            .ToList();
    }

    private static WorkItem ParseCapacityTask(JsonElement wi, string project, string areaPath, string iterPath)
    {
        var f = wi.GetProperty("fields");
        var item = new WorkItem
        {
            Id = wi.GetProperty("id").GetInt32().ToString(),
            Title = GetStr(f, "System.Title", ""),
            State = GetStr(f, "System.State", "Active"),
            Assigned = GetAssigned(f),
            Area = GetStr(f, "System.AreaPath", areaPath),
            IterationPath = GetStr(f, "System.IterationPath", iterPath),
            Sprint = GetStr(f, "System.IterationPath", "").Split('\\').LastOrDefault() ?? "",
            Type = GetStr(f, "System.WorkItemType", "Task"),
            Project = project,
            Discipline = GetStr(f, "Microsoft.VSTS.Common.Discipline", ""),
            Tags = GetStr(f, "System.Tags", ""),
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

    /// <summary>
    /// Tags, type and title of the given work items, keyed by id — the parents of capacity tasks.
    /// Tags drive deliverable classification; the type is how a task under a Bug is recognised as
    /// bug work. Ids that are not numeric are dropped, since they end up in the request URL. A parent
    /// TFS cannot return is missing from the map, so its tasks are classified by their own tags alone.
    /// </summary>
    public async Task<Dictionary<string, WorkItem>> GetParentsAsync(string project, IEnumerable<string> ids, CancellationToken ct = default)
    {
        var numeric = ids.Where(i => int.TryParse(i, out _)).Select(int.Parse).Distinct().ToList();
        var map = new Dictionary<string, WorkItem>(StringComparer.Ordinal);
        if (numeric.Count == 0) return map;
        foreach (var w in await FetchFieldsByIds(project, numeric, "System.Tags,System.WorkItemType,System.Title", ct))
            map[w.Id] = w;
        return map;
    }

    /// <summary>A team's own record of the selected sprint: its iteration id and the sprint dates.</summary>
    private sealed record IterationMatch(string Id, DateTime? Start, DateTime? Finish);

    /// <summary>
    /// Finds the selected sprint among one team's iterations. Pure — the sequential version wrote
    /// the dates straight into the shared result, which concurrent team reads cannot do safely.
    /// </summary>
    private static IterationMatch? FindIteration(JsonElement iters, string selectedPath)
    {
        if (!iters.TryGetProperty("value", out var arr) || arr.ValueKind != JsonValueKind.Array) return null;
        foreach (var it in arr.EnumerateArray())
        {
            var path = it.TryGetProperty("path", out var p) ? p.GetString() ?? "" : "";
            var name = it.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
            if (!IterationMatches(selectedPath, path, name)) continue;

            var id = it.TryGetProperty("id", out var idEl) ? idEl.GetString() : null;
            if (string.IsNullOrEmpty(id)) return null;

            // TryGetProperty throws on a non-object, so a null "attributes" is checked first.
            var dated = it.TryGetProperty("attributes", out var attr) && attr.ValueKind == JsonValueKind.Object;
            return new IterationMatch(id,
                dated ? IterationDate(attr, "startDate") : null,
                dated ? IterationDate(attr, "finishDate") : null);
        }
        return null;
    }

    private static DateTime? IterationDate(JsonElement attributes, string prop) =>
        attributes.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String && DateTime.TryParse(v.GetString(), out var d)
            ? d.Date
            : null;

    private static bool IterationMatches(string selectedPath, string teamPath, string teamName)
    {
        static string Norm(string s) => s.Replace('/', '\\').Trim().Trim('\\').ToLowerInvariant();
        var sel = Norm(selectedPath);
        var tp = Norm(teamPath);
        if (sel.Length == 0) return false;
        if (sel == tp) return true;
        var selLeaf = sel.Contains('\\') ? sel[(sel.LastIndexOf('\\') + 1)..] : sel;
        var tpLeaf = tp.Contains('\\') ? tp[(tp.LastIndexOf('\\') + 1)..] : tp;
        if (selLeaf.Length > 0 && selLeaf == tpLeaf) return true;
        if (selLeaf.Length > 0 && selLeaf == teamName.Trim().ToLowerInvariant()) return true;
        return tpLeaf.Length > 0 && (sel.EndsWith("\\" + tpLeaf) || tp.EndsWith("\\" + selLeaf));
    }

    private static void MergeCapacities(JsonElement caps, SprintCapacity result)
    {
        if (!caps.TryGetProperty("value", out var arr) || arr.ValueKind != JsonValueKind.Array) return;
        foreach (var m in arr.EnumerateArray())
        {
            var displayName = m.TryGetProperty("teamMember", out var tm) && tm.TryGetProperty("displayName", out var d)
                ? d.GetString() ?? "" : "";
            if (string.IsNullOrEmpty(displayName)) continue;

            var capPerDay = 0.0;
            if (m.TryGetProperty("activities", out var acts) && acts.ValueKind == JsonValueKind.Array)
                foreach (var a in acts.EnumerateArray())
                    if (a.TryGetProperty("capacityPerDay", out var cpd) && cpd.TryGetDouble(out var cv)) capPerDay += cv;

            var key = PlanningCapacityCalculator.NormalizeMemberName(displayName);
            // Highest non-zero capacity wins across teams; first entry otherwise.
            if (result.Members.TryGetValue(key, out var existing) && existing.CapacityPerDay >= capPerDay) continue;
            result.Members[key] = new MemberCapacity
            {
                DisplayName = displayName,
                NormalizedName = key,
                CapacityPerDay = capPerDay,
                DaysOff = ParseDateRanges(m, "daysOff")
            };
        }
    }

    private static List<DateRange> ParseDateRanges(JsonElement el, string prop)
    {
        var list = new List<DateRange>();
        if (el.TryGetProperty(prop, out var arr) && arr.ValueKind == JsonValueKind.Array)
            foreach (var r in arr.EnumerateArray())
                if (r.TryGetProperty("start", out var s) && r.TryGetProperty("end", out var e)
                    && DateTime.TryParse(s.GetString(), out var sd) && DateTime.TryParse(e.GetString(), out var ed))
                    list.Add(new DateRange { Start = sd.Date, End = ed.Date });
        return list;
    }

}
