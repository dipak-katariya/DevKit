using System.Net;
using System.Net.Http.Headers;
using System.Text;
using DevKit.Web.Services;
using Microsoft.Extensions.Logging;
using Xunit;

namespace DevKit.Tests;

/// <summary>
/// The transport stage every TFS request passes: it queues on the gate, times a request only once it has a
/// slot, keeps the slot until the body is read, never leaks one, and logs a line that names no secret. Each
/// test has its own gate, so the tests can run in parallel with everything else.
/// </summary>
public sealed class TfsTransportHandlerTests
{
    private const string Pat = "transport-test-pat-51c7";
    private const string UserInfo = "devkit-user";
    private const string ContinuationToken = "c2tpcDoxMDAw";
    private const string CacheBuster = "__rc=638650000000000000";
    private const string RefsPath = "/tfs/DefaultCollection/Contoso/_apis/git/repositories/6f1c2d3e/refs";
    private const string Payload = "{\"value\":[]}";

    private static readonly Uri RefsUri = new("https://tfs.example.test" + RefsPath + "?api-version=5.0");
    private static readonly TimeSpan ShortTimeout = TimeSpan.FromMilliseconds(100);
    private static readonly TimeSpan QuietPeriod = TimeSpan.FromMilliseconds(150);
    private static readonly TimeSpan SenderWait = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(10);

    private readonly MessageRecordingLogger<TfsTransportHandler> _log = new();

    [Fact]
    public async Task No_more_requests_are_in_flight_than_the_gate_has_slots()
    {
        using var gate = new TfsGate(2);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var inFlight = 0;
        var most = 0;
        var tfs = new StubTfs(async (_, ct) =>
        {
            var now = Interlocked.Increment(ref inFlight);
            InterlockedMax(ref most, now);
            await release.Task.WaitAsync(ct);
            Interlocked.Decrement(ref inFlight);
            return Reply(HttpStatusCode.OK);
        });
        using var invoker = Transport(gate, tfs);

        var sends = Enumerable.Range(0, 5).Select(_ => invoker.SendAsync(Get(), CancellationToken.None)).ToList();
        await WaitUntilAsync(() => Volatile.Read(ref inFlight) == 2);
        await Task.Delay(QuietPeriod);
        var mostWhileBlocked = Volatile.Read(ref most);
        release.SetResult();
        var responses = await Task.WhenAll(sends);

        Assert.Equal(2, mostWhileBlocked);
        Assert.Equal(2, Volatile.Read(ref most));
        Assert.Equal(5, tfs.Calls);
        Assert.Equal(2, gate.Available);
        DisposeAll(responses);
    }

    [Fact]
    public async Task Time_spent_queued_does_not_count_against_the_request_timeout()
    {
        using var gate = new TfsGate(1);
        var tfs = new StubTfs((_, _) => Task.FromResult(Reply(HttpStatusCode.OK)));
        using var invoker = Transport(gate, tfs, ShortTimeout);

        Task<HttpResponseMessage> send;
        int sentWhileQueued;
        bool doneWhileQueued;
        using (await gate.EnterAsync(CancellationToken.None))
        {
            send = invoker.SendAsync(Get(), CancellationToken.None);
            await Task.Delay(ShortTimeout * 4);
            (sentWhileQueued, doneWhileQueued) = (tfs.Calls, send.IsCompleted);
        }
        using var response = await send.WaitAsync(TestTimeout);

        Assert.Equal(0, sentWhileQueued);
        Assert.False(doneWhileQueued);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(1, gate.Available);
    }

    [Fact]
    public async Task A_reply_slower_than_the_timeout_is_cancelled_by_the_transport_and_frees_its_slot()
    {
        using var gate = new TfsGate(1);
        var tfs = new StubTfs(async (_, ct) =>
        {
            await Task.Delay(Timeout.Infinite, ct);
            return Reply(HttpStatusCode.OK);
        });
        using var invoker = Transport(gate, tfs, ShortTimeout);
        using var caller = new CancellationTokenSource();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => invoker.SendAsync(SecretLadenGet(), caller.Token));

