using System.Text.Json;
using DevKit.Web.Models;

namespace DevKit.Web.Services;

/// <summary>
/// The TFS / Azure DevOps REST client. Split across partials by the area of the API it talks to
/// (see TfsApiService.Capacity, .Audit, .WorkItems and .Git), all sharing the request helpers here.
/// </summary>
public partial class TfsApiService : ITfsMetadataSource
{
    private const int MaxWorkItems = 200;
    private const int WorkItemBatchSize = 50;

    /// <summary>Active pull requests asked for per request when deciding which branches Branch Cleanup must keep.</summary>
    private const int ActivePullRequestPageSize = 1000;

    /// <summary>Pages of active pull requests read per repository before the answer is reported as cut short.</summary>
    private const int MaxActivePullRequestPages = 20;

    /// <summary>Most active pull requests read per repository; a repository with more has every branch kept.</summary>
    public const int MaxActivePullRequests = ActivePullRequestPageSize * MaxActivePullRequestPages;

    private const string EmptyObjectId = "0000000000000000000000000000000000000000";

    private readonly TfsRequestClient _req;
    private readonly ILogger<TfsApiService> _logger;

    private static readonly string[] ExcludedAreas = {
        "CasepointARA", "508 Compliance Team", "Core Framework Web", "DA Tools Utilities",
        "DataSite Team", "Mobile App", "Review Utilities", "Document Export", "AI", "ARA core"
    };

    public TfsApiService(TfsRequestClient req, ILogger<TfsApiService> logger)
    {
        _req = req;
        _logger = logger;
    }

    /// <summary>Over a client of the caller's own, as tests use it; DI only sees the public constructor.</summary>
    internal TfsApiService(HttpClient http, SettingsService settings, ILogger<TfsApiService> logger)
        : this(new TfsRequestClient(http, settings), logger)
    {
    }

    // Auth, cache-bypass, TFS error detail and the URL builders live in TfsRequestClient so
    // every TFS-facing service shares one implementation.
    private string Url => _req.Url;
    private string Ver => _req.Ver;

    private string Api(string path) => _req.Api(path);
    private string ProjectApi(string project, string path) => _req.ProjectApi(project, path);
    private string ProjectTeamApi(string project, string team, string path) => _req.ProjectTeamApi(project, team, path);

    private static string WiqlEscape(string value) => TfsRequestClient.WiqlEscape(value);

    private Task<HttpResponseMessage> SendAuthed(HttpRequestMessage req, CancellationToken ct = default) =>
        _req.SendAsync(req, ct);
    private Task<JsonElement> GetJson(string url, CancellationToken ct = default) => _req.GetJsonAsync(url, ct);
    private Task<JsonElement> PostJson(string url, object body, CancellationToken ct = default) => _req.PostJsonAsync(url, body, ct);

    // ═══ PROJECTS ═══

    /// <summary>
    /// Checks a server URL and sign-in before anything is saved, and returns how many projects that sign-in can
    /// see and the account TFS ran it as. Throws when TFS refuses the sign-in or cannot be reached;
    /// <see cref="TfsErrors.Describe"/> words it.
    /// </summary>
    public Task<TfsConnectionCheck> TestConnectionAsync(
        string url, TfsAuthMode auth, string pat, CancellationToken ct = default) =>
        _req.CheckConnectionAsync(url, auth, pat, ct);

    public async Task<List<TfsProject>> GetProjectsAsync(CancellationToken ct = default)
    {
        var data = await GetJson(Api($"projects?api-version={Ver}"), ct);
        return data.GetProperty("value").EnumerateArray()
            .Select(p => new TfsProject { Id = p.GetProperty("id").GetString()!, Name = p.GetProperty("name").GetString()! })
            .OrderBy(p => p.Name).ToList();
    }

