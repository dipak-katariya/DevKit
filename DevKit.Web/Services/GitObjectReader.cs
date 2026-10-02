using System.Text.Json;
using DevKit.Web.Models;

namespace DevKit.Web.Services;

/// <summary>Where a file's content lives in git, and its size when the tree listing says.</summary>
public readonly record struct BlobRef(string ObjectId, long? Size);

/// <summary>One file a pull request changed, as the commit diff reports it.</summary>
/// <param name="BaseBlobId">Object id before the change, when the diff names it.</param>
/// <param name="AfterBlobId">Object id after the change, when the diff names it.</param>
/// <param name="Unsupported">Why this entry can't be compared (a submodule pointer); null for an ordinary file.</param>
public sealed record FileChange(
    string Path, string? OriginalPath, FileChangeKind Kind, string? BaseBlobId, string? AfterBlobId, string? Unsupported = null);

/// <summary>The files a pull request changed.</summary>
/// <param name="Truncated">There were more than the limit asked for.</param>
/// <param name="Incomplete">TFS stopped listing before the end, so some changed files are unknown.</param>
public sealed record ChangeList(IReadOnlyList<FileChange> Files, bool Truncated, bool Incomplete = false);

/// <summary>
/// Git objects read from TFS for one comparison run. Commits, trees and blobs never change, so each is
/// fetched once per run and shared: every pull request in a sprint walks the same destination tree, and
/// a file two pull requests touched is downloaded once. A file's object id comes from walking the
/// trees, so "not there" is an answer rather than a 404. Every request goes through
/// <see cref="TfsThrottle"/>.
/// </summary>
public sealed class GitObjectReader
{
    private const int DiffPageSize = 500;

    /// <summary>Stops a runaway paging loop on a pathological diff: 200 pages is 100,000 changes.</summary>
    private const int MaxDiffPages = 200;

    private const long CommitCacheBytes = 4L * 1024 * 1024;
    private const long TreeCacheBytes = 64L * 1024 * 1024;

    /// <summary>Rough in-memory cost of a cached commit and of one tree entry, for the cache budgets.</summary>
    private const int CommitBytes = 256;
    private const int TreeEntryBytes = 160;

    // VersionControlChangeType flag values, for servers that send the change type as a number.
    private const int AddFlag = 1;
    private const int RenameFlag = 8;
    private const int DeleteFlag = 16;

    // GitObjectType, by name and by the number some servers send instead.
    private const string BlobType = "blob";
    private const string TreeType = "tree";
    private const string CommitType = "commit";
    private const int GitCommitNumber = 1;
    private const int GitTreeNumber = 2;
    private const int GitBlobNumber = 3;
    private const int GitTagNumber = 4;

    private const int ShortShaLength = 8;

    private readonly TfsRequestClient _req;
    private readonly AsyncLruCache<CommitObject> _commits = new(CommitCacheBytes, _ => CommitBytes);
    private readonly AsyncLruCache<TreeObject> _trees = new(TreeCacheBytes, t => (long)t.Count * TreeEntryBytes);
    private readonly AsyncLruCache<byte[]?> _blobs;
    private int _requests;

    public GitObjectReader(TfsRequestClient req, long blobCacheBytes)
    {
        _req = req;
        _blobs = new AsyncLruCache<byte[]?>(blobCacheBytes, b => b?.Length ?? 0);
    }

    /// <summary>TFS requests made so far, for the run's log line.</summary>
    public int RequestCount => Volatile.Read(ref _requests);

    /// <summary>The commit a branch points at now, or null when the repository has no such branch.</summary>
    public async Task<string?> BranchTipAsync(string project, string repoId, string branch, CancellationToken ct)
    {
        var name = GitRefName.Strip(branch.Trim());
        var data = await GetJsonAsync(RepoApi(project, repoId,
            $"refs?filter={Uri.EscapeDataString("heads/" + name)}&api-version={_req.Ver}"), ct);

        // The filter matches by prefix, so "release/1.0" also lists "release/1.0-hotfix".
        var refs = TfsJson.Values(data).Select(GitRefListing.ParseRef).OfType<TfsRef>().ToList();
        var exact = refs.Find(r => string.Equals(r.Name, name, StringComparison.Ordinal))
                    ?? refs.Find(r => string.Equals(r.Name, name, StringComparison.OrdinalIgnoreCase));
        return exact?.ObjectId;
    }

