using System.Diagnostics;
using System.Globalization;
using System.IO.Pipes;
using System.Net;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.Options;

namespace DevKit.Web.Services;

/// <summary>
/// Hands DevKit's port over during an update. Each switch (<see cref="DevKitSwitch"/>) gives the DevKit it starts a run
/// id of its own (<see cref="RunVariable"/>). Whatever holds the port for a run, the switch's maintenance page and then
/// that DevKit, listens on a named pipe for its port and run, made once it listens, and makes way when asked there: the
/// switch's DevKit asks its maintenance page as it is about to listen, and the next switch asks that DevKit. A DevKit of
/// an earlier run, started again by its own keep-alive script, finds no pipe of its run, so it neither stops anyone nor
/// waits for the port. A pipe is seen from every Windows session, so a switch run over remote desktop reaches a DevKit
/// that started at boot or in another session; only the account that made it may connect, and only to a pipe of its own.
/// </summary>
internal static class PortHandover
{
    /// <summary>The environment variable the scripts set to DevKit's port.</summary>
    public const string PortVariable = "DEVKIT_PORT";

    /// <summary>The environment variable a switch sets to the id of its run, for the DevKit it starts.</summary>
    public const string RunVariable = "DEVKIT_RUN_ID";

    /// <summary>
    /// How long a newcomer waits for the port to be free once its holder was asked to make way: a program that has
    /// stopped can keep its port a moment longer.
    /// </summary>
    public static readonly TimeSpan PortWait = TimeSpan.FromSeconds(60);

    private const int MaxRunLength = 40;

    private const string PipePrefix = @"\\.\pipe\";
    private const string PortPipePrefix = "DevKit.Port.";

    // What Windows answers for a pipe that exists while all its instances are busy.
    private const int PipeBusy = 121;

    // Short, since the port is closed for as long as it is free and the newcomer has not noticed.
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(25);

    // A holder answers at once; this only covers the moment it takes between two connections.
    private static readonly TimeSpan ConnectWait = TimeSpan.FromSeconds(2);

    /// <summary>The port in <see cref="PortVariable"/>; null when it is not set or names no port.</summary>
    public static int? ConfiguredPort() =>
        int.TryParse(Environment.GetEnvironmentVariable(PortVariable), NumberStyles.None, CultureInfo.InvariantCulture, out var port)
        && port is > IPEndPoint.MinPort and <= IPEndPoint.MaxPort
            ? port
            : null;

    /// <summary>The run id in <see cref="RunVariable"/>; null when it is not set or is not one (<see cref="RunFrom"/>).</summary>
    public static string? ConfiguredRun() => RunFrom(Environment.GetEnvironmentVariable(RunVariable));

    /// <summary>
    /// <paramref name="value"/> when it can be a run id: letters and digits only, since it becomes part of an event's
    /// name; otherwise null.
    /// </summary>
    internal static string? RunFrom(string? value) =>
        value is { Length: > 0 and <= MaxRunLength } && value.All(char.IsAsciiLetterOrDigit) ? value : null;

    /// <summary>A run id no other switch has: switches started in the same second each get their own.</summary>
    public static string NewRun() => Guid.NewGuid().ToString("N");

    /// <summary>
    /// Makes DevKit's web server take <paramref name="port"/> over from the holder of <paramref name="run"/>
    /// (<see cref="TakeOverAsync"/>) as it starts: when the request pipeline is already built and it is about to listen,
    /// so the port stays closed for a moment only, where a cold start would otherwise keep it closed for a second or more.
    /// </summary>
    public static void AddTakeOver(IServiceCollection services, int port, string? run) =>
        AddBeforeListening(services, (logger, cancellationToken) => TakeOverAsync(port, run, PortWait, logger, cancellationToken));

    /// <summary>
    /// Makes the DevKit that holds <paramref name="port"/> for <paramref name="run"/> make way when the next switch asks:
    /// once it listens it waits on its run's pipe, and when asked there it stops, which closes its port at once and lets
    /// the requests in progress finish. The maintenance page it took the port from had a pipe of the same run, which it
    /// lets go once asked; this waits for that first.
    /// </summary>
    public static void MakeWayWhenAsked(WebApplication app, int port, string run)
    {
        ArgumentNullException.ThrowIfNull(app);
        HandoverRequests? requests = null;
        app.Lifetime.ApplicationStarted.Register(() => _ = ListenForNextSwitchAsync(app, port, run, made => requests = made));
        app.Lifetime.ApplicationStopping.Register(() => requests?.Dispose());
    }