    // ═══ REPOSITORIES ═══
    // The metadata reads throw on failure: an empty list would read as a server with nothing on it.
    // SharedTfsData logs and describes the failure.
    public async Task<List<TfsRepo>> GetRepositoriesAsync(CancellationToken ct = default)
    {
        var data = await GetJson(Api($"git/repositories?api-version={Ver}"), ct);
        return data.GetProperty("value").EnumerateArray()
            .Where(r => r.TryGetProperty("id", out _) && r.TryGetProperty("name", out _))
            .Select(r => new TfsRepo
            {
                Id = r.GetProperty("id").GetString()!,
                Name = r.GetProperty("name").GetString()!,
                Project = r.TryGetProperty("project", out var proj) && proj.TryGetProperty("name", out var pn) ? pn.GetString()! : "",
                DefaultBranch = GitRefName.Strip(TfsJson.Str(r, "defaultBranch"))
            })
            .Where(r => !string.IsNullOrEmpty(r.Project))
            .OrderBy(r => $"{r.Project}/{r.Name}").ToList();
    }

    // ═══ AREAS ═══
    public async Task<List<TfsArea>> GetAreasAsync(string project, CancellationToken ct = default)
    {
        var data = await GetJson(ProjectApi(project, $"wit/classificationnodes/areas?$depth=10&api-version={Ver}"), ct);
        var nodes = FlattenNodes(data);
        return nodes
            .Where(n => !ExcludedAreas.Any(ex => string.Equals(n.Name, ex, StringComparison.OrdinalIgnoreCase)))
            .Select(n => new TfsArea { Id = n.Id, Name = n.Name, Path = n.Path, Project = project })
            .ToList();
    }

    // ═══ ITERATIONS ═══
    public async Task<List<TfsIteration>> GetIterationsAsync(string project, CancellationToken ct = default)
    {
        var data = await GetJson(ProjectApi(project, $"wit/classificationnodes/iterations?$depth=5&api-version={Ver}"), ct);
        return FlattenIterations(data, project);
    }

    // ═══ URL BUILDERS ═══
    public string WorkItemUrl(string project, string id) =>
        $"{Url}/{Uri.EscapeDataString(project)}/_workitems/edit/{id}";

    public string CommitUrl(string project, string repoName, string sha) =>
        $"{Url}/{Uri.EscapeDataString(project)}/_git/{Uri.EscapeDataString(repoName)}/commit/{sha}";

    /// <summary>The branch's page in the TFS web UI ("GB" is the git-branch version selector).</summary>
    public string BranchUrl(TfsRepo repo, string branch) => _req.BranchWebUrl(repo.Project, repo.Name, branch);

    // ═══ HELPERS ═══
    private static int ParseIdOrZero(string id) => int.TryParse(id, out var n) ? n : 0;

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

    /// <summary>
    /// Iterations keep their sprint dates, which the generic node flattener drops. They come
    /// from the same response, so this costs no extra request.
    /// </summary>
    private static List<TfsIteration> FlattenIterations(JsonElement root, string project)
    {
        var result = new List<TfsIteration>();
        Walk(root);
        return result;

        void Walk(JsonElement node)
        {
            if (node.TryGetProperty("name", out var name))
            {
                var id = node.TryGetProperty("id", out var idEl) ? idEl.ToString() : name.GetString()!;
                var path = node.TryGetProperty("path", out var pathEl) ? pathEl.GetString()! : name.GetString()!;

                // TryGetProperty throws on a default JsonElement, so an iteration without an
                // attributes object has to be handled before the date lookups, not inside them.
                var dated = node.TryGetProperty("attributes", out var attrs)
                            && attrs.ValueKind == JsonValueKind.Object;

                result.Add(new TfsIteration
                {
                    Id = id,
                    Name = name.GetString()!,
                    Path = path,
                    Project = project,
                    StartDate = dated ? TfsJson.Date(attrs, "startDate") : null,
                    FinishDate = dated ? TfsJson.Date(attrs, "finishDate") : null
                });
            }

            if (!node.TryGetProperty("children", out var children)) return;
            foreach (var child in children.EnumerateArray()) Walk(child);
        }
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