    /// <summary>
    /// The files that differ between two commits. Reading stops once there are more than
    /// <paramref name="maxFiles"/>, so a huge pull request costs one page past the limit, not all of them.
    /// </summary>
    /// <remarks>
    /// A short page is not taken as the last one: a server may hand back fewer changes than asked for, and
    /// treating that as the end would leave files unchecked and the pull request reading as merged. So
    /// paging moves on by what actually arrived, until a page brings nothing new — which also ends the
    /// loop on a server that ignores <c>$skip</c> and repeats itself. When TFS states how many changes
    /// there are, fewer arriving means the list was cut short.
    /// </remarks>
    public async Task<ChangeList> ChangesAsync(
        string project, string repoId, string baseCommit, string targetCommit, int maxFiles, CancellationToken ct)
    {
        var files = new List<FileChange>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        int? stated = null;
        for (var page = 0; page < MaxDiffPages; page++)
        {
            var data = await GetJsonAsync(RepoApi(project, repoId,
                $"diffs/commits?baseVersion={Uri.EscapeDataString(baseCommit)}&baseVersionType=commit" +
                $"&targetVersion={Uri.EscapeDataString(targetCommit)}&targetVersionType=commit" +
                $"&diffCommonCommit=false&$top={DiffPageSize}&$skip={seen.Count}&api-version={_req.Ver}"), ct);

            // No list at all is not "no changes": reading it that way would call the pull request merged.
            if (!data.TryGetProperty("changes", out var changes) || changes.ValueKind != JsonValueKind.Array)
            {
                throw new TfsResponseException("TFS answered the diff without a list of changes, so this pull request's files are unknown.");
            }

            stated ??= StatedTotal(data);
            var fresh = changes.EnumerateArray().Where(e => seen.Add(e.GetRawText())).ToList();
            if (fresh.Count == 0)
            {
                return new ChangeList(files, Truncated: false, Incomplete: stated is int total && seen.Count < total);
            }

            files.AddRange(fresh.Select(ParseChange).OfType<FileChange>());
            if (files.Count > maxFiles)
            {
                return new ChangeList(files.GetRange(0, maxFiles), Truncated: true);
            }
        }
        return new ChangeList(files, Truncated: true);
    }

    /// <summary>The total TFS gives in <c>changeCounts</c>, summed over change types; null when it gives none.</summary>
    private static int? StatedTotal(JsonElement data)
    {
        if (!data.TryGetProperty("changeCounts", out var counts) || counts.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var total = 0;
        foreach (var count in counts.EnumerateObject())
        {
            if (count.Value.ValueKind == JsonValueKind.Number && count.Value.TryGetInt32(out var n))
            {
                total += n;
            }
        }
        return total;
    }

    /// <summary>The commit's first parent — what a merge commit was made on top of — or null for a root commit.</summary>
    public async Task<string?> FirstParentAsync(string project, string repoId, string commitId, CancellationToken ct) =>
        (await CommitAsync(project, repoId, commitId, ct)).Parents.FirstOrDefault();

    /// <summary>The file at <paramref name="path"/> in a commit, or null when the commit has no such file.</summary>
    public async Task<BlobRef?> FindBlobAsync(string project, string repoId, string commitId, string path, CancellationToken ct)
    {
        var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0)
        {
            return null;
        }

        var treeId = (await CommitAsync(project, repoId, commitId, ct)).TreeId;
        for (var i = 0; i < segments.Length; i++)
        {
            var tree = await TreeAsync(project, repoId, treeId, ct);
            if (tree.Find(segments[i]) is not { } entry)
            {
                return null;
            }

            // Something is at this path, just not what the change needs. "Absent" would be the wrong
            // answer — on QA's side it would even excuse a missing file — so it is an error instead.
            var last = i == segments.Length - 1;
            if (last ? !entry.IsBlob : !entry.IsTree)
            {
                throw new TfsResponseException(
                    $"{path} is not a file at commit {ShortId(commitId)}: '{string.Join('/', segments.Take(i + 1))}' is a {Describe(entry.Type)}.");
            }
            if (last)
            {
                return new BlobRef(entry.ObjectId, entry.Size);
            }
            treeId = entry.ObjectId;
        }
        return null;
    }

