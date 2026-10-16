using System.Diagnostics;
using System.Net;

namespace DevKit.Web.Services;

/// <summary>
/// The one process-wide limit on concurrent TFS calls: the Code Merging Sheet, Code Compare and the
/// parallel reads of Sprint Insights all share it. TFS throttles aggressively and a large sprint would otherwise
/// open hundreds of parallel calls; a second user loading a page shares the same slots by design.
/// </summary>
public sealed class TfsGate : IDisposable
{
    /// <summary>Concurrent TFS calls allowed across the whole process.</summary>
    public const int MaxParallelRequests = 5;

    private static readonly HttpRequestOptionsKey<TimeSpan> HeldOption = new("DevKit.TfsGate.Held");

    private readonly SemaphoreSlim _slots;

    internal TfsGate(int slots)
    {
        _slots = new SemaphoreSlim(slots, slots);
    }

    /// <summary>The gate every TFS request shares. DI hands out this same instance, never a new one.</summary>
    public static TfsGate Shared { get; } = new(MaxParallelRequests);

    /// <summary>Slots free right now.</summary>
    internal int Available => _slots.CurrentCount;

    /// <summary>Waits for a slot. Disposing the result gives the slot back, once, however often it is disposed.</summary>
    public async Task<IDisposable> EnterAsync(CancellationToken ct)
    {
        await _slots.WaitAsync(ct).ConfigureAwait(false);
        return new Slot(_slots);
    }

    /// <summary>
    /// Says the sender already holds a slot for this request, so the transport must not queue it again: a
    /// second wait while holding a slot would deadlock once every slot was held that way.
    /// <paramref name="queued"/> is how long the sender waited for it, so the request's log line still shows
    /// the wait.
    /// </summary>
    internal static void MarkHeld(HttpRequestMessage request, TimeSpan queued = default) =>
        request.Options.Set(HeldOption, queued);

    /// <summary>Whether the sender holds the request's slot, and how long it queued for it.</summary>
    internal static bool IsHeld(HttpRequestMessage request, out TimeSpan queued) =>
        request.Options.TryGetValue(HeldOption, out queued);

    public void Dispose() => _slots.Dispose();

    private sealed class Slot : IDisposable
    {
        private SemaphoreSlim? _owner;

        public Slot(SemaphoreSlim owner)
        {
            _owner = owner;
        }

        public void Dispose() => Interlocked.Exchange(ref _owner, null)?.Release();
    }
}

/// <summary>
/// The HTTP stage every TFS request passes through. It waits for a <see cref="TfsGate"/> slot, then starts
/// the request's own timeout, so time spent queued behind a large sheet never counts against it. It
/// buffers the reply's body before giving the slot back, so the gate covers the whole download, and writes
/// one log line per request. It never reads or changes the request: auth, the URL and the body stay
/// <see cref="TfsRequestClient"/>'s business, which lets a connection test send a token not yet saved.
/// </summary>
public sealed class TfsTransportHandler : DelegatingHandler
{
    /// <summary>What HttpClient allowed each request before the transport owned the timeout.</summary>
    public static readonly TimeSpan DefaultRequestTimeout = TimeSpan.FromSeconds(100);

    private readonly TfsGate _gate;
    private readonly ILogger<TfsTransportHandler> _logger;
    private readonly TimeSpan _timeout;

    public TfsTransportHandler(TfsGate gate, ILogger<TfsTransportHandler> logger)
        : this(gate, logger, DefaultRequestTimeout)
    {
    }

    internal TfsTransportHandler(TfsGate gate, ILogger<TfsTransportHandler> logger, TimeSpan timeout)
    {
        _gate = gate;
        _logger = logger;
        _timeout = timeout;
    }