    private static async Task ListenForNextSwitchAsync(WebApplication app, int port, string run, Action<HandoverRequests> made)
    {
        var name = PipeName(port, run);
        var stopping = app.Lifetime.ApplicationStopping;
        await WaitUntilAsync(() => !PipeExists(name) || stopping.IsCancellationRequested, PortWait, CancellationToken.None);
        if (stopping.IsCancellationRequested)
        {
            return;
        }

        try
        {
            var requests = new HandoverRequests(port, run);
            made(requests);
            await StopWhenAskedAsync(app, requests, port);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            app.Logger.LogError(ex, "This DevKit cannot be asked to make way on port {Port}, so the next switch stops it instead", port);
        }
    }

    /// <summary>
    /// Asks the holder of <paramref name="run"/> on <paramref name="port"/>, if there is one, to make way, then waits for
    /// the port (<see cref="WaitForFreePortAsync"/>). Without a holder it returns at once, so a DevKit that no switch is
    /// waiting for fails to listen on a port in use, as it always did, and takes nothing from anyone.
    /// </summary>
    public static async Task TakeOverAsync(int port, string? run, TimeSpan wait, ILogger logger, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(logger);
        if (AskToMakeWay(port, run, logger))
        {
            await WaitForFreePortAsync(port, wait, logger, cancellationToken);
        }
    }

    /// <summary>
    /// Waits up to <paramref name="wait"/> while <paramref name="port"/> is in use; returns at once when it is free. A
    /// port still in use after that is logged as an Error; listening on it then fails as it would have without the wait.
    /// </summary>
    public static async Task WaitForFreePortAsync(int port, TimeSpan wait, ILogger logger, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(logger);
        try
        {
            if (!await WaitUntilAsync(() => !IsListening(port), wait, cancellationToken))
            {
                logger.LogError("Port {Port} is still in use after {Seconds} seconds, so this DevKit cannot listen on it",
                    port, wait.TotalSeconds);
            }
        }
        catch (NetworkInformationException ex)
        {
            logger.LogError(ex, "Windows did not say whether port {Port} is in use; DevKit tries to listen on it anyway", port);
        }
    }

    /// <summary>Checks <paramref name="condition"/> until it holds, for up to <paramref name="wait"/>; whether it did.</summary>
    internal static async Task<bool> WaitUntilAsync(Func<bool> condition, TimeSpan wait, CancellationToken cancellationToken)
    {
        var waited = Stopwatch.StartNew();
        while (!condition())
        {
            if (waited.Elapsed >= wait)
            {
                return false;
            }

            await Task.Delay(PollInterval, cancellationToken);
        }

        return true;
    }

    /// <summary>Whether a program listens on <paramref name="port"/> on any address.</summary>
    internal static bool IsListening(int port) =>
        IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners().Any(endpoint => endpoint.Port == port);

    /// <summary>Whether something holds <paramref name="port"/> for <paramref name="run"/>: it made its pipe once it listened.</summary>
    internal static bool HolderListens(int port, string run) => PipeExists(PipeName(port, run));

    /// <summary>
    /// Asks the holder of <paramref name="run"/> on <paramref name="port"/> to make way; false when there is none, or when
    /// the pipe there is not one this account made.
    /// </summary>
    internal static bool AskToMakeWay(int port, string? run, ILogger logger)
    {
        var name = PipeName(port, run);
        if (!PipeExists(name))
        {
            return false;
        }

        try
        {
            // CurrentUserOnly: the connection fails unless this account made the pipe, so nobody else hears the request.
            using var holder = new NamedPipeClientStream(".", name, PipeDirection.Out, PipeOptions.CurrentUserOnly);
            holder.Connect(ConnectWait);
            holder.WriteByte(HandoverRequests.MakeWay);
            holder.Flush();
            logger.LogInformation("Asked the holder of port {Port} to make way", port);
            return true;
        }
        catch (Exception ex) when (ex is TimeoutException or IOException or UnauthorizedAccessException)
        {
            logger.LogError(ex, "The holder of port {Port} could not be asked to make way; it may run as another Windows account", port);
            return false;
        }
    }

    internal static string PipeName(int port, string? run) =>
        PortPipePrefix + port.ToString(CultureInfo.InvariantCulture) + (run is null ? "" : "." + run);

    /// <summary>Whether a pipe named <paramref name="name"/> exists, asked without connecting to it.</summary>
    internal static bool PipeExists(string name) =>
        OperatingSystem.IsWindows()
        && (WaitNamedPipe(PipePrefix + name, 1) || Marshal.GetLastPInvokeError() == PipeBusy);

