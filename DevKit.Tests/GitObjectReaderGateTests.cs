using System.Net;
using System.Text;
using DevKit.Web.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DevKit.Tests;

/// <summary>
/// A file's bytes are the one TFS read streamed past the transport, so the read takes its gate slot itself
/// and keeps it until the last byte is in; every other read waits meanwhile. Each test has a private
/// one-slot gate.
/// </summary>
[Collection(SettingsFileCollection.Name)]
public sealed class GitObjectReaderGateTests
{
    private const string CollectionUrl = "https://tfs.example.test/tfs/DefaultCollection";
    private const string Project = "Contoso";
    private const string RepoId = "6f1c2d3e-4a5b-4c6d-8e7f-90a1b2c3d4e5";
    private const string BlobId = "0123456789abcdef0123456789abcdef01234567";
    private const string TipSha = "89abcdef0123456789abcdef0123456789abcdef";
    private const string FileText = "public static class Totals { }";
    private const long BlobCacheBytes = 1024 * 1024;
    private const long Unlimited = 1024 * 1024;

    private static readonly TimeSpan QuietPeriod = TimeSpan.FromMilliseconds(150);
    private static readonly TimeSpan TransportTimeout = TimeSpan.FromMilliseconds(100);
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(10);

    private readonly SettingsService _settings = new(NullLogger<SettingsService>.Instance);
    private readonly MessageRecordingLogger<TfsTransportHandler> _log = new();

    public GitObjectReaderGateTests()
    {
        _settings.Tfs.Url = CollectionUrl;
    }

    [Fact]
    public async Task A_file_read_keeps_its_slot_until_the_body_is_in_and_other_reads_wait_for_it()
    {
        using var gate = new TfsGate(1);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var refsCalls = 0;
        using var tfs = new StubTfs((request, _) =>
        {
            if (IsBlob(request))
            {
                return Task.FromResult(Body(new ScriptedContent(async (stream, ct) =>
                {
                    started.TrySetResult();
                    await release.Task.WaitAsync(ct);
                    await stream.WriteAsync(Encoding.UTF8.GetBytes(FileText), ct);
                })));
            }
            Interlocked.Increment(ref refsCalls);
            return Task.FromResult(Body(new StringContent(
                $$"""{"value":[{"name":"refs/heads/main","objectId":"{{TipSha}}"}]}""", Encoding.UTF8, "application/json")));
        });
        using var http = Pipeline(gate, tfs);
        var reader = new GitObjectReader(new TfsRequestClient(http, _settings, gate), BlobCacheBytes);

        var file = reader.ReadBlobAsync(Project, RepoId, BlobId, Unlimited, CancellationToken.None);
        await started.Task.WaitAsync(TestTimeout);
        var tip = reader.BranchTipAsync(Project, RepoId, "main", CancellationToken.None);
        await Task.Delay(QuietPeriod);
        var (refsSentWhileReading, freeWhileReading) = (Volatile.Read(ref refsCalls), gate.Available);
        release.SetResult();
        var bytes = await file.WaitAsync(TestTimeout);
        var head = await tip.WaitAsync(TestTimeout);

        Assert.Equal(0, refsSentWhileReading);
        Assert.Equal(0, freeWhileReading);
        Assert.Equal(FileText, Encoding.UTF8.GetString(bytes!));
        Assert.Equal(TipSha, head);
        Assert.Equal(1, gate.Available);
        Assert.Equal(2, reader.RequestCount);
    }

    [Fact]
    public async Task A_file_body_is_timed_by_the_read_itself_not_by_the_transport()
    {
        using var gate = new TfsGate(1);
        using var tfs = new StubTfs((_, _) => Task.FromResult(Body(new ScriptedContent(async (stream, ct) =>
        {
            await Task.Delay(TransportTimeout * 3, ct);
            await stream.WriteAsync(Encoding.UTF8.GetBytes(FileText), ct);
        }))));
        using var http = Pipeline(gate, tfs, TransportTimeout);
        var reader = new GitObjectReader(new TfsRequestClient(http, _settings, gate), BlobCacheBytes);

        var bytes = await reader.ReadBlobAsync(Project, RepoId, BlobId, Unlimited, CancellationToken.None).WaitAsync(TestTimeout);

        Assert.Equal(FileText, Encoding.UTF8.GetString(bytes!));
        Assert.Equal(1, gate.Available);
    }

    /// <summary>A file read queues for its slot before the transport sees it, so the wait is carried to the log line.</summary>
    [Fact]
    public async Task A_file_read_that_waited_for_its_slot_logs_the_wait()
    {
        using var gate = new TfsGate(1);
        using var tfs = new StubTfs((_, _) => Task.FromResult(Body(new StringContent(FileText))));
        using var http = Pipeline(gate, tfs);
        var reader = new GitObjectReader(new TfsRequestClient(http, _settings, gate), BlobCacheBytes);

        Task<byte[]?> file;
        using (await gate.EnterAsync(CancellationToken.None))
        {
            file = reader.ReadBlobAsync(Project, RepoId, BlobId, Unlimited, CancellationToken.None);
            await Task.Delay(QuietPeriod);
        }
        var bytes = await file.WaitAsync(TestTimeout);

        Assert.Equal(FileText, Encoding.UTF8.GetString(bytes!));
        var queuedMs = Assert.IsType<long>(Assert.Single(_log.Entries).Properties["QueuedMs"]);
        Assert.True(queuedMs >= (long)(QuietPeriod / 2).TotalMilliseconds, $"Logged {queuedMs} ms queued.");
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_file_over_the_limit_reads_as_null_and_gives_its_slot_back(bool declaresLength)
    {
        using var gate = new TfsGate(1);
        var content = Encoding.UTF8.GetBytes(FileText);
        using var tfs = new StubTfs((_, _) => Task.FromResult(Body(declaresLength
            ? new ByteArrayContent(content)
            : new ScriptedContent((stream, ct) => stream.WriteAsync(content, ct).AsTask()))));
        using var http = Pipeline(gate, tfs);
        var reader = new GitObjectReader(new TfsRequestClient(http, _settings, gate), BlobCacheBytes);

        var bytes = await reader.ReadBlobAsync(Project, RepoId, BlobId, content.Length - 1, CancellationToken.None)
            .WaitAsync(TestTimeout);

        Assert.Null(bytes);
        Assert.Equal(1, tfs.Calls);
        Assert.Equal(1, gate.Available);
    }

    /// <summary>The app's own transport in front of the stand-in, with the client's timeout off as the app sets it.</summary>
    private HttpClient Pipeline(TfsGate gate, HttpMessageHandler tfs, TimeSpan? transportTimeout = null)
    {
        var transport = new TfsTransportHandler(gate, _log, transportTimeout ?? TfsTransportHandler.DefaultRequestTimeout)
        {
            InnerHandler = tfs
        };
        return new HttpClient(transport) { Timeout = Timeout.InfiniteTimeSpan };
    }

    private static bool IsBlob(HttpRequestMessage request) =>
        request.RequestUri!.AbsolutePath.Contains("/blobs/", StringComparison.Ordinal);

    private static HttpResponseMessage Body(HttpContent content) => new(HttpStatusCode.OK) { Content = content };
}
