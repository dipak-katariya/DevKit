using System.Collections.Concurrent;
using System.Net;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Web;
using DevKit.Web.Components.Pages;
using DevKit.Web.Models;
using DevKit.Web.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DevKit.Tests;

/// <summary>
/// Pins the organisation-specific rules still written into the code (ARC-23) before they move into
/// settings: the built-in area names hidden from every area picker, the custom work-item fields read for
/// Revised Estimate and Task Execution Type, and the one repository that defaults to its own base branch.
/// The hidden area names, custom field names and that repository name are the real ones only because they
/// are what is being pinned; everything else is made up.
/// </summary>
[Collection(SettingsFileCollection.Name)]
public sealed class OrganizationBehaviourTests : IDisposable
{
    private const string CollectionUrl = "https://tfs.example.test/tfs/DefaultCollection";
    private const string Project = "Contoso";
    private const string AreaPath = @"Contoso\TEAMX";
    private const string SprintPath = @"Contoso\Sprint 7";
    private const int TaskId = 4100;
    private const string RepoId = "c0ffee00-0000-0000-0000-000000000001";

    private const string StandardRevisedEstimate = "Microsoft.VSTS.Scheduling.RevisedEstimate";
    private const string OrganisationRevisedEstimate = "Casepoint.TFS.CustomFields.RevisedEstimate";
    private const string InheritedRevisedEstimate = "Custom.RevisedEstimate";
    private const string OriginalEstimate = "Microsoft.VSTS.Scheduling.OriginalEstimate";
    private const string OrganisationExecutionType = "Casepoint.TFS.CustomFields.TaskExecutionType";

    /// <summary>Both a built-in hidden area name and the repository with its own default base branch.</summary>
    private const string OrganisationName = "CasepointARA";
    private const string OrganisationBaseBranch = "dev-qa-net8";

    private readonly FakeTfs _tfs = new();
    private readonly HttpClient _http;
    private readonly SettingsService _settings;
    private readonly TfsApiService _api;

    public OrganizationBehaviourTests()
    {
        _http = new HttpClient(_tfs, disposeHandler: false);
        _settings = new SettingsService(NullLogger<SettingsService>.Instance);
        _settings.Tfs.Url = CollectionUrl;
        _api = new TfsApiService(_http, _settings, NullLogger<TfsApiService>.Instance);
    }

    public enum WorkItemLoader
    {
        Audit,
        Capacity
    }

    public static TheoryData<WorkItemLoader, string?, string?, string?, double?> RevisedEstimateSources
    {
        get
        {
            var data = new TheoryData<WorkItemLoader, string?, string?, string?, double?>();
            foreach (var loader in Enum.GetValues<WorkItemLoader>())
            {
                data.Add(loader, "8", "5", "3", 8);
                data.Add(loader, null, "5", "3", 5);
                data.Add(loader, null, null, "3", 3);
                data.Add(loader, null, null, null, null);
                data.Add(loader, "\"n/a\"", "5", "3", 5);
                data.Add(loader, "0", "5", "3", 0);
            }
            return data;
        }
    }

    public void Dispose()
    {
        _http.Dispose();
        _tfs.Dispose();
    }