        Assert.False(caller.IsCancellationRequested);
        Assert.Equal(1, gate.Available);
        var entry = Assert.Single(_log.Entries);
        Assert.Equal(LogLevel.Warning, entry.Level);
        Assert.Equal(ShortTimeout.TotalSeconds, entry.Properties["TimeoutSeconds"]);
        Assert.Null(entry.Exception);
        AssertNamesNoSecret(entry);
    }

    /// <summary>
    /// With the client's own timeout off, HttpClient passes the transport's cancellation through untouched, so
    /// the transport must give it the timeout's shape itself; a bare "A task was canceled." reads as a cancel
    /// nobody asked for, and invites a retry of a write TFS may already have applied.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_timeout_reaches_the_caller_as_a_timeout_not_as_a_cancel(bool stallsInBody)
    {
        using var gate = new TfsGate(1);
        var tfs = new StubTfs(async (_, ct) =>
        {
            if (stallsInBody)
            {
                var stalled = new ScriptedContent((_, reading) => Task.Delay(Timeout.Infinite, reading));
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = stalled };
            }
            await Task.Delay(Timeout.Infinite, ct);
            return Reply(HttpStatusCode.OK);
        });
        using var http = Client(gate, tfs, ShortTimeout);
        using var caller = new CancellationTokenSource();

        var ex = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => http.GetAsync(RefsUri, caller.Token));

        Assert.False(caller.IsCancellationRequested);
        Assert.IsType<TimeoutException>(ex.InnerException);
        Assert.Contains("did not answer within", ex.Message, StringComparison.Ordinal);
        Assert.Contains("too long", TfsErrors.Describe(ex), StringComparison.Ordinal);
        Assert.Equal(1, gate.Available);
    }

    [Fact]
    public async Task A_cancel_the_caller_asked_for_is_not_reported_as_a_timeout()
    {
        using var gate = new TfsGate(1);
        var sent = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var tfs = new StubTfs(async (_, ct) =>
        {
            sent.TrySetResult();
            await Task.Delay(Timeout.Infinite, ct);
            return Reply(HttpStatusCode.OK);
        });
        using var http = Client(gate, tfs, TfsTransportHandler.DefaultRequestTimeout);
        using var caller = new CancellationTokenSource();

        var send = http.GetAsync(RefsUri, caller.Token);
        await sent.Task.WaitAsync(TestTimeout);
        await caller.CancelAsync();
        var ex = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => send);

        Assert.DoesNotContain(Chain(ex), e => e is TimeoutException);
        Assert.DoesNotContain("did not answer", ex.Message, StringComparison.Ordinal);
        Assert.Equal(caller.Token, ex.CancellationToken);
        Assert.Equal(1, gate.Available);
        Assert.Equal(LogLevel.Debug, Assert.Single(_log.Entries).Level);
    }

    [Fact]
    public async Task Cancelling_a_queued_request_never_sends_it_and_leaks_no_slot()
    {
        using var gate = new TfsGate(1);
        var tfs = new StubTfs((_, _) => Task.FromResult(Reply(HttpStatusCode.OK)));
        using var invoker = Transport(gate, tfs);
        using var caller = new CancellationTokenSource();

        using (await gate.EnterAsync(CancellationToken.None))
        {
            var send = invoker.SendAsync(Get(), caller.Token);
            await Task.Delay(QuietPeriod);
            await caller.CancelAsync();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => send);
        }

        Assert.Equal(0, tfs.Calls);
        Assert.Equal(1, gate.Available);
        Assert.Equal(LogLevel.Debug, Assert.Single(_log.Entries).Level);
    }

    [Fact]
    public async Task A_request_whose_sender_holds_a_slot_is_not_queued_again()
    {
        using var gate = new TfsGate(1);
        var tfs = new StubTfs((_, _) => Task.FromResult(Reply(HttpStatusCode.OK)));
        using var invoker = Transport(gate, tfs);
        using var held = await gate.EnterAsync(CancellationToken.None);
        using var request = Get();
        TfsGate.MarkHeld(request);

        using var response = await invoker.SendAsync(request, CancellationToken.None).WaitAsync(TestTimeout);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(1, tfs.Calls);
        Assert.Equal(0, gate.Available);
    }

    [Fact]
    public async Task A_held_request_logs_the_wait_its_sender_had_for_the_slot()
    {
        using var gate = new TfsGate(1);
        var tfs = new StubTfs((_, _) => Task.FromResult(Reply(HttpStatusCode.OK)));
        using var invoker = Transport(gate, tfs);
        using var request = Get();
        TfsGate.MarkHeld(request, SenderWait);

        using var response = await invoker.SendAsync(request, CancellationToken.None).WaitAsync(TestTimeout);

        var queuedMs = Assert.IsType<long>(Assert.Single(_log.Entries).Properties["QueuedMs"]);
        Assert.True(queuedMs >= (long)SenderWait.TotalMilliseconds, $"Logged {queuedMs} ms queued.");
    }

    [Fact]
    public async Task The_body_is_read_before_the_slot_is_given_back()
    {
        using var gate = new TfsGate(1);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var body = new ScriptedContent(async (stream, ct) =>
        {
            started.TrySetResult();
            await release.Task.WaitAsync(ct);
            await stream.WriteAsync(Encoding.UTF8.GetBytes(Payload), ct);
        });
        var tfs = new StubTfs((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = body }));
        using var invoker = Transport(gate, tfs);

        var send = invoker.SendAsync(Get(), CancellationToken.None);
        await started.Task.WaitAsync(TestTimeout);
        await Task.Delay(QuietPeriod);
        var (doneWhileReading, freeWhileReading) = (send.IsCompleted, gate.Available);
        release.SetResult();
        using var response = await send.WaitAsync(TestTimeout);

        Assert.False(doneWhileReading);
        Assert.Equal(0, freeWhileReading);
        Assert.Equal(1, gate.Available);
        Assert.Equal(Payload, await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task A_request_that_fails_without_a_reply_gives_its_slot_back()
    {
        using var gate = new TfsGate(1);
        var tfs = new StubTfs((_, _) => throw new HttpRequestException("No route to the TFS server."));
        using var invoker = Transport(gate, tfs);

        await Assert.ThrowsAsync<HttpRequestException>(() => invoker.SendAsync(SecretLadenGet(), CancellationToken.None));

        Assert.Equal(1, gate.Available);
        var entry = Assert.Single(_log.Entries);
        Assert.Equal(LogLevel.Warning, entry.Level);
        Assert.Null(entry.Exception);
        AssertNamesNoSecret(entry);
    }

    [Fact]
    public async Task A_body_that_breaks_off_releases_the_reply_and_the_slot()
    {
        using var gate = new TfsGate(1);
        var body = new ScriptedContent((_, _) => throw new IOException("The connection was reset."));
        var tfs = new StubTfs((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = body }));
        using var invoker = Transport(gate, tfs);

        await Assert.ThrowsAsync<HttpRequestException>(() => invoker.SendAsync(Get(), CancellationToken.None));

        Assert.True(body.Disposed);
        Assert.Equal(1, gate.Available);
    }

    [Theory]
    [InlineData(HttpStatusCode.OK, LogLevel.Debug)]
    [InlineData(HttpStatusCode.NotFound, LogLevel.Warning)]
    [InlineData(HttpStatusCode.InternalServerError, LogLevel.Warning)]
    public async Task Each_request_logs_one_line_with_its_method_path_status_and_timings_and_no_secret(
        HttpStatusCode status, LogLevel level)
    {
        using var gate = new TfsGate(1);
        var tfs = new StubTfs((_, _) => Task.FromResult(Reply(status)));
        using var invoker = Transport(gate, tfs);

        using var response = await invoker.SendAsync(SecretLadenGet(), CancellationToken.None);

        Assert.Equal(status, response.StatusCode);
        var entry = Assert.Single(_log.Entries);
        Assert.Equal(level, entry.Level);
        Assert.Null(entry.Exception);
        Assert.Equal("GET", entry.Properties["Method"]);
        Assert.Equal(RefsPath, entry.Properties["Path"]);
        Assert.Equal((int)status, entry.Properties["Status"]);
        Assert.IsType<long>(entry.Properties["ElapsedMs"]);
        Assert.IsType<long>(entry.Properties["QueuedMs"]);
        AssertNamesNoSecret(entry);
    }

    private HttpMessageInvoker Transport(TfsGate gate, HttpMessageHandler tfs, TimeSpan? timeout = null) =>
        new(new TfsTransportHandler(gate, _log, timeout ?? TfsTransportHandler.DefaultRequestTimeout) { InnerHandler = tfs });

    /// <summary>The transport behind an HttpClient whose own timeout is off, as the app composes it.</summary>
    private HttpClient Client(TfsGate gate, HttpMessageHandler tfs, TimeSpan timeout) =>
        new(new TfsTransportHandler(gate, _log, timeout) { InnerHandler = tfs }) { Timeout = Timeout.InfiniteTimeSpan };

    private static HttpRequestMessage Get() => new(HttpMethod.Get, RefsUri);

    /// <summary>A request carrying everything a log line must leave out: query, user info and the token.</summary>
    private static HttpRequestMessage SecretLadenGet()
    {
        var request = new HttpRequestMessage(HttpMethod.Get,
            $"https://{UserInfo}@tfs.example.test{RefsPath}?filter=heads&continuationToken={ContinuationToken}&api-version=5.0&{CacheBuster}");
        request.Headers.Authorization = new AuthenticationHeaderValue("Basic", BasicToken);
        return request;
    }

    private static string BasicToken => Convert.ToBase64String(Encoding.ASCII.GetBytes(":" + Pat));

    private static void AssertNamesNoSecret(MessageRecordingLogger<TfsTransportHandler>.Entry entry)
    {
        var text = entry.AllText;
        foreach (var secret in new[] { "filter=heads", "api-version", "continuationToken", ContinuationToken, "__rc", UserInfo,
                     "Authorization", "Basic ", Pat, BasicToken })
        {
            Assert.DoesNotContain(secret, text, StringComparison.OrdinalIgnoreCase);
        }
    }

    private static IEnumerable<Exception> Chain(Exception ex)
    {
        for (Exception? link = ex; link is not null; link = link.InnerException)
        {
            yield return link;
        }
    }

    private static HttpResponseMessage Reply(HttpStatusCode status) =>
        new(status) { Content = new StringContent(Payload, Encoding.UTF8, "application/json") };

    private static void InterlockedMax(ref int target, int value)
    {
        var seen = Volatile.Read(ref target);
        while (value > seen)
        {
            var previous = Interlocked.CompareExchange(ref target, value, seen);
            if (previous == seen)
            {
                return;
            }
            seen = previous;
        }
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        using var limit = new CancellationTokenSource(TestTimeout);
        while (!condition() && !limit.IsCancellationRequested)
        {
            await Task.Delay(10);
        }
        Assert.True(condition());
    }

    private static void DisposeAll(IEnumerable<HttpResponseMessage> responses)
    {
        foreach (var response in responses)
        {
            response.Dispose();
        }
    }
}