    /// <summary>A file's bytes, or null when it is larger than <paramref name="maxBytes"/>.</summary>
    public Task<byte[]?> ReadBlobAsync(string project, string repoId, string blobId, long maxBytes, CancellationToken ct) =>
        _blobs.GetOrAddAsync($"{repoId}:{blobId}", () => CountedAsync(() => _req.GetBytesAsync(
            RepoApi(project, repoId, $"blobs/{Uri.EscapeDataString(blobId)}?$format=octetstream&api-version={_req.Ver}"),
            maxBytes, ct), ct));

    private Task<CommitObject> CommitAsync(string project, string repoId, string commitId, CancellationToken ct) =>
        _commits.GetOrAddAsync($"{repoId}:{commitId}", async () =>
        {
            var data = await GetJsonAsync(RepoApi(project, repoId,
                $"commits/{Uri.EscapeDataString(commitId)}?api-version={_req.Ver}"), ct);
            var parents = data.TryGetProperty("parents", out var list) && list.ValueKind == JsonValueKind.Array
                ? list.EnumerateArray().Select(p => p.GetString() ?? "").Where(p => p.Length > 0).ToList()
                : new List<string>();
            var treeId = TfsJson.Str(data, "treeId");
            if (treeId.Length == 0)
            {
                throw new TfsResponseException($"TFS did not return the tree of commit {ShortId(commitId)}.");
            }
            return new CommitObject(treeId, parents);
        });

    private Task<TreeObject> TreeAsync(string project, string repoId, string treeId, CancellationToken ct) =>
        _trees.GetOrAddAsync($"{repoId}:{treeId}", async () =>
        {
            var data = await GetJsonAsync(RepoApi(project, repoId,
                $"trees/{Uri.EscapeDataString(treeId)}?api-version={_req.Ver}"), ct);

            // A listing with no entries at all would make every file under it read as absent.
            if (!data.TryGetProperty("treeEntries", out var list) || list.ValueKind != JsonValueKind.Array)
            {
                throw new TfsResponseException($"TFS returned a folder listing without entries for tree {ShortId(treeId)}.");
            }

            var entries = list.EnumerateArray()
                .Select(e => new TreeEntry(
                    TfsJson.Str(e, "relativePath"),
                    TfsJson.Str(e, "objectId"),
                    ObjectType(e),
                    e.TryGetProperty("size", out var size) && size.ValueKind == JsonValueKind.Number
                        && size.TryGetInt64(out var bytes) ? bytes : null))
                .Where(e => e.Name.Length > 0 && e.ObjectId.Length > 0);
            return new TreeObject(entries);
        });

    private string RepoApi(string project, string repoId, string path) =>
        _req.ProjectApi(project, $"git/repositories/{Uri.EscapeDataString(repoId)}/{path}");

    private Task<JsonElement> GetJsonAsync(string url, CancellationToken ct) =>
        CountedAsync(() => _req.GetJsonAsync(url, ct), ct);

    private Task<T> CountedAsync<T>(Func<Task<T>> request, CancellationToken ct)
    {
        Interlocked.Increment(ref _requests);
        return TfsThrottle.RunAsync(request, ct);
    }

