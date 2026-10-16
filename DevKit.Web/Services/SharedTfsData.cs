using System.Diagnostics;
using DevKit.Web.Models;

namespace DevKit.Web.Services;

/// <summary>
/// The four TFS reads behind every page's area, sprint and repository pickers. Each one throws when TFS cannot
/// answer, so a failure is never taken for a server with nothing on it.
/// </summary>
public interface ITfsMetadataSource
{
    Task<List<TfsProject>> GetProjectsAsync(CancellationToken ct = default);

    Task<List<TfsRepo>> GetRepositoriesAsync(CancellationToken ct = default);

    Task<List<TfsArea>> GetAreasAsync(string project, CancellationToken ct = default);

    Task<List<TfsIteration>> GetIterationsAsync(string project, CancellationToken ct = default);
}

/// <summary>
/// One finished load of the TFS metadata. It never changes once built: a later load publishes a new snapshot in its
/// place, so a page never sees lists half filled or listed twice.
/// </summary>
public sealed record TfsMetadataSnapshot(
    IReadOnlyList<TfsProject> Projects,
    IReadOnlyList<TfsRepo> Repos,
    IReadOnlyList<TfsArea> Areas,
    IReadOnlyList<TfsIteration> Iterations,
    bool IsComplete)
{
    public static TfsMetadataSnapshot Empty { get; } = new([], [], [], [], false);

    /// <summary>
    /// Which snapshot to keep after a load. A complete load always replaces; a partial one replaces only a snapshot
    /// that is not complete either, and never with nothing: a load whose project read failed has no lists at all, so
    /// it leaves the lists an earlier partial load did read. A failed refresh therefore never takes usable lists away,
    /// while a first load that partly failed still shows what it did read.
    /// </summary>
    public static TfsMetadataSnapshot Choose(TfsMetadataSnapshot current, TfsMetadataSnapshot loaded)
    {
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(loaded);
        if (loaded.IsComplete)
        {
            return loaded;
        }

        if (current.IsComplete || (loaded.Projects.Count == 0 && current.Projects.Count > 0))
        {
            return current;
        }

        return loaded;
    }
}

public enum TfsMetadataLoadStatus
{
    Loaded,
    Failed,
    Cancelled
}

/// <summary>
/// What a page does once it has asked for the metadata: carry on, show <see cref="ErrorMessage"/>, or stop because
/// it was closed. The message is written to be shown as it is; the exceptions behind it are in the log.
/// </summary>
public readonly record struct TfsMetadataLoad(TfsMetadataLoadStatus Status, string? ErrorMessage)
{
    public static TfsMetadataLoad Complete { get; } = new(TfsMetadataLoadStatus.Loaded, null);

    public static TfsMetadataLoad Stopped { get; } = new(TfsMetadataLoadStatus.Cancelled, null);

    public bool Succeeded => Status == TfsMetadataLoadStatus.Loaded;

    public bool Cancelled => Status == TfsMetadataLoadStatus.Cancelled;

    public static TfsMetadataLoad Failure(string message) => new(TfsMetadataLoadStatus.Failed, message);
}

/// <summary>
/// The projects, repositories, areas and sprints every page of a circuit shares, loaded once for all of them.
///
/// Callers that arrive during a load share it, and a caller's token only ends its own wait: the load goes on for
/// whoever else is waiting, as <see cref="BranchCacheService"/> does for a branch listing. A finished load publishes
/// one immutable snapshot in place of the last, by <see cref="TfsMetadataSnapshot.Choose"/>. Each failed read is
/// logged once at Error here and reaches the page as a <see cref="TfsErrors.Describe"/> message; the lists that did
/// load stay usable, and <see cref="IsLoaded"/> stays false so the next page to ask reads everything again.
/// </summary>
public sealed class SharedTfsData : IDisposable
{
    /// <summary>Ceiling for one whole load: well above a normal one, short of leaving the pickers empty for good.</summary>
    private static readonly TimeSpan DefaultLoadTimeout = TimeSpan.FromMinutes(2);

