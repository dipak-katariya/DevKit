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
    /// Bugs and Change Requests are always included without needing the Deliverable tag:
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

    /// <summary>
    /// Builds a row for each matched work item, in batches. One TFS cannot return (deleted or moved since the
    /// query ran, or not readable with this token) is left out of its batch (errorPolicy=omit) rather than
    /// failing the whole sheet, and is named on the bundle so the page can say which are missing.
    /// </summary>
    private async Task LoadRequirementsAsync(CodeMergingBundle bundle, string project, List<int> ids, CancellationToken ct)
    {
        var rows = new List<RequirementMergingRow>(ids.Count);
        foreach (var chunk in ids.Chunk(WorkItemBatchSize))
        {
            var url = _req.ProjectApi(project,
                $"wit/workitems?ids={string.Join(",", chunk)}&fields={WorkItemFields}&errorPolicy=omit&api-version={_req.Ver}");
            var data = await _req.GetJsonAsync(url, ct);
            rows.AddRange(TfsJson.ObjectValues(data).Select(wi => ParseRequirement(wi, project)));
        }

        bundle.Requirements = rows;
        bundle.UnreadWorkItemIds = TfsJson.MissingIds(ids, rows.Select(r => r.WorkItemId));
        if (bundle.UnreadWorkItemIds.Count > 0)
        {
            _logger.LogWarning(
                "{Count} of {Matched} work items matched in {Project} could not be read and were left off the sheet (deleted, moved, or not readable with this PAT): {Ids}",
                bundle.UnreadWorkItemIds.Count, ids.Count, project, string.Join(", ", bundle.UnreadWorkItemIds));
        }
    }

    private RequirementMergingRow ParseRequirement(JsonElement wi, string project)
    {
        var id = TfsJson.Int(wi, "id");
        var f = wi.TryGetProperty("fields", out var fields) ? fields : default;
        return new RequirementMergingRow
        {
            WorkItemId = id,
            Title = TfsJson.Str(f, "System.Title", "(no title)"),
            State = TfsJson.Str(f, "System.State"),
            AssignedTo = TfsJson.Person(f, "System.AssignedTo"),
            WorkItemType = TfsJson.Str(f, "System.WorkItemType"),
            WorkItemUrl = $"{_req.Url}/{Uri.EscapeDataString(project)}/_workitems/edit/{id}"
        };
    }

    /// <summary>One warning per work item TFS did not return, for the sheet's list of failed reads.</summary>
    private static IEnumerable<DiscoveryWarning> UnreadWorkItemWarnings(IEnumerable<int> ids) =>
        ids.Select(id => new DiscoveryWarning($"Work item #{id}", UnreadWorkItemReason));

}