    /// <summary>
    /// Wraps the registered web server so that <paramref name="beforeListening"/> runs as it starts: once the request
    /// pipeline is built and its certificates are loaded, just before it listens.
    /// </summary>
    internal static void AddBeforeListening(IServiceCollection services, Func<ILogger, CancellationToken, Task> beforeListening)
    {
        ArgumentNullException.ThrowIfNull(services);
        var server = services.LastOrDefault(service => service.ServiceType == typeof(IServer) && !service.IsKeyedService);
        if (server?.ImplementationType is not { } kestrel)
        {
            throw new InvalidOperationException("DevKit can take its port over only from a web server registered by its type, as Kestrel is.");
        }

        services.Remove(server);
        services.AddSingleton(kestrel);
        services.AddSingleton<IServer>(provider => new TakingOverServer(
            (IServer)provider.GetRequiredService(kestrel),
            provider.GetRequiredService<IOptions<KestrelServerOptions>>().Value,
            beforeListening,
            provider.GetRequiredService<ILoggerFactory>().CreateLogger(typeof(PortHandover))));
    }

    private static async Task StopWhenAskedAsync(WebApplication app, HandoverRequests requests, int port)
    {
        await requests.Asked;
        app.Logger.LogInformation("The next switch asked for port {Port}, so this DevKit stops listening and finishes the requests in progress", port);
        app.Lifetime.StopApplication();
    }

    [DllImport("kernel32.dll", EntryPoint = "WaitNamedPipeW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool WaitNamedPipe(string name, uint timeoutMilliseconds);

    /// <summary>The registered server, started once <c>beforeListening</c> has run.</summary>
    private sealed class TakingOverServer(
        IServer server,
        KestrelServerOptions options,
        Func<ILogger, CancellationToken, Task> beforeListening,
        ILogger logger) : IServer
    {
        public IFeatureCollection Features => server.Features;

        public async Task StartAsync<TContext>(IHttpApplication<TContext> application, CancellationToken cancellationToken)
            where TContext : notnull
        {
            // The endpoints and certificates are read now, not once the port is free, which would keep it closed
            // meanwhile; one that does not load stops the start before anything is taken from anyone.
            options.ConfigurationLoader?.Load();
            await beforeListening(logger, cancellationToken);
            await server.StartAsync(application, cancellationToken);
        }

        public Task StopAsync(CancellationToken cancellationToken) => server.StopAsync(cancellationToken);

        public void Dispose()
        {
            // The container made the server, so it disposes it.
        }
    }
}

/// <summary>
/// The requests to make way for the holder of one port and run: the named pipe a newcomer writes to
/// (<see cref="PortHandover.AskToMakeWay"/>), made when this is, so only once the holder listens. Only this account may
/// connect, and the pipe is refused when someone made one of that name first.
/// </summary>
internal sealed class HandoverRequests : IDisposable
{
    /// <summary>What a newcomer writes to ask; a connection that writes anything else asks nothing.</summary>
    public const byte MakeWay = 1;

    private readonly NamedPipeServerStream _pipe;
    private readonly CancellationTokenSource _stop = new();
    private readonly CancellationToken _stopping;
    private readonly TaskCompletionSource _asked = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Lock _gate = new();
    private bool _disposed;

    public HandoverRequests(int port, string run)
    {
        _pipe = new NamedPipeServerStream(PortHandover.PipeName(port, run), PipeDirection.In, 1, PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly | PipeOptions.FirstPipeInstance);
        _stopping = _stop.Token;
        _ = ListenAsync();
    }

    /// <summary>Completes once a newcomer has asked; never, when none does before this is disposed.</summary>
    public Task Asked => _asked.Task;

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _stop.Cancel();
            _pipe.Dispose();
        }

        _stop.Dispose();
    }

    // Disposing ends the waits below: their tasks then end cancelled or faulted, and so does this loop. Each step on the
    // pipe starts under the lock, so none starts on a pipe already disposed.
    private async Task ListenAsync()
    {
        var request = new byte[1];
        while (Start(() => _pipe.WaitForConnectionAsync(_stopping)) is { } connected)
        {
            await connected.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
            if (!connected.IsCompletedSuccessfully || Start(() => _pipe.ReadAsync(request, _stopping).AsTask()) is not Task<int> read)
            {
                return;
            }

            await ((Task)read).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
            if (read.IsCompletedSuccessfully && read.Result == 1 && request[0] == MakeWay)
            {
                _asked.TrySetResult();
                // Asked once is enough: the name is free at once for the DevKit that asked, which makes its own.
                Dispose();
                return;
            }

            // Anything else asks nothing: the pipe waits for the next connection.
            if (Start(() => { _pipe.Disconnect(); return Task.CompletedTask; }) is null)
            {
                return;
            }
        }
    }

    private T? Start<T>(Func<T> step) where T : Task
    {
        lock (_gate)
        {
            return _disposed ? null : step();
        }
    }
}