    private readonly ITfsMetadataSource _source;
    private readonly ILogger<SharedTfsData> _logger;
    private readonly TimeSpan _loadTimeout;
    private readonly CancellationTokenSource _lifetime = new();

    // Guards the fields below. Nothing is awaited under it, and a superseded load's source is cancelled only once
    // it is released, so no continuation that cancelling runs inline can see the state half changed.
    private readonly object _sync = new();
    private TfsMetadataSnapshot _snapshot = TfsMetadataSnapshot.Empty;
    private Task<TfsMetadataLoad>? _inFlight;
    private CancellationTokenSource? _inFlightStop;
    private long _generation;
    private bool _disposed;

    public SharedTfsData(ITfsMetadataSource source, ILogger<SharedTfsData> logger)
        : this(source, logger, DefaultLoadTimeout)
    {
    }

    /// <summary>With a ceiling of the caller's own, as tests use it; DI only sees the public constructor.</summary>
    internal SharedTfsData(ITfsMetadataSource source, ILogger<SharedTfsData> logger, TimeSpan loadTimeout)
    {
        _source = source;
        _logger = logger;
        _loadTimeout = loadTimeout;
    }

    /// <summary>True only when every list was read. A load that partly failed keeps what it read but stays not loaded.</summary>
    public bool IsLoaded => Current.IsComplete;

    public IReadOnlyList<TfsProject> Projects => Current.Projects;

    public IReadOnlyList<TfsRepo> Repos => Current.Repos;

    public IReadOnlyList<TfsArea> Areas => Current.Areas;

    public IReadOnlyList<TfsIteration> Iterations => Current.Iterations;

    private TfsMetadataSnapshot Current => Volatile.Read(ref _snapshot);

    /// <summary>
    /// Answers at once when everything is loaded; otherwise joins the load in progress or starts one.
    /// <paramref name="ct"/> only ends this caller's wait, with <see cref="TfsMetadataLoad.Cancelled"/>.
    /// </summary>
    public Task<TfsMetadataLoad> EnsureLoadedAsync(CancellationToken ct = default) => FollowAsync(JoinOrStart(), ct);

    /// <summary>Reads everything again in place of any load in progress. A refresh that fails keeps complete lists.</summary>
    public Task<TfsMetadataLoad> ReloadAsync(CancellationToken ct = default) => FollowAsync(Restart(), ct);

    /// <summary>Forgets the lists and stops any load in progress, after the server changed; the next caller loads afresh.</summary>
    public void Invalidate()
    {
        CancellationTokenSource? superseded;
        lock (_sync)
        {
            if (_disposed)
            {
                return;
            }
            superseded = DetachLocked();
            Volatile.Write(ref _snapshot, TfsMetadataSnapshot.Empty);
        }
        Retire(superseded);
    }

    public void Dispose()
    {
        CancellationTokenSource? running;
        lock (_sync)
        {
            if (_disposed)
            {
                return;
            }
            _disposed = true;
            running = DetachLocked();
        }
        Retire(running);
        _lifetime.Cancel();
        _lifetime.Dispose();
    }

    /// <summary>
    /// Waits for the load, and for the one that replaced it when it was superseded meanwhile, until one ends for
    /// good, the caller stops waiting or the circuit closes.
    /// </summary>
    private async Task<TfsMetadataLoad> FollowAsync(PendingLoad next, CancellationToken ct)
    {
        while (next.Load is { } load)
        {
            if (!await CompletesFirstAsync(load, ct).ConfigureAwait(false))
            {
                return TfsMetadataLoad.Stopped;
            }
            if (!Superseded(next.Generation))
            {
                return load.IsCompletedSuccessfully ? load.Result : TfsMetadataLoad.Stopped;
            }
            next = JoinOrStart();
        }
        return IsLoaded ? TfsMetadataLoad.Complete : TfsMetadataLoad.Stopped;
    }

