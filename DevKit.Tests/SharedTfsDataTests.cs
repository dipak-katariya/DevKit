using System.Net;
using DevKit.Web.Services;
using Microsoft.Extensions.Logging;
using Xunit;

namespace DevKit.Tests;

/// <summary>
/// The circuit's one TFS metadata loader (B074, ARC-04, CQ-03): it always reads all four lists, callers that arrive
/// during a load share it, a finished load replaces the lists rather than adding to them, and it counts as loaded only
/// when every list was read. A failure is logged once at Error and described to the page; a page closing only ends its
/// own wait.
/// </summary>
public sealed class SharedTfsDataTests : IDisposable
{
    private const string Projects = TfsMetadataRun.ProjectsList;
    private const string Repos = TfsMetadataRun.ReposList;
    private const string Areas = TfsMetadataRun.AreasList;
    private const string Sprints = TfsMetadataRun.SprintsList;

    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(10);
    private static readonly string TokenAdvice = TfsErrors.Describe(new HttpRequestException("", null, HttpStatusCode.Unauthorized));
    private static readonly string TooSlow = TfsErrors.Describe(new TimeoutException());

    private readonly FakeTfsMetadata _tfs = new();
    private readonly RecordingLogger<SharedTfsData> _log = new();
    private readonly SharedTfsData _data;

    public SharedTfsDataTests()
    {
        _data = new SharedTfsData(_tfs, _log);
    }

    public void Dispose() => _data.Dispose();

    [Fact]
    public async Task Loads_projects_repositories_areas_and_sprints()
    {
        var load = await _data.EnsureLoadedAsync().WaitAsync(TestTimeout);

        Assert.True(load.Succeeded);
        Assert.Null(load.ErrorMessage);
        Assert.True(_data.IsLoaded);
        Assert.Equal(new[] { "Contoso", "Fabrikam" }, _data.Projects.Select(p => p.Name));
        Assert.Equal(new[] { "Contoso.Web-one", "Fabrikam.Web-one" }, _data.Repos.Select(r => r.Name));
        Assert.Equal(4, _data.Areas.Count);
        Assert.Equal(2, _data.Iterations.Count);
    }

