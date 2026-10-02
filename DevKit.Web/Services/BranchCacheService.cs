using DevKit.Web.Models;

namespace DevKit.Web.Services;

/// <summary>
/// Per-repository branch lists, fetched once and shared for the lifetime of a circuit.
///
/// Three properties matter here. Listings go through <see cref="CodeMergingService"/>, which pages
/// the refs endpoint behind a process-wide concurrency gate — a repository with tens of thousands
/// of refs would otherwise be one unbounded request that times out. Concurrent callers for the
/// same repository share a single in-flight task, so re-rendering a picker or opening two of them
/// cannot stampede TFS. And that shared task is never bound to a caller's cancellation token: one
/// component navigating away would otherwise cancel a load another component is awaiting.
/// </summary>
public sealed class BranchCacheService : IDisposable
{
    /// <summary>Branches change whenever someone pushes, so a cached list is refreshed periodically.</summary>
    private static readonly TimeSpan Ttl = TimeSpan.FromMinutes(5);

    /// <summary>Ceiling for one listing: well above a normal load, short of hanging a picker forever.</summary>
    private static readonly TimeSpan FetchTimeout = TimeSpan.FromMinutes(2);

    private static readonly IReadOnlyList<string> Empty = Array.Empty<string>();

    private readonly CodeMergingService _merging;
    private readonly ILogger<BranchCacheService> _logger;

    // Guards _entries only. The critical section creates a task, it never awaits one, so the lock
    // is held for microseconds and no TFS call ever runs under it.
    private readonly object _sync = new();
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.OrdinalIgnoreCase);

    private readonly CancellationTokenSource _cts = new();
    private bool _disposed;

    public BranchCacheService(CodeMergingService merging, ILogger<BranchCacheService> logger)
    {
        _merging = merging;
        _logger = logger;
    }

    /// <summary>
    /// Branch names for one repository, from cache or a fresh listing. The returned list is shared
    /// and must be treated as read-only. <paramref name="ct"/> only stops the caller waiting; the
    /// underlying listing continues for whoever else is waiting on it.
    /// </summary>
    public async Task<IReadOnlyList<string>> GetAsync(TfsRepo repo, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(repo);
        if (_disposed || string.IsNullOrWhiteSpace(repo.Id)) return Empty;

        Task<IReadOnlyList<string>> load;
        lock (_sync)
        {
            if (!_entries.TryGetValue(repo.Id, out var entry) || !entry.IsUsable(Ttl))
            {
                entry = new Entry(FetchAsync(repo));
                _entries[repo.Id] = entry;
            }
            load = entry.Branches;
        }

        return await load.WaitAsync(ct).ConfigureAwait(false);
    }

    /// <summary>Cached branches for a repository, or null when none have been loaded for it yet.</summary>
    public IReadOnlyList<string>? Peek(string? repoId)
    {
        if (string.IsNullOrWhiteSpace(repoId)) return null;

        lock (_sync)
        {
            return _entries.TryGetValue(repoId, out var entry) && entry.Branches.IsCompletedSuccessfully
                ? entry.Branches.Result
                : null;
        }
    }

    public bool IsLoading(string? repoId)
    {
        if (string.IsNullOrWhiteSpace(repoId)) return false;

        lock (_sync)
        {
            return _entries.TryGetValue(repoId, out var entry) && !entry.Branches.IsCompleted;
        }
    }

    /// <summary>
    /// Drops a repository's cached list. Called after a branch is created or deleted so the next
    /// read reflects it rather than waiting out the TTL.
    /// </summary>
    public void Invalidate(string? repoId)
    {
        if (string.IsNullOrWhiteSpace(repoId)) return;

        lock (_sync) { _entries.Remove(repoId); }
    }

    private async Task<IReadOnlyList<string>> FetchAsync(TfsRepo repo)
    {
        using var attempt = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
        attempt.CancelAfter(FetchTimeout);

        var started = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            var branches = await _merging.GetBranchesAsync(new[] { repo }, attempt.Token).ConfigureAwait(false);
            _logger.LogDebug("Loaded {Count} branches for {Repo} in {Elapsed}ms",
                branches.Count, repo.Name, started.ElapsedMilliseconds);
            return branches;
        }
        catch (OperationCanceledException) when (_cts.IsCancellationRequested)
        {
            return Empty;
        }
        catch (OperationCanceledException)
        {
            _logger.LogWarning("Branch listing for {Repo} exceeded {Seconds}s and was abandoned",
                repo.Name, FetchTimeout.TotalSeconds);
            throw new TimeoutException($"Loading branches for \"{repo.Name}\" timed out.");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Branch listing failed for {Repo}", repo.Name);
            throw;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        lock (_sync) { _entries.Clear(); }

        try { _cts.Cancel(); }
        catch (ObjectDisposedException) { /* already torn down */ }
        _cts.Dispose();
    }

    private sealed class Entry(Task<IReadOnlyList<string>> branches)
    {
        public Task<IReadOnlyList<string>> Branches { get; } = branches;

        private DateTimeOffset StartedAt { get; } = DateTimeOffset.UtcNow;

        /// <summary>
        /// A load still running is reused so callers share it. A successful one is reused until it
        /// ages out. Failures and empty results are never reused: the branch listing swallows
        /// per-repository errors and returns nothing, so caching an empty list would keep showing
        /// "no branches" for the whole TTL after a single TFS hiccup.
        /// </summary>
        public bool IsUsable(TimeSpan ttl)
        {
            if (!Branches.IsCompleted) return true;
            if (!Branches.IsCompletedSuccessfully || Branches.Result.Count == 0) return false;
            return DateTimeOffset.UtcNow - StartedAt < ttl;
        }
    }
}
