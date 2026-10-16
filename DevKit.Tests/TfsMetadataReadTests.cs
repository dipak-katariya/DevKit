using System.Net;
using DevKit.Web.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DevKit.Tests;

/// <summary>
/// B075: the repository, area and sprint reads behind every picker report a failure instead of answering with an
/// empty list, which read as a server with nothing on it and was kept as loaded for the whole session. They leave
/// the logging to <see cref="SharedTfsData"/>, which logs each failure once.
/// </summary>
[Collection(SettingsFileCollection.Name)]
public sealed class TfsMetadataReadTests : IDisposable
{
    private const string CollectionUrl = "https://tfs.example.test/tfs/DefaultCollection";
    private const string Project = "Contoso";
    private const string Refused = """{"message":"TF400813: The user is not authorized to access this resource."}""";
    private const string Broken = """{"message":"Internal error."}""";
    private const string OneProject = """{"count":1,"value":[{"id":"c0","name":"Contoso"}]}""";
    private const string OneRepository = """{"count":1,"value":[{"id":"r1","name":"Contoso.Web","project":{"name":"Contoso"}}]}""";
    private const string OneSprint = """{"id":7,"name":"Contoso","path":"\\Contoso\\Iteration"}""";

    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(10);

    private static readonly Dictionary<string, Func<TfsApiService, CancellationToken, Task>> Reads = new()
    {
        ["repositories"] = (api, ct) => api.GetRepositoriesAsync(ct),
        ["areas"] = (api, ct) => api.GetAreasAsync(Project, ct),
        ["sprints"] = (api, ct) => api.GetIterationsAsync(Project, ct)
    };

    private readonly RecordingTfsHandler _tfs = new();
    private readonly HttpClient _http;
    private readonly SettingsService _settings = new(NullLogger<SettingsService>.Instance);
    private readonly RecordingLogger<TfsApiService> _log = new();
    private readonly TfsApiService _api;

    public TfsMetadataReadTests()
    {
        _http = new HttpClient(_tfs, disposeHandler: false);
        _settings.Tfs.Url = CollectionUrl;
        _api = new TfsApiService(_http, _settings, _log);
    }

    public static TheoryData<string> ReadNames => new(Reads.Keys);

    public static TheoryData<string, HttpStatusCode> Failures
    {
        get
        {
            var data = new TheoryData<string, HttpStatusCode>();
            foreach (var read in Reads.Keys)
            {
                data.Add(read, HttpStatusCode.Unauthorized);
                data.Add(read, HttpStatusCode.InternalServerError);
            }
            return data;
        }
    }

    public void Dispose()
    {
        _http.Dispose();
        _tfs.Dispose();
    }

    [Theory]
    [MemberData(nameof(Failures))]
    public async Task A_refused_or_failed_read_throws_instead_of_answering_empty(string read, HttpStatusCode status)
    {
        _tfs.Reply(status, status == HttpStatusCode.Unauthorized ? Refused : Broken);

        var failure = await Assert.ThrowsAsync<HttpRequestException>(() => Reads[read](_api, CancellationToken.None));

        Assert.Equal(status, failure.StatusCode);
        Assert.Single(_tfs.Sent);
        lock (_log.Entries)
        {
            Assert.Empty(_log.Entries);
        }
    }

    [Fact]
    public async Task The_shared_loader_shows_a_failed_area_read_once_and_keeps_the_other_lists()
    {
        _tfs.Respond = sent => sent.Path switch
        {
            var path when path.EndsWith("/_apis/projects", StringComparison.Ordinal) => (HttpStatusCode.OK, OneProject),
            var path when path.EndsWith("/_apis/git/repositories", StringComparison.Ordinal) => (HttpStatusCode.OK, OneRepository),
            var path when path.EndsWith("/classificationnodes/areas", StringComparison.Ordinal) => (HttpStatusCode.InternalServerError, Broken),
            _ => (HttpStatusCode.OK, OneSprint)
        };
        var loaderLog = new RecordingLogger<SharedTfsData>();
        using var data = new SharedTfsData(_api, loaderLog);

        var load = await data.EnsureLoadedAsync().WaitAsync(TestTimeout);

        var serverFailure = TfsErrors.Describe(new HttpRequestException("", null, HttpStatusCode.InternalServerError));
        Assert.Equal($"Couldn't load the areas for {Project}: {serverFailure}", load.ErrorMessage);
        Assert.False(data.IsLoaded);
        Assert.Empty(data.Areas);
        Assert.Single(data.Repos);
        Assert.Single(data.Iterations);
        lock (loaderLog.Entries)
        {
            Assert.Single(loaderLog.Entries, e => e.Level == LogLevel.Error);
        }
        lock (_log.Entries)
        {
            Assert.Empty(_log.Entries);
        }
    }

    [Theory]
    [MemberData(nameof(ReadNames))]
    public async Task A_read_cancelled_while_TFS_answers_throws_instead_of_answering_empty(string read)
    {
        var sent = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var tfs = new StubTfs(async (_, ct) =>
        {
            sent.TrySetResult();
            await Task.Delay(Timeout.Infinite, ct);
            throw new InvalidOperationException("Only cancellation ends this wait.");
        });
        using var http = new HttpClient(tfs, disposeHandler: false);
        var api = new TfsApiService(http, _settings, _log);
        using var leaving = new CancellationTokenSource();

        var running = Reads[read](api, leaving.Token);
        await sent.Task.WaitAsync(TestTimeout);
        await leaving.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => running.WaitAsync(TestTimeout));
        Assert.Equal(1, tfs.Calls);
        lock (_log.Entries)
        {
            Assert.Empty(_log.Entries);
        }
    }
}
