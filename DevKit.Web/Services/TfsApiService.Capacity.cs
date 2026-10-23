using System.Diagnostics;
using System.Text.Json;
using DevKit.Web.Models;

namespace DevKit.Web.Services;

/// <summary>
/// Teams, sprint capacity and the tasks the Capacity Planning tab plans against. Each team is
/// read in parallel and merged in team order, so the numbers do not depend on which reply lands first.
/// </summary>
public partial class TfsApiService
{
    /// <summary>The team field of a team whose work is split by area path, as most are.</summary>
    private const string AreaPathField = "System.AreaPath";

    // ═══ TEAMS + CAPACITY (Sprint Insights) ═══

    /// <summary>The project's teams. Throws when they cannot be read: no teams read is not "no teams".</summary>
    public async Task<List<TfsTeam>> GetTeamsAsync(string project, CancellationToken ct = default)
    {
        var data = await GetJson(Api($"projects/{Uri.EscapeDataString(project)}/teams?api-version={Ver}"), ct);
        return data.GetProperty("value").EnumerateArray()
            .Select(t => new TfsTeam { Id = t.GetProperty("id").GetString()!, Name = t.GetProperty("name").GetString()! })
            .ToList();
    }

    /// <summary>
    /// Resolves per-member capacity for an area's sprint from every team that plans the sprint. Each team keeps
    /// its own record of a member: a member split over two teams, such as 4.5 h a day in one and 4 h in the
    /// other, has both, and each team's days off go with that team's share alone. Teams whose areas take in
    /// <paramref name="areaPath"/> come first, so the capacity matches the tasks being planned (see
    /// <see cref="KeepAreaTeams"/>).
    /// Degrades gracefully (Loaded=false + Error) so the caller can fall back to default capacity;
    /// a team list that cannot be read sets <see cref="SprintCapacity.ReadFailed"/>, and teams that
    /// cannot be read are counted in <see cref="SprintCapacity.TeamsUnread"/>, so neither reads as
    /// capacity that was never configured.
    ///
    /// Teams are independent, so they are read concurrently under the shared TFS gate instead of
    /// one after another; this scan was most of the wait on Load. The reads are merged afterwards
    /// in team order, so the outcome does not depend on which reply lands first: the first matching
    /// team supplies the sprint dates and the member's display name.
    /// </summary>
    public async Task<SprintCapacity> GetSprintCapacityAsync(string project, string areaPath, string iterationPath, CancellationToken ct = default)
    {
        var result = new SprintCapacity();
        var started = Stopwatch.StartNew();
        _logger.LogInformation("Capacity read for {Project} / {Area} / {Iter} started", project, areaPath, iterationPath);
        try
        {
            var teams = await GetTeamsAsync(project, ct);
            result.TeamsScanned = teams.Count;

            var reads = await Task.WhenAll(teams.Select(team => ReadTeamCapacityAsync(project, team, areaPath, iterationPath, ct)));
            result.TeamsUnread = reads.Count(read => read.Unread);
            var sprintTeams = reads.Select(read => read.Capacity).OfType<TeamCapacityRead>().ToList();
            MergeTeamCapacities(sprintTeams, result);

            result.Loaded = result.Members.Count > 0 && result.Start.HasValue && result.Finish.HasValue;
            result.Error = result.Loaded ? null : MissingCapacityReason(result);
            _logger.LogInformation(
                "Capacity read for {Project} / {Area} / {Iter} ended in {ElapsedMs} ms: {SprintTeams} of {Teams} teams plan the sprint, " +
                "{AreaTeams} of them the area; {Members} members; {Unread} teams not fully read",
                project, areaPath, iterationPath, started.ElapsedMilliseconds, sprintTeams.Count, result.TeamsScanned,
                sprintTeams.Count(team => team.PlansArea), result.Members.Count, result.TeamsUnread);
        }
        catch (Exception ex) when (!TfsErrors.IsRequestedCancellation(ex, ct))
        {
            _logger.LogError(ex, "Capacity for {Project} / {Area} / {Iter} could not be read", project, areaPath, iterationPath);
            result.ReadFailed = true;
            result.Error = TfsErrors.Describe(ex);
        }
        return result;
    }