    /// <summary>The gate this handler waits on. The factory rebuilds handlers every few minutes, so it must be the shared one.</summary>
    internal TfsGate Gate => _gate;

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var exchange = new Exchange(request);
        try
        {
            using var slot = exchange.Held ? null : await _gate.EnterAsync(cancellationToken).ConfigureAwait(false);
            exchange.SlotGranted();
            using var deadline = new Deadline(_timeout, cancellationToken);
            try
            {
                var sending = base.SendAsync(request, deadline.Token);
                await deadline.AwaitAsync(sending).ConfigureAwait(false);
                var response = await sending.ConfigureAwait(false);
                if (slot is not null)
                {
                    await BufferOrDisposeAsync(response, deadline).ConfigureAwait(false);
                }
                exchange.Answered(response);
                return response;
            }
            finally
            {
                exchange.TimedOut = deadline.Expired;
            }
        }
        finally
        {
            Log(exchange, cancellationToken.IsCancellationRequested);
        }
    }

    /// <summary>A reply that is not handed back is released here; the caller only ever sees the exception.</summary>
    private static async Task BufferOrDisposeAsync(HttpResponseMessage response, Deadline deadline)
    {
        var buffered = false;
        try
        {
            await deadline.AwaitAsync(response.Content.LoadIntoBufferAsync(deadline.Token)).ConfigureAwait(false);
            buffered = true;
        }
        finally
        {
            if (!buffered)
            {
                response.Dispose();
            }
        }
    }

    /// <summary>
    /// One line per request, without the exception: whoever catches the failure logs it once at Error. Only
    /// the path is written, because the query can carry continuation tokens and the authority a user name.
    /// </summary>
    private void Log(Exchange exchange, bool callerCancelled)
    {
        if (exchange.Status is { } status)
        {
            _logger.Log(exchange.Succeeded ? LogLevel.Debug : LogLevel.Warning,
                "TFS {Method} {Path} answered {Status} in {ElapsedMs} ms after {QueuedMs} ms queued",
                exchange.Method, exchange.Path, (int)status, exchange.ElapsedMs, exchange.QueuedMs);
        }
        else if (exchange.TimedOut)
        {
            _logger.LogWarning(
                "TFS {Method} {Path} timed out after {TimeoutSeconds} s, having queued {QueuedMs} ms",
                exchange.Method, exchange.Path, _timeout.TotalSeconds, exchange.QueuedMs);
        }
        else if (callerCancelled)
        {
            _logger.LogDebug(
                "TFS {Method} {Path} was cancelled after {ElapsedMs} ms, having queued {QueuedMs} ms",
                exchange.Method, exchange.Path, exchange.ElapsedMs, exchange.QueuedMs);
        }
        else
        {
            _logger.LogWarning(
                "TFS {Method} {Path} failed without a reply after {ElapsedMs} ms, having queued {QueuedMs} ms",
                exchange.Method, exchange.Path, exchange.ElapsedMs, exchange.QueuedMs);
        }
    }

    /// <summary>
    /// The request's own time limit, started once it has a slot. With the client's timeout off, HttpClient
    /// passes this limit's cancellation on as a bare "A task was canceled.", which reads as a cancel the
    /// caller asked for and invites retrying a write TFS may already have applied. So the timeout is given
    /// the shape HttpClient's own had.
    /// </summary>
    private sealed class Deadline : IDisposable
    {
        private readonly CancellationTokenSource _source;
        private readonly CancellationToken _caller;
        private readonly TimeSpan _limit;

        public Deadline(TimeSpan limit, CancellationToken caller)
        {
            _source = CancellationTokenSource.CreateLinkedTokenSource(caller);
            _source.CancelAfter(limit);
            _caller = caller;
            _limit = limit;
        }

        public CancellationToken Token => _source.Token;

        /// <summary>The limit ran out while the caller still wanted the answer.</summary>
        public bool Expired => _source.IsCancellationRequested && !_caller.IsCancellationRequested;

        /// <summary>
        /// Waits for one step of the exchange. A step cancelled by the limit alone ends in a cancellation that
        /// names the limit, with a <see cref="TimeoutException"/> inside; any other outcome is the step's own.
        /// </summary>
        public async Task AwaitAsync(Task step)
        {
            await step.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
            if (step.IsCanceled && Expired)
            {
                throw new TaskCanceledException(
                    $"TFS did not answer within {_limit.TotalSeconds:0.#} s.", new TimeoutException(), _source.Token);
            }
            await step.ConfigureAwait(false);
        }

        public void Dispose() => _source.Dispose();
    }

    /// <summary>What the log line reports about one request, gathered as it goes.</summary>
    private sealed class Exchange
    {
        private readonly long _started = Stopwatch.GetTimestamp();
        private readonly TimeSpan _queuedBySender;
        private long? _granted;

        public Exchange(HttpRequestMessage request)
        {
            Method = request.Method.Method;
            Path = request.RequestUri is { IsAbsoluteUri: true } uri ? uri.AbsolutePath : "";
            Held = TfsGate.IsHeld(request, out _queuedBySender);
        }

        public string Method { get; }
        public string Path { get; }

        /// <summary>The sender took the slot itself, so the transport must not queue the request again.</summary>
        public bool Held { get; }

        public HttpStatusCode? Status { get; private set; }
        public bool Succeeded { get; private set; }
        public bool TimedOut { get; set; }

        /// <summary>
        /// Time spent waiting for a slot, or waiting so far when none was granted. A held request reports the
        /// wait its sender had, which is where it queued.
        /// </summary>
        public long QueuedMs =>
            (long)(_queuedBySender + Stopwatch.GetElapsedTime(_started, _granted ?? Stopwatch.GetTimestamp())).TotalMilliseconds;

        /// <summary>Time since the slot was granted; zero when the request never got one.</summary>
        public long ElapsedMs => _granted is { } granted ? Milliseconds(granted, Stopwatch.GetTimestamp()) : 0;

        public void SlotGranted() => _granted = Stopwatch.GetTimestamp();

        public void Answered(HttpResponseMessage response)
        {
            Status = response.StatusCode;
            Succeeded = response.IsSuccessStatusCode;
        }

        private static long Milliseconds(long from, long to) => (long)Stopwatch.GetElapsedTime(from, to).TotalMilliseconds;
    }
}

