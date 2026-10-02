using System.Text.Json;
using DevKit.Web.Models;

namespace DevKit.Web.Services;

/// <summary>The WIQL that selects a sprint's requirements, and the rows built from them.</summary>
public partial class CodeMergingService
{
    // ═══ WORK ITEMS ═══

    private async Task<List<int>> FetchWorkItemIdsAsync(CodeMergingQuery query, CancellationToken ct)
    {
        var data = await _req.PostJsonAsync(
            _req.ProjectApi(query.Project, $"wit/wiql?api-version={_req.Ver}"),
            new { query = BuildWiql(query) }, ct);

        return data.TryGetProperty("workItems", out var arr) && arr.ValueKind == JsonValueKind.Array
            ? arr.EnumerateArray().Select(w => TfsJson.Int(w, "id")).Where(id => id > 0).ToList()
            : new List<int>();
    }

    /// <summary>
    /// Bugs and Change Requests are always included without needing the Deliverable tag —
    /// nobody tags a production bug as a deliverable, but its fix still has to reach the
    /// release branch. Paths are escaped because they end up inside WIQL string literals.
    /// </summary>
    private static string BuildWiql(CodeMergingQuery query)
    {
        var tagged = Quote(TaggedWorkItemTypes);
        var always = Quote(AlwaysIncludedTypes);

        var typeClause = query.DeliverableOnly
            ? $"(([System.WorkItemType] IN ({tagged}) AND [System.Tags] CONTAINS '{DeliverableTag}') " +
              $"OR [System.WorkItemType] IN ({always}))"
            : $"[System.WorkItemType] IN ({tagged},{always})";

        var areaClause = string.IsNullOrWhiteSpace(query.AreaPath)
            ? ""
            : $" AND [System.AreaPath] UNDER '{TfsRequestClient.WiqlEscape(query.AreaPath)}'";

        return "SELECT [System.Id] FROM WorkItems WHERE " + typeClause +
               " AND " + TfsRequestClient.WiqlIterationClause(query.IterationPaths) +
               areaClause +
               " ORDER BY [System.AssignedTo] ASC";
    }

    private static string Quote(IEnumerable<string> values) => string.Join(",", values.Select(v => "'" + v + "'"));

    private async Task<List<RequirementMergingRow>> FetchRequirementsAsync(string project, List<int> ids, CancellationToken ct)
    {
        var rows = new List<RequirementMergingRow>(ids.Count);
        foreach (var chunk in ids.Chunk(WorkItemBatchSize))
        {
            var url = _req.ProjectApi(project,
                $"wit/workitems?ids={string.Join(",", chunk)}&fields={WorkItemFields}&api-version={_req.Ver}");
            var data = await TfsThrottle.RunAsync(() => _req.GetJsonAsync(url, ct), ct);

            foreach (var wi in TfsJson.Values(data))
            {
                var id = TfsJson.Int(wi, "id");
                var f = wi.TryGetProperty("fields", out var fields) ? fields : default;
                rows.Add(new RequirementMergingRow
                {
                    WorkItemId = id,
                    Title = TfsJson.Str(f, "System.Title", "(no title)"),
                    State = TfsJson.Str(f, "System.State"),
                    AssignedTo = TfsJson.Person(f, "System.AssignedTo"),
                    WorkItemType = TfsJson.Str(f, "System.WorkItemType"),
                    WorkItemUrl = $"{_req.Url}/{Uri.EscapeDataString(project)}/_workitems/edit/{id}"
                });
            }
        }
        return rows;
    }

}