    private static FileChange? ParseChange(JsonElement change)
    {
        if (!change.TryGetProperty("item", out var item) || item.ValueKind != JsonValueKind.Object)
        {
            return null;
        }
        if (item.TryGetProperty("isFolder", out var folder) && folder.ValueKind == JsonValueKind.True)
        {
            return null;
        }

        var objectType = ObjectType(item);
        var path = TfsJson.Str(item, "path");
        if (path.Length == 0 || objectType == TreeType)
        {
            return null;
        }

        var kind = KindOf(change);
        if (objectType.Length > 0 && objectType != BlobType)
        {
            // Kept, not dropped: a pull request that only moves a submodule would otherwise list no
            // files and read as fully merged.
            return new FileChange(path, null, kind, null, null, objectType == CommitType
                ? "A submodule pointer — the code it points at lives in another repository, so it can't be compared here; check it by hand."
                : $"A git {objectType} entry, which can't be compared here; check it by hand.");
        }

        var objectId = Blank(TfsJson.Str(item, "objectId"));
        var originalObjectId = Blank(TfsJson.Str(item, "originalObjectId"));
        var originalPath = Blank(TfsJson.Str(change, "originalPath")) ?? Blank(TfsJson.Str(change, "sourceServerItem"));
        return kind switch
        {
            FileChangeKind.Add => new FileChange(path, null, kind, null, objectId),
            // A deleted item's own id is ambiguous across server versions, so the old id is only taken
            // when the server names it explicitly; otherwise the tree walk finds it.
            FileChangeKind.Delete => new FileChange(path, null, kind, originalObjectId, null),
            _ => new FileChange(path, originalPath == path ? null : originalPath, kind, originalObjectId, objectId)
        };
    }

    /// <summary>The git object type, lower-case, whether the server sends it by name or as its enum number.</summary>
    private static string ObjectType(JsonElement el)
    {
        if (!el.TryGetProperty("gitObjectType", out var type))
        {
            return "";
        }
        if (type.ValueKind == JsonValueKind.Number && type.TryGetInt32(out var number))
        {
            return number switch
            {
                GitCommitNumber => CommitType,
                GitTreeNumber => TreeType,
                GitBlobNumber => BlobType,
                GitTagNumber => "tag",
                _ => $"type {number}"
            };
        }
        return type.ValueKind == JsonValueKind.String ? (type.GetString() ?? "").ToLowerInvariant() : "";
    }

    private static string Describe(string objectType) => objectType switch
    {
        TreeType => "folder",
        CommitType => "submodule",
        BlobType => "file",
        "" => "item of unknown type",
        _ => objectType
    };

    private static string ShortId(string sha) => sha.Length > ShortShaLength ? sha[..ShortShaLength] : sha;

    private static FileChangeKind KindOf(JsonElement change)
    {
        if (!change.TryGetProperty("changeType", out var type))
        {
            return FileChangeKind.Edit;
        }
        if (type.ValueKind == JsonValueKind.Number && type.TryGetInt32(out var flags))
        {
            return FromFlags((flags & DeleteFlag) != 0, (flags & AddFlag) != 0, (flags & RenameFlag) != 0);
        }

        // Sent as comma-separated flag names, e.g. "edit, rename".
        var names = (type.GetString() ?? "").Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        bool Has(string flag) => names.Contains(flag, StringComparer.OrdinalIgnoreCase);
        return FromFlags(Has("delete"), Has("add"), Has("rename"));
    }

    private static FileChangeKind FromFlags(bool delete, bool add, bool rename)
    {
        if (delete)
        {
            return FileChangeKind.Delete;
        }
        if (add)
        {
            return FileChangeKind.Add;
        }
        return rename ? FileChangeKind.Rename : FileChangeKind.Edit;
    }

    private static string? Blank(string value) => value.Length == 0 ? null : value;

    private sealed record CommitObject(string TreeId, IReadOnlyList<string> Parents);