    [Fact]
    public async Task Two_callers_during_one_load_share_it_and_nothing_is_listed_twice()
    {
        var hold = _tfs.HoldNext(Projects);
        var first = _data.EnsureLoadedAsync();
        var second = _data.EnsureLoadedAsync();
        await _tfs.StartedAsync(Projects).WaitAsync(TestTimeout);

        hold.SetResult();
        var loads = await Task.WhenAll(first, second).WaitAsync(TestTimeout);

        Assert.All(loads, load => Assert.True(load.Succeeded));
        Assert.Equal(1, _tfs.Reads(Projects));
        Assert.Equal(1, _tfs.Reads(Repos));
        Assert.Equal(2, _tfs.Reads(Areas));
        Assert.Equal(2, _tfs.Reads(Sprints));
        Assert.Equal(4, _data.Areas.Count);
        Assert.Equal(4, _data.Areas.Select(a => a.Path).Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public async Task A_retry_after_a_failure_replaces_the_lists_instead_of_appending()
    {
        _tfs.Failures[FakeTfsMetadata.Key(Sprints, "Fabrikam")] = new HttpRequestException("", null, HttpStatusCode.InternalServerError);
        var failed = await _data.EnsureLoadedAsync().WaitAsync(TestTimeout);
        _tfs.Failures.Clear();

        var retried = await _data.EnsureLoadedAsync().WaitAsync(TestTimeout);

        Assert.False(failed.Succeeded);
        Assert.True(retried.Succeeded);
        Assert.Equal(2, _tfs.Reads(Projects));
        Assert.Equal(4, _data.Areas.Count);
        Assert.Equal(2, _data.Iterations.Count);
        Assert.Equal(2, _data.Repos.Count);
    }

    [Fact]
    public async Task A_failed_list_leaves_IsLoaded_false_and_returns_the_described_error()
    {
        _tfs.Failures[Repos] = new HttpRequestException("", null, HttpStatusCode.Unauthorized);

        var load = await _data.EnsureLoadedAsync().WaitAsync(TestTimeout);

        Assert.Equal(TfsMetadataLoadStatus.Failed, load.Status);
        Assert.Equal($"Couldn't load the repositories: {TokenAdvice}", load.ErrorMessage);
        Assert.False(_data.IsLoaded);
        Assert.Empty(_data.Repos);
        // The lists that did load stay usable.
        Assert.Equal(2, _data.Projects.Count);
        Assert.Equal(4, _data.Areas.Count);
        Assert.Equal(2, _data.Iterations.Count);
    }

    [Fact]
    public async Task An_unknown_failure_is_described_without_its_own_message()
    {
        const string Internals = "parser state 0x2f at https://tfs.example.test/?token=abc";
        _tfs.Failures[FakeTfsMetadata.Key(Areas, "Contoso")] = new InvalidOperationException(Internals);

        var load = await _data.EnsureLoadedAsync().WaitAsync(TestTimeout);

        Assert.Equal(
            $"Couldn't load the areas for Contoso: {TfsErrors.Describe(new InvalidOperationException())}",
            load.ErrorMessage);
        Assert.DoesNotContain("token", load.ErrorMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Each_failure_is_logged_once_at_Error_and_the_page_hears_of_the_first()
    {
        var areas = new HttpRequestException("", null, HttpStatusCode.Forbidden);
        var sprints = new HttpRequestException("", null, HttpStatusCode.BadGateway);
        _tfs.Failures[FakeTfsMetadata.Key(Areas, "Contoso")] = areas;
        _tfs.Failures[FakeTfsMetadata.Key(Sprints, "Fabrikam")] = sprints;

        var load = await _data.EnsureLoadedAsync().WaitAsync(TestTimeout);

        Assert.Equal(
            $"Couldn't load the areas for Contoso: {TfsErrors.Describe(areas)} (1 more list failed. The DevKit log has the details.)",
            load.ErrorMessage);
        var errors = Logged(LogLevel.Error);
        Assert.Equal(2, errors.Count);
        Assert.Same(areas, errors[0].Exception);
        Assert.Same(sprints, errors[1].Exception);
    }

    [Fact]
    public async Task A_failed_project_list_stops_before_repositories()
    {
        _tfs.Failures[Projects] = new HttpRequestException("No such host is known.");

        var load = await _data.EnsureLoadedAsync().WaitAsync(TestTimeout);

        Assert.Equal(
            $"Couldn't load the projects: {TfsErrors.Describe(new HttpRequestException())}", load.ErrorMessage);
        Assert.Equal(0, _tfs.Reads(Repos));
        Assert.Equal(0, _tfs.Reads(Areas));
        Assert.Empty(_data.Projects);
        Assert.Single(Logged(LogLevel.Error));
    }

    [Fact]
    public async Task Leaving_a_page_ends_its_wait_but_not_the_shared_load()
    {
        var hold = _tfs.HoldNext(Projects);
        using var leaving = new CancellationTokenSource();
        var left = _data.EnsureLoadedAsync(leaving.Token);
        var staying = _data.EnsureLoadedAsync();
        await _tfs.StartedAsync(Projects).WaitAsync(TestTimeout);

        await leaving.CancelAsync();
        var leftLoad = await left.WaitAsync(TestTimeout);
        Assert.True(leftLoad.Cancelled);
        Assert.False(staying.IsCompleted);

        hold.SetResult();
        var stayingLoad = await staying.WaitAsync(TestTimeout);

        Assert.True(stayingLoad.Succeeded);
        Assert.True(_data.IsLoaded);
        Assert.Equal(1, _tfs.Reads(Projects));
        Assert.Empty(Logged(LogLevel.Warning, LogLevel.Error));
    }

    [Fact]
    public async Task Disposing_cancels_the_load_without_an_error()
    {
        _tfs.HoldNext(Projects);
        var waiting = _data.EnsureLoadedAsync();
        await _tfs.StartedAsync(Projects).WaitAsync(TestTimeout);

        _data.Dispose();
        await _tfs.CancelledAsync(Projects).WaitAsync(TestTimeout);
        var load = await waiting.WaitAsync(TestTimeout);

        Assert.True(load.Cancelled);
        Assert.False(_data.IsLoaded);
        Assert.Empty(Logged(LogLevel.Warning, LogLevel.Error));
    }

    [Fact]
    public async Task A_load_past_its_ceiling_fails_as_too_slow_and_reads_no_more()
    {
        using var data = new SharedTfsData(_tfs, _log, TimeSpan.FromMilliseconds(200));
        _tfs.HoldNext(Areas);

        var load = await data.EnsureLoadedAsync().WaitAsync(TestTimeout);

        Assert.Equal(
            $"Couldn't load the areas for Contoso: {TooSlow} (3 more lists failed. The DevKit log has the details.)",
            load.ErrorMessage);
        Assert.Equal(1, _tfs.Reads(Areas));
        Assert.Equal(0, _tfs.Reads(Sprints));
        Assert.Single(Logged(LogLevel.Error));
        Assert.False(data.IsLoaded);
        Assert.Equal(2, data.Repos.Count);
    }

    [Fact]
    public async Task Reload_keeps_the_complete_lists_when_the_refresh_fails()
    {
        await _data.EnsureLoadedAsync().WaitAsync(TestTimeout);
        _tfs.Server = "two";
        _tfs.Failures[Repos] = new HttpRequestException("", null, HttpStatusCode.ServiceUnavailable);

        var refresh = await _data.ReloadAsync().WaitAsync(TestTimeout);

        Assert.Equal(TfsMetadataLoadStatus.Failed, refresh.Status);
        Assert.True(_data.IsLoaded);
        Assert.Equal(new[] { "Contoso.Web-one", "Fabrikam.Web-one" }, _data.Repos.Select(r => r.Name));
        Assert.All(_data.Areas, a => Assert.EndsWith("-one", a.Name, StringComparison.Ordinal));
        Assert.Equal(2, _tfs.Reads(Projects));
    }

    [Fact]
    public async Task Invalidate_then_load_replaces_the_previous_server_lists()
    {
        await _data.EnsureLoadedAsync().WaitAsync(TestTimeout);
        _tfs.Server = "two";
        _tfs.ProjectNames = ["Northwind"];

        _data.Invalidate();
        Assert.False(_data.IsLoaded);
        Assert.Empty(_data.Areas);
        var load = await _data.EnsureLoadedAsync().WaitAsync(TestTimeout);

        Assert.True(load.Succeeded);
        Assert.Equal(new[] { "Northwind" }, _data.Projects.Select(p => p.Name));
        Assert.Equal(new[] { "Northwind.Web-two" }, _data.Repos.Select(r => r.Name));
        Assert.All(_data.Areas, a => Assert.EndsWith("-two", a.Name, StringComparison.Ordinal));
        Assert.All(_data.Iterations, i => Assert.EndsWith("-two", i.Name, StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_superseded_load_never_publishes_and_its_waiter_follows_the_new_one()
    {
        _tfs.ProjectNames = ["Contoso"];
        var lastRead = _tfs.HoldNext(Sprints, ignoresCancellation: true);
        var waiter = _data.EnsureLoadedAsync();
        await _tfs.StartedAsync(Sprints).WaitAsync(TestTimeout);

        _tfs.Server = "two";
        var reload = await _data.ReloadAsync().WaitAsync(TestTimeout);
        lastRead.SetResult();
        var followed = await waiter.WaitAsync(TestTimeout);

        Assert.True(reload.Succeeded);
        Assert.True(followed.Succeeded);
        Assert.Equal(2, _tfs.Reads(Sprints));
        Assert.Equal(new[] { "Sprint 1-two" }, _data.Iterations.Select(i => i.Name));
        Assert.All(_data.Areas, a => Assert.EndsWith("-two", a.Name, StringComparison.Ordinal));
    }

    [Fact]
    public async Task Once_loaded_no_further_reads()
    {
        var loads = new List<TfsMetadataLoad>();
        for (var i = 0; i < 3; i++)
        {
            loads.Add(await _data.EnsureLoadedAsync().WaitAsync(TestTimeout));
        }

        Assert.All(loads, load => Assert.True(load.Succeeded));
        Assert.Equal(1, _tfs.Reads(Projects));
        Assert.Equal(1, _tfs.Reads(Repos));
        Assert.Equal(2, _tfs.Reads(Areas));
    }

    [Theory]
    [InlineData(true, true, true)]
    [InlineData(true, false, false)]
    [InlineData(false, true, true)]
    [InlineData(false, false, true)]
    public void A_partial_load_replaces_only_lists_that_are_not_complete(bool currentComplete, bool loadedComplete, bool takesLoaded)
    {
        var current = Snapshot("current", currentComplete);
        var loaded = Snapshot("loaded", loadedComplete);

        var kept = TfsMetadataSnapshot.Choose(current, loaded);

        Assert.Same(takesLoaded ? loaded : current, kept);
    }

    [Fact]
    public void A_load_that_read_nothing_keeps_the_lists_an_earlier_partial_load_read()
    {
        var earlier = Snapshot("earlier", complete: false);

        var kept = TfsMetadataSnapshot.Choose(earlier, TfsMetadataSnapshot.Empty);

        Assert.Same(earlier, kept);
        Assert.Same(TfsMetadataSnapshot.Empty, TfsMetadataSnapshot.Choose(TfsMetadataSnapshot.Empty, TfsMetadataSnapshot.Empty));
    }

    private static TfsMetadataSnapshot Snapshot(string name, bool complete) =>
        new([new DevKit.Web.Models.TfsProject { Id = name, Name = name }], [], [], [], complete);

    private List<(LogLevel Level, Exception? Exception)> Logged(params LogLevel[] levels)
    {
        lock (_log.Entries)
        {
            return _log.Entries.Where(e => levels.Contains(e.Level)).ToList();
        }
    }
}
