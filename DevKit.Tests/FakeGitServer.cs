using System.Collections.Concurrent;
using System.Collections.Specialized;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Web;

namespace DevKit.Tests;

/// <summary>
/// Answers TFS's git endpoints from commits built in memory. Object ids are content hashes, so
/// identical content gets an identical id — the property the comparison relies on.
/// </summary>
internal sealed class FakeGitServer : HttpMessageHandler
{
    private const string ReposMarker = "/_apis/git/repositories/";

    private readonly ConcurrentDictionary<string, byte[]> _blobs = new();
    private readonly ConcurrentDictionary<string, List<Entry>> _trees = new();
    private readonly ConcurrentDictionary<string, CommitData> _commits = new();
    private readonly ConcurrentDictionary<string, string> _branches = new();
    private readonly ConcurrentDictionary<string, long> _reportedSizes = new();
    private int _commitCount;

    public ConcurrentQueue<string> Requests { get; } = new();
    public HashSet<string> FailingDiffTargets { get; } = new();
    public Dictionary<(string Base, string Target), (string From, string To)> Renames { get; } = new();
    public bool OmitOriginalIds { get; set; }

    /// <summary>Makes every diff state more changes than it delivers, as when TFS caps a huge diff.</summary>
    public bool CapDiffs { get; set; }

    /// <summary>Hands back at most this many changes per page, whatever was asked for.</summary>
    public int? PageLimit { get; set; }

    /// <summary>Answers every page as if $skip were 0, like a server that does not page.</summary>
    public bool IgnoreSkip { get; set; }

    /// <summary>When set, diff requests wait for it — or for cancellation.</summary>
    public TaskCompletionSource? Gate { get; set; }