    /// <summary>
    /// Nothing to wait for when everything is loaded or the circuit has closed; otherwise the load in progress, or a
    /// new one when there is none or the last one failed.
    /// </summary>
    private PendingLoad JoinOrStart()
    {
        PendingLoad started;
        CancellationTokenSource? superseded;
        lock (_sync)
        {
            if (_disposed || _snapshot.IsComplete)
            {
                return new PendingLoad(null, _generation);
            }
            if (_inFlight is { IsCompleted: false })
            {
                return new PendingLoad(_inFlight, _generation);
            }
            (started, superseded) = StartLocked();
        }
        Retire(superseded);
        return started;
    }

    /// <summary>A new load in place of the one in progress, if any; nothing once the circuit has closed.</summary>
    private PendingLoad Restart()
    {
        PendingLoad started;
        CancellationTokenSource? superseded;
        lock (_sync)
        {
            if (_disposed)
            {
                return new PendingLoad(null, _generation);
            }
            (started, superseded) = StartLocked();
        }
        Retire(superseded);
        return started;
    }

    // Under the lock. The load runs on the thread pool, so none of it runs while the lock is held.
    private (PendingLoad Started, CancellationTokenSource? Superseded) StartLocked()
    {
        var superseded = DetachLocked();
        var generation = _generation;
        var stop = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        var stopToken = stop.Token;
        _inFlightStop = stop;
        _inFlight = Task.Run(() => LoadAsync(generation, stopToken));
        return (new PendingLoad(_inFlight, generation), superseded);
    }

    // Under the lock: moves to a new generation, so the load in progress may no longer publish, and hands back its
    // stop source for the caller to cancel once the lock is released.
    private CancellationTokenSource? DetachLocked()
    {
        _generation++;
        var stop = _inFlightStop;
        _inFlightStop = null;
        _inFlight = null;
        return stop;
    }

    private static void Retire(CancellationTokenSource? stop)
    {
        if (stop is null)
        {
            return;
        }
        stop.Cancel();
        stop.Dispose();
    }

    private bool Superseded(long generation)
    {
        lock (_sync)
        {
            return !_disposed && generation != _generation;
        }
    }

    // Whether the load ended before the caller stopped waiting. It never throws: a page closing is not a failure.
    private static async Task<bool> CompletesFirstAsync(Task load, CancellationToken ct)
    {
        if (load.IsCompleted)
        {
            return true;
        }
        var left = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using (ct.Register(static state => ((TaskCompletionSource)state!).TrySetResult(), left))
        {
            return await Task.WhenAny(load, left.Task).ConfigureAwait(false) == load;
        }
    }

    /// <summary>
    /// One load under its ceiling. It ends without a result only when it is superseded or the circuit closes: read
    /// failures are handled per list, and the catch here makes sure anything else still reaches the waiting pages.
    /// </summary>
    private async Task<TfsMetadataLoad> LoadAsync(long generation, CancellationToken stop)
    {
        using var attempt = CancellationTokenSource.CreateLinkedTokenSource(stop);
        attempt.CancelAfter(_loadTimeout);
        var run = new TfsMetadataRun(_logger, generation, stop, attempt.Token);
        var started = Stopwatch.StartNew();
        _logger.LogInformation("TFS metadata load {Generation} started", generation);
        try
        {
            var loaded = await ReadAllAsync(run).ConfigureAwait(false);
            var published = TryPublish(generation, loaded);
            _logger.LogInformation(
                "TFS metadata load {Generation} ended in {ElapsedMs} ms with {Projects} projects, {Repos} repositories, " +
                "{Areas} areas and {Sprints} sprints; {Failed} lists not read; published: {Published}",
                generation, started.ElapsedMilliseconds, loaded.Projects.Count, loaded.Repos.Count,
                loaded.Areas.Count, loaded.Iterations.Count, run.FailedCount, published);
            return run.Result();
        }
        catch (Exception ex) when (!stop.IsCancellationRequested)
        {
            _logger.LogError(ex, "TFS metadata load {Generation} failed after {ElapsedMs} ms", generation, started.ElapsedMilliseconds);
            return TfsMetadataLoad.Failure(TfsErrors.Describe(ex));
        }
    }