    [Fact]
    public async Task Areas_are_read_ten_levels_deep_from_the_project_classification_tree()
    {
        _tfs.AreaTree = AreasReply(new AreaNode(Project, new AreaNode("TEAMX")));

        await _api.GetAreasAsync(Project);

        var request = Assert.Single(_tfs.Requests);
        Assert.Equal(HttpMethod.Get, request.Method);
        Assert.StartsWith(
            "/tfs/DefaultCollection/Contoso/_apis/wit/classificationnodes/areas?$depth=10&",
            request.PathAndQuery,
            StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(OrganisationName)]
    [InlineData("508 Compliance Team")]
    [InlineData("Core Framework Web")]
    [InlineData("DA Tools Utilities")]
    [InlineData("DataSite Team")]
    [InlineData("Mobile App")]
    [InlineData("Review Utilities")]
    [InlineData("Document Export")]
    [InlineData("AI")]
    [InlineData("ARA core")]
    public async Task Each_built_in_name_hides_the_area_that_carries_it(string hiddenName)
    {
        _tfs.AreaTree = AreasReply(new AreaNode(Project, new AreaNode(hiddenName), new AreaNode("TEAMX")));

        var areas = await _api.GetAreasAsync(Project);

        Assert.Equal(new[] { Project, "TEAMX" }, areas.Select(a => a.Name));
    }

    [Fact]
    public async Task Built_in_names_are_hidden_by_leaf_name_at_any_depth_while_their_children_stay()
    {
        // Pins B077: matching is by leaf name at any depth, and children of an excluded node stay.
        _tfs.AreaTree = AreasReply(new AreaNode(Project,
            new AreaNode("TEAMX",
                new AreaNode("AI", new AreaNode("Model Tuning")),
                new AreaNode("Contoso.Web")),
            new AreaNode("Mobile App", new AreaNode("iOS")),
            new AreaNode("TEAMY",
                new AreaNode("Contoso.Api", new AreaNode("Review Utilities")))));

        var areas = await _api.GetAreasAsync(Project);

        Assert.Equal(
            new[]
            {
                @"\Contoso\Area",
                @"\Contoso\Area\TEAMX",
                @"\Contoso\Area\TEAMX\AI\Model Tuning",
                @"\Contoso\Area\TEAMX\Contoso.Web",
                @"\Contoso\Area\Mobile App\iOS",
                @"\Contoso\Area\TEAMY",
                @"\Contoso\Area\TEAMY\Contoso.Api"
            },
            areas.Select(a => a.Path));
    }

    [Fact]
    public async Task Hiding_ignores_case_but_needs_the_whole_name()
    {
        _tfs.AreaTree = AreasReply(new AreaNode(Project,
            new AreaNode("document export"),
            new AreaNode("ai"),
            new AreaNode("AI Tools"),
            new AreaNode("ARA"),
            new AreaNode("Mobile App Next")));

        var areas = await _api.GetAreasAsync(Project);

        Assert.Equal(new[] { Project, "AI Tools", "ARA", "Mobile App Next" }, areas.Select(a => a.Name));
    }

    [Fact]
    public async Task A_project_whose_root_carries_a_built_in_name_loses_its_root_area_but_keeps_the_rest()
    {
        _tfs.AreaTree = AreasReply(new AreaNode(OrganisationName, new AreaNode("TEAMX")));

        var areas = await _api.GetAreasAsync(OrganisationName);

        var area = Assert.Single(areas);
        Assert.Equal(@"\CasepointARA\Area\TEAMX", area.Path);
        Assert.Equal(OrganisationName, area.Project);
    }

    [Theory]
    [MemberData(nameof(RevisedEstimateSources))]
    public async Task Revised_estimate_comes_from_the_standard_field_then_the_organisation_field_then_the_inherited_field(
        WorkItemLoader loader, string? standard, string? organisation, string? inherited, double? expected)
    {
        var task = await LoadSingleTaskAsync(loader,
            (StandardRevisedEstimate, standard),
            (OrganisationRevisedEstimate, organisation),
            (InheritedRevisedEstimate, inherited));

        Assert.Equal(expected, task.RevisedEstimateField);
    }

    [Theory]
    [InlineData(WorkItemLoader.Audit)]
    [InlineData(WorkItemLoader.Capacity)]
    public async Task A_zero_standard_revised_estimate_hides_the_organisation_field_so_planning_uses_the_original_estimate(
        WorkItemLoader loader)
    {
        var task = await LoadSingleTaskAsync(loader,
            (StandardRevisedEstimate, "0"),
            (OrganisationRevisedEstimate, "5"),
            (OriginalEstimate, "6"));

        Assert.Equal(0, task.RevisedEstimateField);
        Assert.Equal(6, task.EffectiveEstimate);
    }

    [Theory]
    [InlineData(OrganisationExecutionType, "\"Manual\"", "Manual")]
    [InlineData(OrganisationExecutionType, "1", "")]
    [InlineData("Custom.TaskExecutionType", "\"Manual\"", "")]
    public async Task The_audit_reads_task_execution_type_only_as_text_from_the_organisation_field(
        string field, string jsonValue, string expected)
    {
        var task = await LoadSingleTaskAsync(WorkItemLoader.Audit, (field, jsonValue));

        Assert.Equal(expected, task.TaskExecutionType);
    }

    [Fact]
    public async Task Capacity_planning_does_not_read_task_execution_type()
    {
        var task = await LoadSingleTaskAsync(WorkItemLoader.Capacity, (OrganisationExecutionType, "\"Manual\""));

        Assert.Equal("", task.TaskExecutionType);
    }

    [Theory]
    [InlineData("Contoso.Web")]
    [InlineData(OrganisationName)]
    public void The_settings_qa_branch_wins_when_the_repository_has_it(string repoName)
    {
        ConfigureQaBranch("release/qa");

        var picked = PickDefaultBaseBranch(repoName, "main", "develop", OrganisationBaseBranch, "release/qa");

        Assert.Equal("release/qa", picked);
    }

    [Theory]
    [InlineData("Contoso.Web", "main")]
    [InlineData(OrganisationName, OrganisationBaseBranch)]
    public void A_settings_qa_branch_the_repository_lacks_falls_through_to_the_defaults(string repoName, string expected)
    {
        ConfigureQaBranch("release/qa");

        var picked = PickDefaultBaseBranch(repoName, "main", OrganisationBaseBranch);

        Assert.Equal(expected, picked);
    }

    [Fact]
    public void The_organisation_repository_falls_back_to_its_own_base_branch_before_main()
    {
        var picked = PickDefaultBaseBranch(OrganisationName, "main", "develop", OrganisationBaseBranch);

        Assert.Equal(OrganisationBaseBranch, picked);
    }

    [Fact]
    public void The_organisation_repository_is_recognised_in_any_case_and_its_branch_keeps_the_listed_spelling()
    {
        var picked = PickDefaultBaseBranch(OrganisationName.ToLowerInvariant(), "main", "Dev-QA-Net8");

        Assert.Equal("Dev-QA-Net8", picked);
    }

    [Fact]
    public void The_organisation_repository_without_its_own_base_branch_falls_back_to_main()
    {
        var picked = PickDefaultBaseBranch(OrganisationName, "develop", "main");

        Assert.Equal("main", picked);
    }

    [Fact]
    public void Other_repositories_ignore_the_organisation_base_branch()
    {
        var picked = PickDefaultBaseBranch("Contoso.Api", OrganisationBaseBranch, "main");

        Assert.Equal("main", picked);
    }

    [Theory]
    [InlineData("main", "master", "develop", "main")]
    [InlineData("develop", "master", "develop")]
    [InlineData("master", "release/1", "master")]
    [InlineData("feature/x", "feature/x", "release/1")]
    public void Other_repositories_fall_back_to_main_then_develop_then_master_then_the_first_listed_branch(
        string expected, params string[] branches)
    {
        var picked = PickDefaultBaseBranch("Contoso.Web", branches);

        Assert.Equal(expected, picked);
    }

    [Theory]
    [InlineData("Contoso.Web", "release/qa", "release/qa")]
    [InlineData(OrganisationName, "", OrganisationBaseBranch)]
    [InlineData("Contoso.Web", "", "main")]
    public void With_no_branch_list_the_first_candidate_is_used_unchecked(string repoName, string qaBranch, string expected)
    {
        ConfigureQaBranch(qaBranch);

        var picked = PickDefaultBaseBranch(repoName);

        Assert.Equal(expected, picked);
    }

    private async Task<WorkItem> LoadSingleTaskAsync(WorkItemLoader loader, params (string Name, string? JsonValue)[] fields)
    {
        _tfs.WorkItemFields[TaskId] = TaskFieldsJson(fields);

        var tasks = loader == WorkItemLoader.Audit
            ? (await _api.LoadAuditDataAsync(Project, AreaPath, SprintPath)).Tasks
            : await _api.LoadTasksForCapacityAsync(Project, AreaPath, SprintPath);

        return Assert.Single(tasks);
    }

    private static string TaskFieldsJson(IEnumerable<(string Name, string? JsonValue)> extra)
    {
        var fields = new JsonObject
        {
            ["System.WorkItemType"] = "Task",
            ["System.Title"] = "Export the report",
            ["System.State"] = "Active",
            ["System.AreaPath"] = AreaPath,
            ["System.IterationPath"] = SprintPath,
            ["System.AssignedTo"] = new JsonObject { ["displayName"] = "Dev One" }
        };
        foreach (var (name, jsonValue) in extra)
        {
            if (jsonValue is not null)
            {
                fields[name] = JsonNode.Parse(jsonValue);
            }
        }
        return fields.ToJsonString();
    }

    /// <summary>Stores the QA branch the way the page reads it back: by repository id.</summary>
    private void ConfigureQaBranch(string qaBranch)
    {
        if (qaBranch.Length > 0)
        {
            _settings.RepoBranches[RepoId] = new RepoBranchDefaults { QaBranch = qaBranch };
        }
    }

    /// <summary>Calls the page's private picker with the arguments the page passes it after loading branches.</summary>
    private string PickDefaultBaseBranch(string repoName, params string[] branches)
    {
        var method = typeof(BranchCreator).GetMethod("PickDefaultBaseBranch", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);

        var repo = new TfsRepo { Id = RepoId, Name = repoName, Project = Project };
        var preferred = _settings.GetRepoBranches(repo.Id).QaBranch;
        return Assert.IsType<string>(method.Invoke(null, new object?[] { repo, branches, preferred }));
    }

    /// <summary>A classification-nodes reply shaped as TFS sends it: the root's path ends in "\Area".</summary>
    private static string AreasReply(AreaNode root)
    {
        var nextId = 1;
        return JsonSerializer.Serialize(ToJson(root, $@"\{root.Name}\Area"));

        Dictionary<string, object> ToJson(AreaNode node, string path)
        {
            var json = new Dictionary<string, object>
            {
                ["id"] = nextId++,
                ["name"] = node.Name,
                ["structureType"] = "area",
                ["hasChildren"] = node.Children.Length > 0,
                ["path"] = path
            };
            if (node.Children.Length > 0)
            {
                json["children"] = node.Children.Select(child => ToJson(child, $@"{path}\{child.Name}")).ToList();
            }
            return json;
        }
    }

    private sealed record AreaNode(string Name, params AreaNode[] Children);

    private sealed record RecordedRequest(HttpMethod Method, string PathAndQuery, string? ContentType, string Body);

    /// <summary>Answers the area tree, WIQL and work-item reads these paths make, and records every request.</summary>
    private sealed class FakeTfs : HttpMessageHandler
    {
        private readonly ConcurrentQueue<RecordedRequest> _requests = new();

        public string AreaTree { get; set; } = "{}";

        public ConcurrentDictionary<int, string> WorkItemFields { get; } = new();

        public IReadOnlyCollection<RecordedRequest> Requests => _requests;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var uri = request.RequestUri ?? throw new InvalidOperationException("A request without a URL.");
            var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);
            _requests.Enqueue(new RecordedRequest(request.Method, uri.PathAndQuery, request.Content?.Headers.ContentType?.MediaType, body));

            var path = uri.AbsolutePath;
            if (request.Method == HttpMethod.Get && path.EndsWith("/wit/classificationnodes/areas", StringComparison.Ordinal))
            {
                return Reply(HttpStatusCode.OK, AreaTree);
            }
            if (request.Method == HttpMethod.Post && path.EndsWith("/wit/wiql", StringComparison.Ordinal))
            {
                return Reply(HttpStatusCode.OK, WiqlReply(body));
            }
            if (request.Method == HttpMethod.Get && path.EndsWith("/wit/workitems", StringComparison.Ordinal))
            {
                return Reply(HttpStatusCode.OK, WorkItemsReply(uri));
            }
            return Reply(HttpStatusCode.NotFound, """{"message":"The fake has no answer for this request."}""");
        }

        // Only Task queries match, so the audit's Bug query comes back empty and the one task is all it reads.
        private string WiqlReply(string body)
        {
            using var doc = JsonDocument.Parse(body);
            var query = doc.RootElement.GetProperty("query").GetString() ?? "";
            var ids = query.Contains("[System.WorkItemType] = 'Task'", StringComparison.Ordinal)
                ? WorkItemFields.Keys.Order().ToList()
                : new List<int>();
            return JsonSerializer.Serialize(new { workItems = ids.Select(id => new { id }) });
        }

        private string WorkItemsReply(Uri uri)
        {
            var ids = (HttpUtility.ParseQueryString(uri.Query)["ids"] ?? "")
                .Split(',', StringSplitOptions.RemoveEmptyEntries)
                .Select(int.Parse)
                .ToList();
            var items = ids.Select(id => $$"""{"id":{{id}},"fields":{{WorkItemFields[id]}}}""");
            return $$"""{"count":{{ids.Count}},"value":[{{string.Join(",", items)}}]}""";
        }

        private static HttpResponseMessage Reply(HttpStatusCode status, string json) =>
            new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
    }
}