    private static string MissingCapacityReason(SprintCapacity result)
    {
        if (result.Members.Count > 0)
        {
            return "Sprint start/finish dates were not found.";
        }
        return result.TeamsUnread > 0
            ? $"The capacity of {result.TeamsUnread} of {result.TeamsScanned} teams could not be read."
            : "No team capacity is configured for this sprint.";
    }

    /// <summary>
    /// One team's contribution to the sprint's capacity, before merging. <paramref name="DaysOff"/> is null when
    /// they could not be read; <paramref name="PlansArea"/> says whether the team's areas take in the planned area.
    /// </summary>
    private sealed record TeamCapacityRead(
        string Team, IterationMatch Iteration, JsonElement Capacities, List<DateRange>? DaysOff, bool PlansArea);

    /// <summary>
    /// What reading one team gave: its capacity, or none when it does not plan the sprint. <paramref name="Unread"/>
    /// when its settings, or only its days off, could not be read.
    /// </summary>
    private readonly record struct TeamRead(TeamCapacityRead? Capacity, bool Unread);

    /// <summary>
    /// Reads one team's settings for the sprint: its members' capacities, its areas and its days off, side by
    /// side. A team that fails is counted rather than failing the load: the others still describe most of the
    /// people in the sprint.
    /// </summary>
    private async Task<TeamRead> ReadTeamCapacityAsync(string project, TfsTeam team, string areaPath, string iterationPath, CancellationToken ct)
    {
        try
        {
            var iters = await GetJson(ProjectTeamApi(project, team.Id, $"work/teamsettings/iterations?api-version={Ver}"), ct);
            var iteration = FindIteration(iters, iterationPath);
            if (iteration == null)
            {
                return new TeamRead(null, Unread: false);
            }

            // WhenAll first, so a failure in one never leaves another's exception unobserved.
            var caps = GetJson(ProjectTeamApi(project, team.Id, $"work/teamsettings/iterations/{iteration.Id}/capacities?api-version={Ver}"), ct);
            var areas = GetJson(ProjectTeamApi(project, team.Id, $"work/teamsettings/teamfieldvalues?api-version={Ver}"), ct);
            var daysOff = ReadTeamDaysOffAsync(project, team, iteration.Id, ct);
            await Task.WhenAll(caps, areas, daysOff);

            var read = new TeamCapacityRead(team.Name, iteration, await caps, await daysOff, PlansArea(await areas, areaPath));
            return new TeamRead(read, Unread: read.DaysOff is null);
        }
        catch (Exception ex) when (!TfsErrors.IsRequestedCancellation(ex, ct))
        {
            _logger.LogError(ex,
                "Capacity settings of team {Team} for {Project} / {Iter} could not be read; its members keep only their other teams' capacity, else the default",
                team.Name, project, iterationPath);
            return new TeamRead(null, Unread: true);
        }
    }

    /// <summary>
    /// The team's days off in the sprint; TFS answers an empty list for a team that has none. Null when they
    /// could not be read, so the team's capacities still count while its leave is reported as not deducted.
    /// </summary>
    private async Task<List<DateRange>?> ReadTeamDaysOffAsync(string project, TfsTeam team, string iterationId, CancellationToken ct)
    {
        try
        {
            var tdo = await GetJson(ProjectTeamApi(project, team.Id, $"work/teamsettings/iterations/{iterationId}/teamdaysoff?api-version={Ver}"), ct);
            return ParseDateRanges(tdo, "daysOff");
        }
        catch (Exception ex) when (!TfsErrors.IsRequestedCancellation(ex, ct))
        {
            _logger.LogError(ex, "Days off of team {Team} could not be read; its leave is not deducted", team.Name);
            return null;
        }
    }