    public TaskCompletionSource DiffRequested { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>
    /// When set, branch reads wait for it whatever happens — like a request already on its way — and
    /// then fail, so a TFS error can land after the run was stopped.
    /// </summary>
    public TaskCompletionSource? RefsGate { get; set; }

    public TaskCompletionSource RefsRequested { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>Trees whose listing comes back without its entries.</summary>
    public HashSet<string> BrokenTrees { get; } = new();

    /// <summary>Submodule pointers a diff reports as changed, per base and target commit.</summary>
    public Dictionary<(string Base, string Target), string[]> Submodules { get; } = new();

    public void Branch(string name, string commit) => _branches[name] = commit;

    public void RemoveBranch(string name) => _branches.TryRemove(name, out _);

    public string TreeOf(string commit) => _commits[commit].Tree;

    /// <summary>Makes the tree listing report a size for this content other than its real one.</summary>
    public void ReportSize(string content, long size) => _reportedSizes[Hash(Encoding.UTF8.GetBytes(content))] = size;

    public string Commit(IReadOnlyDictionary<string, string> files, params string[] parents) =>
        CommitBytes(files.ToDictionary(f => f.Key, f => Encoding.UTF8.GetBytes(f.Value)), parents);

    public string CommitBytes(IReadOnlyDictionary<string, byte[]> files, params string[] parents)
    {
        var blobByPath = new Dictionary<string, string>();
        foreach (var (path, content) in files)
        {
            var id = Hash(content);
            _blobs[id] = content;
            blobByPath[path] = id;
        }

        var tree = BuildTree(blobByPath.Select(f => (f.Key.Split('/', StringSplitOptions.RemoveEmptyEntries), f.Value)).ToList(), 0);
        var sha = Hash(Encoding.UTF8.GetBytes($"commit {Interlocked.Increment(ref _commitCount)} {tree}"));
        _commits[sha] = new CommitData(tree, parents, blobByPath);
        return sha;
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var uri = request.RequestUri ?? throw new InvalidOperationException("A request without a URL.");
        var path = Uri.UnescapeDataString(uri.AbsolutePath);
        Requests.Enqueue(path + uri.Query);
        var query = HttpUtility.ParseQueryString(uri.Query);
        var route = path[(path.IndexOf(ReposMarker, StringComparison.Ordinal) + ReposMarker.Length)..].Split('/', 2)[1];

        if (route == "refs")
        {
            RefsRequested.TrySetResult();
            if (RefsGate is { } refsGate)
            {
                await refsGate.Task;
                return Reply(HttpStatusCode.ServiceUnavailable, "{\"message\":\"TFS is busy.\"}");
            }
            return Json(Refs(query["filter"] ?? ""));
        }
        if (route == "diffs/commits")
        {
            DiffRequested.TrySetResult();
            if (Gate is { } gate)
            {
                await gate.Task.WaitAsync(ct);
            }
            return Diff(query);
        }
        if (route.StartsWith("commits/", StringComparison.Ordinal) && _commits.TryGetValue(route["commits/".Length..], out var commit))
        {
            return Json(new { treeId = commit.Tree, parents = commit.Parents });
        }
        if (route.StartsWith("trees/", StringComparison.Ordinal) && _trees.TryGetValue(route["trees/".Length..], out var tree))
        {
            var treeId = route["trees/".Length..];
            return BrokenTrees.Contains(treeId)
                ? Json(new { objectId = treeId })
                : Json(new
                {
                    treeEntries = tree.Select(e => new { relativePath = e.Name, objectId = e.ObjectId, gitObjectType = e.Type, size = e.Size })
                });
        }
        if (route.StartsWith("blobs/", StringComparison.Ordinal) && _blobs.TryGetValue(route["blobs/".Length..], out var blob))
        {
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(blob) };
        }
        return Reply(HttpStatusCode.NotFound, "{\"message\":\"TF401174: not found\"}");
    }

    private object Refs(string filter) => new
    {
        value = _branches
            .Where(b => ("heads/" + b.Key).StartsWith(filter, StringComparison.Ordinal))
            .Select(b => new { name = "refs/heads/" + b.Key, objectId = b.Value })
    };

    private HttpResponseMessage Diff(NameValueCollection query)
    {
        var (baseSha, targetSha) = (query["baseVersion"]!, query["targetVersion"]!);
        if (FailingDiffTargets.Contains(targetSha))
        {
            return Reply(HttpStatusCode.InternalServerError, "{\"message\":\"The diff could not be computed.\"}");
        }

        var changes = Changes(baseSha, targetSha);
        var top = Math.Min(int.Parse(query["$top"]!), PageLimit ?? int.MaxValue);
        var skip = IgnoreSkip ? 0 : int.Parse(query["$skip"]!);
        // TFS states the total in changeCounts and leaves "allChangesIncluded: false" out of its replies.
        var stated = changes.Count + (CapDiffs ? 5 : 0);
        return Json(new { changeCounts = new Dictionary<string, int> { ["Edit"] = stated }, changes = changes.Skip(skip).Take(top) });
    }

    /// <summary>Folder entries are listed too, as TFS lists them, so the reader has to skip them.</summary>
    private List<object> Changes(string baseSha, string targetSha)
    {
        var before = _commits[baseSha].Files;
        var after = _commits[targetSha].Files;
        Renames.TryGetValue((baseSha, targetSha), out var rename);

        var result = new List<object>();
        foreach (var folder in before.Keys.Concat(after.Keys).Select(Folder).Distinct().Order())
        {
            result.Add(new { item = new { path = folder, isFolder = true, gitObjectType = "tree" }, changeType = "edit" });
        }
        foreach (var path in before.Keys.Union(after.Keys).Order(StringComparer.Ordinal))
        {
            var (had, has) = (before.GetValueOrDefault(path), after.GetValueOrDefault(path));
            if (rename.To == path)
            {
                result.Add(new { item = Item(path, has!, before[rename.From]), changeType = "edit, rename", originalPath = rename.From });
            }
            else if (rename.From == path || had == has)
            {
                continue;
            }
            else if (had is null)
            {
                result.Add(new { item = Item(path, has!, null), changeType = "add" });
            }
            else if (has is null)
            {
                result.Add(new { item = Item(path, had, null), changeType = "delete" });
            }
            else
            {
                result.Add(new { item = Item(path, has, had), changeType = "edit" });
            }
        }
        foreach (var submodule in Submodules.GetValueOrDefault((baseSha, targetSha)) ?? Array.Empty<string>())
        {
            result.Add(new { item = new { path = submodule, objectId = new string('c', 40), gitObjectType = "commit" }, changeType = "edit" });
        }
        return result;
    }

    private object Item(string path, string objectId, string? originalObjectId) =>
        OmitOriginalIds || originalObjectId is null
            ? new { path, objectId, gitObjectType = "blob" }
            : new { path, objectId, originalObjectId, gitObjectType = "blob" };

    private string BuildTree(List<(string[] Segments, string BlobId)> files, int depth)
    {
        var entries = new List<Entry>();
        foreach (var group in files.GroupBy(f => f.Segments[depth]).OrderBy(g => g.Key, StringComparer.Ordinal))
        {
            var leaf = group.FirstOrDefault(f => f.Segments.Length == depth + 1);
            entries.Add(leaf.Segments is not null
                ? new Entry(group.Key, leaf.BlobId, "blob", _reportedSizes.GetValueOrDefault(leaf.BlobId, _blobs[leaf.BlobId].Length))
                : new Entry(group.Key, BuildTree(group.ToList(), depth + 1), "tree", 0));
        }

        var id = Hash(Encoding.UTF8.GetBytes(string.Join("|", entries.Select(e => $"{e.Type}:{e.Name}:{e.ObjectId}"))));
        _trees[id] = entries;
        return id;
    }

    private static string Folder(string path) => path[..path.LastIndexOf('/')] is { Length: > 0 } folder ? folder : "/";

    private static string Hash(byte[] content) => Convert.ToHexString(SHA256.HashData(content))[..40].ToLowerInvariant();

    private static HttpResponseMessage Json(object body) =>
        Reply(HttpStatusCode.OK, JsonSerializer.Serialize(body));

    private static HttpResponseMessage Reply(HttpStatusCode status, string json) =>
        new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    private sealed record Entry(string Name, string ObjectId, string Type, long Size);

    private sealed record CommitData(string Tree, string[] Parents, Dictionary<string, string> Files);
}
