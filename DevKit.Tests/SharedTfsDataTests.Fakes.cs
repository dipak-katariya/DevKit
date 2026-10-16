using System.Collections.Concurrent;
using DevKit.Web.Models;
using DevKit.Web.Services;

namespace DevKit.Tests;

/// <summary>
/// The four metadata reads over made-up lists. Every name carries <see cref="Server"/>, read when the read starts, so a
/// test can tell one server's lists, or one load's, from another's. A read can be told to fail, or held until the
/// test lets it go; it counts how often each list was read and says when a read starts and when its token is cancelled.
/// </summary>
internal sealed class FakeTfsMetadata : ITfsMetadataSource
{
    private readonly ConcurrentDictionary<string, int> _reads = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _started = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, TaskCompletionSource> _cancelled = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, Hold> _holds = new(StringComparer.Ordinal);

    private sealed record Hold(TaskCompletionSource Release, bool IgnoresCancellation);

    public IReadOnlyList<string> ProjectNames { get; set; } = ["Contoso", "Fabrikam"];

    public string Server { get; set; } = "one";

    /// <summary>Failures by list, or by list and project as "areas|Contoso".</summary>
    public ConcurrentDictionary<string, Exception> Failures { get; } = new(StringComparer.Ordinal);

    public static string Key(string list, string project) => $"{list}|{project}";

    public int Reads(string list) => _reads.GetValueOrDefault(list);

    /// <summary>
    /// Holds the next read of the list until the returned source is completed. A read that ignores cancellation
    /// still answers once let go, after its load has been superseded.
    /// </summary>
    public TaskCompletionSource HoldNext(string list, bool ignoresCancellation = false)
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _holds[list] = new Hold(release, ignoresCancellation);
        return release;
    }

    /// <summary>Completes once a read of the list has started; each started read lets one wait through.</summary>
    public Task StartedAsync(string list) => Signal(list).WaitAsync();

    /// <summary>Completes once the token of a read of the list has been cancelled.</summary>
    public Task CancelledAsync(string list) => CancelSignal(list).Task;

    public Task<List<TfsProject>> GetProjectsAsync(CancellationToken ct = default) =>
        ReadAsync(TfsMetadataRun.ProjectsList, null, ct, _ => ProjectNames.Select(n => new TfsProject { Id = n, Name = n }).ToList());

    public Task<List<TfsRepo>> GetRepositoriesAsync(CancellationToken ct = default) =>
        ReadAsync(TfsMetadataRun.ReposList, null, ct, server =>
            ProjectNames.Select(p => new TfsRepo { Id = $"{p}-{server}", Name = $"{p}.Web-{server}", Project = p }).ToList());

    public Task<List<TfsArea>> GetAreasAsync(string project, CancellationToken ct = default) =>
        ReadAsync(TfsMetadataRun.AreasList, project, ct, server => new List<TfsArea>
        {
            new() { Name = $"{project}-{server}", Path = $@"\{project}\Area", Project = project },
            new() { Name = $"Web-{server}", Path = $@"\{project}\Area\Web", Project = project }
        });

    public Task<List<TfsIteration>> GetIterationsAsync(string project, CancellationToken ct = default) =>
        ReadAsync(TfsMetadataRun.SprintsList, project, ct, server => new List<TfsIteration>
        {
            new() { Name = $"Sprint 1-{server}", Path = $@"\{project}\Iteration\Sprint 1", Project = project }
        });

    private async Task<List<T>> ReadAsync<T>(string list, string? project, CancellationToken ct, Func<string, List<T>> answer)
    {
        var server = Server;
        _reads.AddOrUpdate(list, 1, (_, n) => n + 1);
        // Not disposed here: cancelling can run this read's continuation inline, and disposing the registration
        // there would drop the signal before the token gets to it. It goes when the load disposes its token source.
        ct.Register(() => CancelSignal(list).TrySetResult());
        Signal(list).Release();

        var ignoresCancellation = false;
        if (_holds.TryRemove(list, out var hold))
        {
            ignoresCancellation = hold.IgnoresCancellation;
            await (ignoresCancellation ? hold.Release.Task : hold.Release.Task.WaitAsync(ct));
        }
        if (!ignoresCancellation)
        {
            ct.ThrowIfCancellationRequested();
        }

        if (Failures.TryGetValue(list, out var failure)
            || (project is not null && Failures.TryGetValue(Key(list, project), out failure)))
        {
            throw failure;
        }
        return answer(server);
    }

    private SemaphoreSlim Signal(string list) => _started.GetOrAdd(list, _ => new SemaphoreSlim(0));

    private TaskCompletionSource CancelSignal(string list) =>
        _cancelled.GetOrAdd(list, _ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));
}