    /// <param name="Type">Lower-case, as <see cref="ObjectType"/> reads it.</param>
    private sealed record TreeEntry(string Name, string ObjectId, string Type, long? Size)
    {
        public bool IsBlob => Type == BlobType;
        public bool IsTree => Type == TreeType;
    }

    /// <summary>
    /// One directory. Git names are case-sensitive and matched exactly first; a case-insensitive match
    /// is the fallback, so a folder whose case was changed on one branch is still found.
    /// </summary>
    private sealed class TreeObject
    {
        private readonly Dictionary<string, TreeEntry> _exact = new(StringComparer.Ordinal);
        private readonly Dictionary<string, TreeEntry> _anyCase = new(StringComparer.OrdinalIgnoreCase);

        public TreeObject(IEnumerable<TreeEntry> entries)
        {
            foreach (var entry in entries)
            {
                _exact[entry.Name] = entry;
                _anyCase.TryAdd(entry.Name, entry);
            }
        }

        public int Count => _exact.Count;

        public TreeEntry? Find(string name) =>
            _exact.TryGetValue(name, out var entry) || _anyCase.TryGetValue(name, out entry) ? entry : null;
    }
}

/// <summary>
/// An async get-or-fetch cache with least-recently-used eviction by an approximate byte budget.
/// Concurrent requests for one key share a single fetch, and a fetch that fails or is cancelled is
/// dropped from the cache so the next request tries again instead of inheriting the failure.
/// </summary>
internal sealed class AsyncLruCache<T>
{
    private readonly object _gate = new();
    private readonly Dictionary<string, LinkedListNode<Slot>> _slots = new(StringComparer.Ordinal);
    private readonly LinkedList<Slot> _recency = new();
    private readonly long _budget;
    private readonly Func<T, long> _sizeOf;
    private long _used;

    public AsyncLruCache(long budget, Func<T, long> sizeOf)
    {
        _budget = budget;
        _sizeOf = sizeOf;
    }

    public Task<T> GetOrAddAsync(string key, Func<Task<T>> fetch)
    {
        Slot slot;
        var added = false;
        lock (_gate)
        {
            if (_slots.TryGetValue(key, out var node))
            {
                _recency.Remove(node);
                _recency.AddFirst(node);
                slot = node.Value;
            }
            else
            {
                // Started outside the lock, and wrapped so even a synchronous throw becomes a faulted
                // task — Lazy would otherwise cache the exception for good.
                slot = new Slot(key, new Lazy<Task<T>>(() => RunAsync(fetch), LazyThreadSafetyMode.ExecutionAndPublication));
                _slots[key] = _recency.AddFirst(slot);
                added = true;
            }
        }

        var task = slot.Value.Value;
        if (added)
        {
            _ = task.ContinueWith(done => Settle(slot, done), CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        }
        return task;
    }

    private static async Task<T> RunAsync(Func<Task<T>> fetch) => await fetch();

    private void Settle(Slot slot, Task<T> done)
    {
        lock (_gate)
        {
            if (!_slots.TryGetValue(slot.Key, out var node) || !ReferenceEquals(node.Value, slot))
            {
                return;
            }
            if (done.Status != TaskStatus.RanToCompletion)
            {
                Remove(node);
                return;
            }

            slot.Size = Math.Max(0, _sizeOf(done.Result));
            _used += slot.Size;
            while (_used > _budget && _recency.Last is { } oldest && !ReferenceEquals(oldest.Value, slot))
            {
                Remove(oldest);
            }
        }
    }

    private void Remove(LinkedListNode<Slot> node)
    {
        _slots.Remove(node.Value.Key);
        _recency.Remove(node);
        _used -= node.Value.Size;
    }

    private sealed class Slot
    {
        public Slot(string key, Lazy<Task<T>> value)
        {
            Key = key;
            Value = value;
        }

        public string Key { get; }
        public Lazy<Task<T>> Value { get; }

        /// <summary>Zero until the fetch completes; an in-flight slot costs nothing against the budget.</summary>
        public long Size { get; set; }
    }
}