    /// <summary>Projects first, since everything else hangs off them, then repositories, then each project's areas and sprints.</summary>
    private async Task<TfsMetadataSnapshot> ReadAllAsync(TfsMetadataRun run)
    {
        var projects = await run.ReadAsync(TfsMetadataRun.ProjectsList, null, _source.GetProjectsAsync).ConfigureAwait(false);
        if (projects is null)
        {
            return TfsMetadataSnapshot.Empty;
        }

        var repos = await run.ReadAsync(TfsMetadataRun.ReposList, null, _source.GetRepositoriesAsync).ConfigureAwait(false);
        var areas = new List<TfsArea>();
        var iterations = new List<TfsIteration>();
        foreach (var name in projects.Select(project => project.Name))
        {
            areas.AddRange(await run.ReadAsync(TfsMetadataRun.AreasList, name, ct => _source.GetAreasAsync(name, ct)).ConfigureAwait(false) ?? []);
            iterations.AddRange(await run.ReadAsync(TfsMetadataRun.SprintsList, name, ct => _source.GetIterationsAsync(name, ct)).ConfigureAwait(false) ?? []);
        }
        return new TfsMetadataSnapshot(projects, repos ?? [], areas, iterations, run.FailedCount == 0);
    }

    private bool TryPublish(long generation, TfsMetadataSnapshot loaded)
    {
        lock (_sync)
        {
            if (_disposed || generation != _generation)
            {
                return false;
            }
            Volatile.Write(ref _snapshot, TfsMetadataSnapshot.Choose(_snapshot, loaded));
            return true;
        }
    }

    /// <summary>A load to wait for and the generation it was started in; no load means there is nothing to wait for.</summary>
    private readonly record struct PendingLoad(Task<TfsMetadataLoad>? Load, long Generation);
}

/// <summary>
/// The reads of one metadata load, one after another. A failed read is logged once at Error and counted, and its
/// list stays empty; only the first failure is described to the page. Being superseded or the circuit closing is
/// not a failure: that cancellation is let through.
/// </summary>
internal sealed class TfsMetadataRun(ILogger logger, long generation, CancellationToken stop, CancellationToken deadline)
{
    public const string ProjectsList = "projects";
    public const string ReposList = "repositories";
    public const string AreasList = "areas";
    public const string SprintsList = "sprints";

    private const string CollectionScope = "the collection";

    private string? _firstProblem;
    private bool _pastDeadline;

    /// <summary>Lists that were not read, whether their read failed or the load ran out of time before it.</summary>
    public int FailedCount { get; private set; }

    /// <summary>The list, or null when it could not be read. <paramref name="project"/> is null for collection-wide lists.</summary>
    public async Task<List<T>?> ReadAsync<T>(string list, string? project, Func<CancellationToken, Task<List<T>>> read)
    {
        stop.ThrowIfCancellationRequested();
        if (_pastDeadline)
        {
            // The read that ran out of time is already logged and described; the lists after it are only counted.
            FailedCount++;
            return null;
        }

        try
        {
            return await read(deadline).ConfigureAwait(false);
        }
        catch (Exception ex) when (!stop.IsCancellationRequested)
        {
            logger.LogError(ex, "TFS metadata load {Generation} could not read the {List} of {Project}",
                generation, list, project ?? CollectionScope);
            FailedCount++;
            _firstProblem ??= Describe(list, project, ex);
            _pastDeadline = deadline.IsCancellationRequested;
            return null;
        }
    }

    public TfsMetadataLoad Result()
    {
        if (FailedCount == 0)
        {
            return TfsMetadataLoad.Complete;
        }
        var more = FailedCount - 1;
        var first = _firstProblem ?? "Some TFS lists could not be loaded.";
        if (more == 0)
        {
            return TfsMetadataLoad.Failure(first);
        }

        var lists = more == 1 ? "list" : "lists";
        return TfsMetadataLoad.Failure($"{first} ({more} more {lists} failed. The DevKit log has the details.)");
    }

    private static string Describe(string list, string? project, Exception ex) => project is null
        ? $"Couldn't load the {list}: {TfsErrors.Describe(ex)}"
        : $"Couldn't load the {list} for {project}: {TfsErrors.Describe(ex)}";
}