    /// <summary>
    /// Puts the teams' reads together in team order, then keeps each member to the teams that plan the area,
    /// when any does. When no team plans the area, every team counts.
    /// </summary>
    private static void MergeTeamCapacities(IReadOnlyList<TeamCapacityRead> reads, SprintCapacity result)
    {
        foreach (var read in reads)
        {
            // First matching team with dates wins, exactly as the sequential scan behaved.
            if (result.Start == null)
            {
                result.Start = read.Iteration.Start;
                result.Finish = read.Iteration.Finish ?? result.Finish;
            }
            MergeCapacities(read, result);
        }

        var planning = reads.Where(read => read.PlansArea).ToList();
        var planningTeams = planning.Select(read => read.Team).ToHashSet(StringComparer.Ordinal);
        foreach (var member in result.Members.Values)
        {
            KeepAreaTeams(member, planningTeams);
        }

        result.SharedDaysOff = SharedDaysOff(planning.Count > 0 ? planning : reads, result.Start, result.Finish);
    }

    /// <summary>
    /// Keeps a member to the teams that plan the area when those teams hold their hours, or when they have hours
    /// in no team. A member whose hours are all in other teams keeps every team: they are helping out in this
    /// area, and TFS lists many people in a project-wide team with no hours, which must not cost them their own.
    /// </summary>
    private static void KeepAreaTeams(MemberCapacity member, IReadOnlySet<string> planningTeams)
    {
        var inArea = member.Teams.FindAll(team => planningTeams.Contains(team.Team));
        if (inArea.Count > 0 && (inArea.Sum(team => team.CapacityPerDay) > 0 || member.CapacityPerDay <= 0))
        {
            member.Teams = inArea;
        }
    }

    /// <summary>
    /// The days inside the sprint that all the given teams take off, one entry per day. A team whose days off could
    /// not be read says nothing about which days are shared, so it is left out.
    /// </summary>
    private static List<DateRange> SharedDaysOff(IEnumerable<TeamCapacityRead> teams, DateTime? start, DateTime? finish)
    {
        if (start is not { } from || finish is not { } to)
        {
            return [];
        }

        HashSet<DateTime>? shared = null;
        foreach (var daysOff in teams.Select(team => team.DaysOff).OfType<List<DateRange>>())
        {
            var days = PlanningCapacityCalculator.DaysCovered(daysOff, from, to);
            if (shared == null)
            {
                shared = days;
            }
            else
            {
                shared.IntersectWith(days);
            }
        }
        return shared?.Order().Select(day => new DateRange { Start = day, End = day }).ToList() ?? [];
    }