/// <summary>The composition root for everything that talks to TFS, shared by Program.cs and the tests.</summary>
public static class TfsServiceCollectionExtensions
{
    /// <summary>
    /// Registers <see cref="TfsRequestClient"/> as the typed HTTP client for PAT sign-in, and
    /// <see cref="TfsRequestClient.WindowsSignInClient"/> beside it for Windows sign-in, both behind
    /// <see cref="TfsTransportHandler"/>, and the services built on them. Their own timeout is off because it
    /// would start before the request queues for a slot; the handler times each request from the moment it has
    /// one. The factory's own loggers are removed: the handler writes the one line per request, without the
    /// query. <see cref="SettingsService"/> is registered by the caller, as everything else uses it too.
    /// </summary>
    public static IServiceCollection AddTfsServices(this IServiceCollection services)
    {
        services.AddSingleton(TfsGate.Shared);
        services.AddTransient<TfsTransportHandler>();
        services.AddHttpClient<TfsRequestClient>().AsTfsClient();
        services.AddHttpClient(TfsRequestClient.WindowsSignInClient).AsTfsClient()
            .ConfigurePrimaryHttpMessageHandler((primary, _) => SignInAsWindowsAccount(primary));
        services.AddTransient<TfsApiService>();
        services.AddTransient<CodeMergingService>();
        services.AddTransient<CodeCompareService>();
        services.AddTransient<MergeVerificationService>();
        services.AddScoped<ITfsMetadataSource>(sp => sp.GetRequiredService<TfsApiService>());
        services.AddScoped<SharedTfsData>();
        return services;
    }

    private static IHttpClientBuilder AsTfsClient(this IHttpClientBuilder client) => client
        .ConfigureHttpClient(http => http.Timeout = Timeout.InfiniteTimeSpan)
        .AddHttpMessageHandler<TfsTransportHandler>()
        .RemoveAllLoggers();

    /// <summary>
    /// Lets the factory's own handler answer TFS's Negotiate/NTLM challenge as the Windows account DevKit runs as.
    /// The handler sends no credentials on a redirect, so they only reach the server the request was sent to.
    /// A handler of another kind, such as a test's stand-in for TFS, is left as it is.
    /// </summary>
    private static void SignInAsWindowsAccount(HttpMessageHandler primary)
    {
        if (primary is SocketsHttpHandler sockets)
        {
            sockets.Credentials = CredentialCache.DefaultCredentials;
        }
    }
}