    /// <summary>
    /// Whether a team plans work in the area: one of its area paths is the area, lies under it, or holds it with
    /// its sub-areas. A team whose work is split by a field other than the area path cannot be placed by area,
    /// so it is taken to plan every area.
    /// </summary>
    private static bool PlansArea(JsonElement teamFieldValues, string areaPath)
    {
        var field = teamFieldValues.TryGetProperty("field", out var f) && f.ValueKind == JsonValueKind.Object
            ? TfsJson.Str(f, "referenceName")
            : "";
        if (!string.Equals(field, AreaPathField, StringComparison.OrdinalIgnoreCase)
            || !teamFieldValues.TryGetProperty("values", out var values) || values.ValueKind != JsonValueKind.Array)
        {
            return true;
        }

        var area = NormalizePath(areaPath);
        return values.EnumerateArray().Where(v => v.ValueKind == JsonValueKind.Object).Any(v =>
        {
            var teamArea = NormalizePath(TfsJson.Str(v, "value"));
            var withSubAreas = v.TryGetProperty("includeChildren", out var children) && children.ValueKind == JsonValueKind.True;
            return teamArea.Length > 0
                && (teamArea == area
                    || teamArea.StartsWith(area + "\\", StringComparison.Ordinal)
                    || (withSubAreas && area.StartsWith(teamArea + "\\", StringComparison.Ordinal)));
        });
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
            GetJson(ProjectApi(project, $"wit/workitems?ids={string.Join(",", chunk)}&$expand=relations&api-version={Ver}"), ct)));

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
    /// Tags, type and title of the given work items (the parents of capacity tasks), keyed by id.
    /// Tags drive deliverable classification; the type is how a task under a Bug is recognised as
    /// bug work. Ids that are not numeric are dropped, since they end up in the request URL. A parent
    /// TFS cannot return is missing from the map, so its tasks are classified by their own tags alone.
    /// </summary>
    public async Task<Dictionary<string, WorkItem>> GetParentsAsync(string project, IEnumerable<string> ids, CancellationToken ct = default)
    {
        var numeric = ids.Where(i => int.TryParse(i, out _)).Select(int.Parse).Distinct().ToList();
        var map = new Dictionary<string, WorkItem>(StringComparer.Ordinal);
        if (numeric.Count == 0) return map;
        foreach (var w in (await FetchFieldsByIds(project, numeric, "System.Tags,System.WorkItemType,System.Title", ct)).Items)
        {
            map[w.Id] = w;
        }
        return map;
    }

    /// <summary>A team's own record of the selected sprint: its iteration id and the sprint dates.</summary>
    private sealed record IterationMatch(string Id, DateTime? Start, DateTime? Finish);

    /// <summary>
    /// Finds the selected sprint among one team's iterations. Pure: the sequential version wrote
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
                dated ? TfsJson.Day(attr, "startDate") : null,
                dated ? TfsJson.Day(attr, "finishDate") : null);
        }
        return null;
    }

    /// <summary>A classification path compared without case, edge backslashes or forward slashes.</summary>
    private static string NormalizePath(string path) => path.Replace('/', '\\').Trim().Trim('\\').ToLowerInvariant();

    private static bool IterationMatches(string selectedPath, string teamPath, string teamName)
    {
        var sel = NormalizePath(selectedPath);
        var tp = NormalizePath(teamPath);
        if (sel.Length == 0) return false;
        if (sel == tp) return true;
        var selLeaf = sel.Contains('\\') ? sel[(sel.LastIndexOf('\\') + 1)..] : sel;
        var tpLeaf = tp.Contains('\\') ? tp[(tp.LastIndexOf('\\') + 1)..] : tp;
        if (selLeaf.Length > 0 && selLeaf == tpLeaf) return true;
        if (selLeaf.Length > 0 && selLeaf == teamName.Trim().ToLowerInvariant()) return true;
        return tpLeaf.Length > 0 && (sel.EndsWith("\\" + tpLeaf) || tp.EndsWith("\\" + selLeaf));
    }

    /// <summary>
    /// Adds one team's record of each of its members: their hours a day in this team, and their own days off
    /// with the team's. A member in several teams gets one record from each, so no team's share is dropped.
    /// </summary>
    private static void MergeCapacities(TeamCapacityRead read, SprintCapacity result)
    {
        foreach (var m in TfsJson.ObjectValues(read.Capacities))
        {
            var displayName = TfsJson.Person(m, "teamMember", "");
            if (string.IsNullOrEmpty(displayName))
            {
                continue;
            }

            var key = PlanningCapacityCalculator.NormalizeMemberName(displayName);
            if (!result.Members.TryGetValue(key, out var member))
            {
                result.Members[key] = member = new MemberCapacity { DisplayName = displayName };
            }
            member.Teams.Add(new TeamMemberCapacity
            {
                Team = read.Team,
                CapacityPerDay = CapacityPerDay(m),
                DaysOff = [.. ParseDateRanges(m, "daysOff"), .. read.DaysOff ?? []]
            });
        }
    }

    /// <summary>A member's hours a day in one team: the sum over their activities, as the team's capacity page shows.</summary>
    private static double CapacityPerDay(JsonElement member) =>
        member.TryGetProperty("activities", out var activities) && activities.ValueKind == JsonValueKind.Array
            ? activities.EnumerateArray().Sum(a =>
                a.ValueKind == JsonValueKind.Object && a.TryGetProperty("capacityPerDay", out var perDay)
                && perDay.ValueKind == JsonValueKind.Number ? perDay.GetDouble() : 0)
            : 0;

    private static List<DateRange> ParseDateRanges(JsonElement el, string prop)
    {
        var list = new List<DateRange>();
        if (el.TryGetProperty(prop, out var arr) && arr.ValueKind == JsonValueKind.Array)
        {
            foreach (var r in arr.EnumerateArray())
            {
                if (TfsJson.Day(r, "start") is { } start && TfsJson.Day(r, "end") is { } end)
                {
                    list.Add(new DateRange { Start = start, End = end });
                }
            }
        }
        return list;
    }

}
