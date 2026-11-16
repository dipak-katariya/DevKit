using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text.RegularExpressions;

namespace DevKit.Web.Services;

/// <summary>How a switch ended. DevKit.Web.exe --maintenance exits with it, and "Run Latest DevKit.bat" says what it means.</summary>
internal enum SwitchOutcome
{
    /// <summary>The package's DevKit took the port over.</summary>
    Switched = 0,

    /// <summary>The arguments or the package were refused; nothing changed.</summary>
    Refused = 1,

    /// <summary>Another switch on the same port is still running; nothing changed.</summary>
    Busy = 2,

    /// <summary>The DevKit that runs did not stop, so it keeps running and nothing changed.</summary>
    NotStopped = 3,

    /// <summary>The package's DevKit did not start, so the package that ran before runs again.</summary>
    WentBack = 4,

    /// <summary>The package's DevKit did not start, and no earlier package runs instead: DevKit is down.</summary>
    Down = 5,

    /// <summary>The package's DevKit did not start, and the earlier one did not start again either: DevKit is down.</summary>
    WentBackFailed = 6,

    /// <summary>The switch was stopped while the package's DevKit was starting, which goes on starting by itself.</summary>
    Interrupted = 7
}

/// <summary>
/// Makes a package the DevKit that runs on a port, with the maintenance page (<see cref="MaintenanceMode"/>) answering
/// meanwhile. "Run Latest DevKit.bat" starts it as DevKit.Web.exe --maintenance --package &lt;folder&gt; [--stop &lt;process&gt;],
/// and waits for it in its own window:
///   1. It claims the port, so one switch runs at a time, and checks the package is whole and in its Release folder.
///   2. As the maintenance page is about to listen, with its certificate loaded, it records the package as the one that
///      runs (<see cref="LivePackage"/>), then asks the DevKit that runs to make way, which finishes the requests it is
///      serving. One that does not, as DevKit from an earlier package, is stopped (the --stop process). Nothing is
///      stopped when the page itself cannot start, so a damaged package never takes DevKit down.
///   3. It starts the package's keep-alive script, whose DevKit takes the port from the page as it is about to listen.
///      A package made before the page could hand over gets the port free instead.
///   4. When that DevKit does not take the port over, its script is stopped, the package is marked as failed, and the
///      package that ran before starts again the same way.
/// </summary>
internal sealed partial class DevKitSwitch
{
    public const string PackageOption = "--package";
    public const string StopOption = "--stop";

    /// <summary>Overrides the Release folder the switch's packages are in; tests and side-by-side copies point it at their own.</summary>
    public const string ReleaseVariable = "DEVKIT_RELEASE_FOLDER";

    /// <summary>
    /// What devkit-deploy.bat writes last into a package: how many files it has and their size, so a package copied only
    /// in part is not taken for a whole one. Packages without it were made before this switch.
    /// </summary>
    public const string ManifestFileName = "package.txt";

    /// <summary>What the switch writes into a package whose DevKit did not start; "Run Latest DevKit.bat" skips it unless named.</summary>
    public const string FailedFileName = "switch-failed.txt";

    /// <summary>The single package an earlier devkit-deploy.bat made right in the Release folder, once it moved aside.</summary>
    public const string SinglePackageName = "DevKit_Previous";

    private const string LoopFileName = "devkit-run.bat";
    private const string ClaimPrefix = "DevKit.Switch.";
    private const string LockScript = "tools\\devkit-lock.bat";
    private const string ProgramName = "DevKit.Web";
    private const string AppFolderName = "app";

    private static readonly TimeSpan GracefulStopWait = TimeSpan.FromSeconds(10);

    // Shared DevKit checks its certificate and asks the domain for its keys as it starts, and its keep-alive script tries
    // again for a few minutes when it stops; this is longer, so a DevKit that cannot start is noticed by its script ending.
    private static readonly TimeSpan StartWait = TimeSpan.FromMinutes(4);

    private static readonly TimeSpan VerifyWait = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan KillWait = TimeSpan.FromSeconds(10);

    private readonly int _port;
    private readonly string _release;
    private readonly string _package;
    private readonly int? _stopProcess;
    private readonly ILogger _log;
    private readonly string _run = PortHandover.NewRun();
    private volatile SwitchStatus _status;
    private LivePackage? _previous;
    private bool _liveWritten;
    private bool _previousStopped;
    private WebApplication? _page;
    private bool _serving;

    private DevKitSwitch(int port, string release, string package, int? stopProcess, ILogger log)
    {
        _port = port;
        _release = release;
        _package = package;
        _stopProcess = stopProcess;
        _log = log;
        _status = new SwitchStatus(Path.GetFileName(package), DateTimeOffset.UtcNow, GoingBack: false);
    }

    /// <summary>What a switch was asked for: the package, the process to stop if it does not make way, and the page's own arguments.</summary>
    internal sealed record Request(string Package, int? StopProcess, string[] PageArgs);

    /// <summary>
    /// What the maintenance page tells whoever opens DevKit: the name of the package whose DevKit is starting, when the
    /// switch began, and whether that package is the one that ran before, because the new one did not start.
    /// </summary>
    internal sealed record SwitchStatus(string PackageName, DateTimeOffset Since, bool GoingBack);

    /// <summary>Where the switch is now; the maintenance page reads it for every request.</summary>
    public SwitchStatus Status => _status;

    /// <summary>
    /// The switch to <see cref="Request.Package"/> on <paramref name="port"/>, from the Release folder this program's
    /// own package is in; null, with the reason logged, when the package is not a whole one there.
    /// </summary>
    public static DevKitSwitch? Create(int port, Request request, ILogger log)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(log);
        var release = ReleaseFolder();
        if (release is null)
        {
            log.LogError("This program does not run from a package in a Release folder, so it switches to no package");
            return null;
        }

        if (!TryPackage(release, request.Package, log, out var package, out var problem))
        {
            log.LogError("{Package} is not a package DevKit can switch to: {Problem}", request.Package, problem);
            return null;
        }

        return new DevKitSwitch(port, release, package, request.StopProcess, log);
    }

    /// <summary>The package, the process and the page's arguments in <paramref name="args"/>; null, with the reason logged, without a package.</summary>
    public static Request? ParseRequest(IEnumerable<string> args, ILogger log)
    {
        ArgumentNullException.ThrowIfNull(log);
        var pageArgs = new List<string>();
        string? package = null;
        int? stopProcess = null;
        var queue = new Queue<string>(args);
        while (queue.TryDequeue(out var arg))
        {
            if (arg.Equals(PackageOption, StringComparison.OrdinalIgnoreCase) && queue.TryDequeue(out var folder))
            {
                package = folder;
            }
            else if (arg.Equals(StopOption, StringComparison.OrdinalIgnoreCase) && queue.TryDequeue(out var process))
            {
                stopProcess = int.TryParse(process, NumberStyles.None, CultureInfo.InvariantCulture, out var id) ? id : null;
            }
            else if (!arg.Equals(MaintenanceMode.Switch, StringComparison.OrdinalIgnoreCase))
            {
                pageArgs.Add(arg);
            }
        }

        if (package is null)
        {
            log.LogError("The maintenance page runs only as part of a switch: start it with Run Latest DevKit.bat");
            return null;
        }

        return new Request(package, stopProcess, pageArgs.ToArray());
    }

    /// <summary>
    /// Claims <paramref name="port"/> for this switch until it is disposed; null while another switch holds it. The
    /// claim is a named pipe, which every Windows session and account on the machine sees, so two switches never run at
    /// once, even from two remote desktop sessions.
    /// </summary>
    public static IDisposable? Claim(int port, ILogger log)
    {
        ArgumentNullException.ThrowIfNull(log);
        var name = ClaimPrefix + port.ToString(CultureInfo.InvariantCulture);
        if (PortHandover.PipeExists(name))
        {
            return null;
        }

        try
        {
            return new NamedPipeServerStream(name, PipeDirection.In, 1, PipeTransmissionMode.Byte,
                PipeOptions.FirstPipeInstance | PipeOptions.CurrentUserOnly);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            log.LogError(ex, "Another switch claimed port {Port} at the same moment", port);
            return null;
        }
    }

    /// <summary>Whether <paramref name="name"/> is a package folder's: DevKit_&lt;day&gt;_&lt;month&gt;_&lt;year&gt;_&lt;hour&gt;_&lt;minute&gt;_&lt;AM|PM&gt;.</summary>
    public static bool IsPackageName(string name) => PackageNamePattern().IsMatch(name);

    /// <summary>When the package named <paramref name="name"/> was made, in the server's time, as its name says; null for any other name.</summary>
    public static DateTime? MadeAt(string name) =>
        IsPackageName(name)
        && DateTime.TryParseExact(name, "'DevKit_'dd'_'MM'_'yyyy'_'hh'_'mm'_'tt", CultureInfo.InvariantCulture, DateTimeStyles.None, out var made)
            ? made
            : null;

    /// <summary>
    /// Runs the switch: the maintenance page, made by <paramref name="buildPage"/> around the step that takes the port
    /// as it is about to listen, then the hand-over to the package, or back to the one that ran.
    /// </summary>
    public async Task<SwitchOutcome> RunAsync(Func<Func<ILogger, CancellationToken, Task>, WebApplication> buildPage)
    {
        ArgumentNullException.ThrowIfNull(buildPage);
        _log.LogInformation("Switching DevKit on port {Port} to {Package}", _port, Path.GetFileName(_package));
        await using var page = BuildPage(buildPage);
        if (page is null)
        {
            return SwitchOutcome.Refused;
        }

        _page = page;
        try
        {
            await page.StartAsync();
            _serving = true;
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or CryptographicException or UnauthorizedAccessException)
        {
            _log.LogError(ex, "The maintenance page did not start on port {Port}", _port);
            return await PortNotTakenAsync();
        }

        var handOver = await HandOverAsync(_package, _run);
        if (handOver == HandOver.TookOver)
        {
            ClearFailed();
            _log.LogInformation("DevKit from {Package} runs on port {Port}", Path.GetFileName(_package), _port);
            return SwitchOutcome.Switched;
        }

        if (handOver == HandOver.Interrupted)
        {
            return SwitchOutcome.Interrupted;
        }

        MarkFailed("its DevKit did not take port " + _port.ToString(CultureInfo.InvariantCulture) + " over");
        return await GoBackAsync();
    }

    /// <summary>The maintenance page; null, with the reason logged, when its configuration is refused, and then nothing has changed.</summary>
    private WebApplication? BuildPage(Func<Func<ILogger, CancellationToken, Task>, WebApplication> buildPage)
    {
        try
        {
            return buildPage(TakePortAsync);
        }
        catch (Exception ex) when (ex is InvalidOperationException or InvalidDataException or FormatException)
        {
            _log.LogError(ex, "The maintenance page could not be made ready, so nothing was changed");
            return null;
        }
    }

    private enum HandOver
    {
        TookOver,
        Failed,
        Interrupted
    }

    /// <summary>
    /// The maintenance page's step just before it listens: records the package as the one that runs, then stops the
    /// DevKit that runs. Throws when that DevKit is still listening after all, so the page does not start.
    /// </summary>
    private async Task TakePortAsync(ILogger logger, CancellationToken cancellationToken)
    {
        _previous = LivePackage.Read(_port, _release);
        // Before that DevKit stops: its keep-alive script reads it as it does, and stays stopped.
        new LivePackage(_package, _run).Write(_port, _release);
        _liveWritten = true;
        if (PortHandover.IsListening(_port) && !await StopRunningDevKitAsync(cancellationToken))
        {
            throw new InvalidOperationException($"The DevKit that runs on port {_port} did not stop.");
        }
    }

    private async Task<bool> StopRunningDevKitAsync(CancellationToken cancellationToken)
    {
        if (_previous?.Run is { } run
            && PortHandover.AskToMakeWay(_port, run, _log)
            && await WaitForFreePortAsync(GracefulStopWait, cancellationToken))
        {
            _log.LogInformation("The DevKit that ran finished what it was doing and made way");
            _previousStopped = true;
            return true;
        }

        if (_stopProcess is not { } process || !StopProcess(process))
        {
            return false;
        }

        _previousStopped = true;
        return await WaitForFreePortAsync(PortHandover.PortWait, cancellationToken);
    }

    /// <summary>Stops DevKit process <paramref name="id"/>, an earlier package's, which cannot be asked to make way.</summary>
    private bool StopProcess(int id)
    {
        var devKits = Process.GetProcessesByName(ProgramName);
        try
        {
            var running = devKits.FirstOrDefault(process => process.Id == id && process.Id != Environment.ProcessId);
            if (running is null)
            {
                _log.LogInformation("The DevKit that ran (process {Process}) has stopped already", id);
                return true;
            }

            _log.LogInformation("Stopping the DevKit that runs (process {Process}): it did not make way by itself", id);
            running.Kill();
            return running.WaitForExit(KillWait);
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or NotSupportedException)
        {
            _log.LogError(ex, "The DevKit that runs (process {Process}) could not be stopped; it may run as another Windows account", id);
            return false;
        }
        finally
        {
            foreach (var devKit in devKits)
            {
                devKit.Dispose();
            }
        }
    }

    /// <summary>
    /// The page did not start: nothing changed when the DevKit that ran is still running, otherwise it goes back. The
    /// record of which package runs is put back only when this switch wrote it: a page that failed before, as on a
    /// certificate it could not load, leaves it as it found it.
    /// </summary>
    private async Task<SwitchOutcome> PortNotTakenAsync()
    {
        if (_previousStopped)
        {
            return await GoBackAsync();
        }

        if (!_liveWritten)
        {
            _log.LogError("Nothing was changed: the DevKit that ran keeps running");
            return SwitchOutcome.NotStopped;
        }

        try
        {
            if (_previous is null)
            {
                LivePackage.Clear(_port, _release);
            }
            else
            {
                _previous.Write(_port, _release);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log.LogError(ex, "The record of which package runs on port {Port} could not be put back; run the switch again", _port);
        }

        _log.LogError("Nothing was changed: the DevKit that ran keeps running");
        return SwitchOutcome.NotStopped;
    }

    /// <summary>
    /// Starts <paramref name="package"/>'s keep-alive script for <paramref name="run"/> and waits until its DevKit has
    /// taken the port over, its script has ended, or <see cref="StartWait"/> has passed; in the last two cases the
    /// script and its DevKit are stopped.
    /// </summary>
    private async Task<HandOver> HandOverAsync(string package, string run)
    {
        var takesOver = File.Exists(Path.Combine(package, ManifestFileName));
        if (!takesOver)
        {
            _log.LogInformation("{Package} was made before DevKit could take the port from the maintenance page, so the page makes way first",
                Path.GetFileName(package));
            await StopServingAsync();
        }

        var requests = new HandoverRequests(_port, run);
        if (_serving)
        {
            // Said once its DevKit can ask it to make way.
            _log.LogInformation("The maintenance page answers on port {Port}: whoever opens DevKit is told it is being updated", _port);
        }

        try
        {
            using var loop = StartLoop(package, run);
            if (loop is null)
            {
                return HandOver.Failed;
            }

            _log.LogInformation("Waiting for it to take the port over, for up to {Minutes} minutes; its window says how it goes",
                StartWait.TotalMinutes);
            var handOver = await WaitForTakeOverAsync(loop, takesOver ? _ => requests.Asked : PortListenedAsync);
            if (handOver == HandOver.TookOver && takesOver)
            {
                await StopServingAsync();
                requests.Dispose();
                // Its own pipe, and something on the port: a DevKit that took the page's place but listens elsewhere
                // has not taken DevKit's port over.
                handOver = await PortHandover.WaitUntilAsync(() => PortHandover.HolderListens(_port, run) && PortHandover.IsListening(_port),
                        VerifyWait, CancellationToken.None)
                    ? HandOver.TookOver
                    : HandOver.Failed;
            }

            if (handOver == HandOver.Failed)
            {
                _log.LogError("DevKit from {Package} did not take the port over; its window and devkit-run.log beside devkit.json say why",
                    Path.GetFileName(package));
                StopLoop(loop);
            }

            return handOver;
        }
        finally
        {
            requests.Dispose();
        }
    }

    /// <summary>Waits for <paramref name="tookOver"/>, the end of <paramref name="loop"/>, <see cref="StartWait"/>, or the page being stopped.</summary>
    private async Task<HandOver> WaitForTakeOverAsync(Process loop, Func<CancellationToken, Task> tookOver)
    {
        using var interrupted = new CancellationTokenSource();
        using var stopping = _serving ? _page!.Lifetime.ApplicationStopping.Register(interrupted.Cancel) : default;
        using var decided = new CancellationTokenSource();
        var took = tookOver(decided.Token);
        var ended = loop.WaitForExitAsync(decided.Token);
        var waited = Task.Delay(StartWait, interrupted.Token);
        var first = await Task.WhenAny(took, ended, waited);
        await decided.CancelAsync();
        if (first == took)
        {
            return HandOver.TookOver;
        }

        if (interrupted.IsCancellationRequested)
        {
            _log.LogWarning("The switch was stopped; DevKit from {Package} goes on starting by itself in its window", Path.GetFileName(_package));
            return HandOver.Interrupted;
        }

        return HandOver.Failed;
    }

    /// <summary>Completes once something listens on the port: the DevKit of a package that gets it free.</summary>
    private Task PortListenedAsync(CancellationToken cancellationToken) =>
        PortHandover.WaitUntilAsync(() => PortHandover.IsListening(_port), TimeSpan.MaxValue, cancellationToken);

    /// <summary>
    /// Goes back to the package that ran before, when it is another whole package in this Release folder: it is
    /// recorded as the one that runs, under a run of its own, and started the same way.
    /// </summary>
    private async Task<SwitchOutcome> GoBackAsync()
    {
        var previous = PreviousPackage();
        if (previous is null)
        {
            await StopServingAsync();
            _log.LogError("There is no earlier package to go back to, so DevKit does not run on port {Port}", _port);
            return SwitchOutcome.Down;
        }

        _log.LogWarning("Going back to {Package}, which ran before", Path.GetFileName(previous));
        _status = _status with { PackageName = Path.GetFileName(previous), GoingBack = true };
        var run = PortHandover.NewRun();
        try
        {
            new LivePackage(previous, run).Write(_port, _release);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log.LogError(ex, "{Package} could not be recorded as the package that runs; it is started anyway", Path.GetFileName(previous));
        }

        var handOver = await HandOverAsync(previous, run);
        await StopServingAsync();
        if (handOver == HandOver.TookOver)
        {
            _log.LogInformation("DevKit from {Package} runs on port {Port} again", Path.GetFileName(previous), _port);
        }

        return handOver switch
        {
            HandOver.TookOver => SwitchOutcome.WentBack,
            HandOver.Interrupted => SwitchOutcome.Interrupted,
            _ => SwitchOutcome.WentBackFailed
        };
    }

    private string? PreviousPackage() =>
        _previous is not null
        && TryPackage(_release, _previous.PackageFolder, _log, out var previous, out _)
        && !previous.Equals(_package, StringComparison.OrdinalIgnoreCase)
            ? previous
            : null;

    private async Task StopServingAsync()
    {
        if (_serving)
        {
            _serving = false;
            await _page!.StopAsync();
        }
    }

    /// <summary>Starts <paramref name="package"/>'s keep-alive script in a minimized window of its own, for <paramref name="run"/>.</summary>
    private Process? StartLoop(string package, string run)
    {
        // The script, and its DevKit, get this program's environment: the port, shared mode, and the run.
        Environment.SetEnvironmentVariable(PortHandover.RunVariable, run);
        var start = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "cmd.exe"), "/c .\\" + LoopFileName + " --no-pause")
        {
            UseShellExecute = true,
            WorkingDirectory = package,
            WindowStyle = ProcessWindowStyle.Minimized
        };
        try
        {
            var loop = Process.Start(start);
            _log.LogInformation("Started DevKit from {Package} in a minimized window", Path.GetFileName(package));
            return loop;
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
        {
            _log.LogError(ex, "The keep-alive script of {Package} could not be started", Path.GetFileName(package));
            return null;
        }
    }

    private void StopLoop(Process loop)
    {
        try
        {
            if (!loop.HasExited)
            {
                loop.Kill(entireProcessTree: true);
                loop.WaitForExit(KillWait);
            }
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or NotSupportedException or AggregateException)
        {
            _log.LogError(ex, "The keep-alive script that was started (process {Process}) could not be stopped; close its window", loop.Id);
        }
    }

    private Task<bool> WaitForFreePortAsync(TimeSpan wait, CancellationToken cancellationToken) =>
        PortHandover.WaitUntilAsync(() => !PortHandover.IsListening(_port), wait, cancellationToken);

    private void MarkFailed(string reason)
    {
        try
        {
            File.WriteAllText(Path.Combine(_package, FailedFileName),
                $"{DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)}: {reason}{Environment.NewLine}");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log.LogError(ex, "{Package} could not be marked as failed, so a plain run of the switch may pick it again", Path.GetFileName(_package));
        }
    }

    private void ClearFailed()
    {
        try
        {
            File.Delete(Path.Combine(_package, FailedFileName));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log.LogError(ex, "{Package} still says it failed before; delete its {File}", Path.GetFileName(_package), FailedFileName);
        }
    }

    /// <summary>
    /// The Release folder this program's package is in, as it runs from &lt;Release&gt;\&lt;package&gt;\app, or the one
    /// <see cref="ReleaseVariable"/> names.
    /// </summary>
    private static string? ReleaseFolder()
    {
        if (Environment.GetEnvironmentVariable(ReleaseVariable) is { Length: > 0 } named)
        {
            return Path.GetFullPath(named).TrimEnd(Path.DirectorySeparatorChar);
        }

        var app = new DirectoryInfo(AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar));
        return app.Name.Equals(AppFolderName, StringComparison.OrdinalIgnoreCase) && app.Parent?.Parent is { } release
            ? release.FullName.TrimEnd(Path.DirectorySeparatorChar)
            : null;
    }

    /// <summary>
    /// Whether <paramref name="candidate"/> is a whole package right in <paramref name="release"/>: a package's name, no
    /// link to another folder, its keep-alive script and program there, its files the ones its package.txt lists, and
    /// nobody but this account, SYSTEM and administrators able to change it or the Release folder.
    /// <paramref name="folder"/> is its full path.
    /// </summary>
    internal static bool TryPackage(string release, string candidate, ILogger log, out string folder, out string problem)
    {
        if (string.IsNullOrWhiteSpace(candidate) || candidate.IndexOfAny(Path.GetInvalidPathChars()) >= 0)
        {
            folder = "";
            problem = "it is not a folder name";
            return false;
        }

        folder = Path.GetFullPath(candidate).TrimEnd(Path.DirectorySeparatorChar);
        problem = Problem(release, folder);
        if (problem.Length == 0)
        {
            problem = Trust(release, folder, log);
        }

        return problem.Length == 0;
    }

    /// <summary>
    /// What keeps the files of package <paramref name="folder"/> from being trusted: another account that may change them
    /// or the Release folder, so it could run its own code as this one, or files that are not the ones its package.txt
    /// lists. Empty when nothing does.
    /// </summary>
    private static string Trust(string release, string folder, ILogger log)
    {
        try
        {
            var writer = OperatingSystem.IsWindows()
                ? FolderAccess.OtherWriter(release, AccessScope.Folder) ?? FolderAccess.OtherWriter(folder, AccessScope.FolderAndContents)
                : null;
            return writer is null
                ? PackageManifest.Problem(folder)
                : $"{writer}, so it could run its own code as this account. Run {LockScript} \"{release}\" to leave the folder to this account, SYSTEM and administrators only";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            log.LogError(ex, "The files of {Package} could not be checked", Path.GetFileName(folder));
            return "its files could not be checked: " + ex.Message;
        }
    }

    /// <summary>What keeps <paramref name="folder"/> from being a whole package right in <paramref name="release"/>; empty when nothing does.</summary>
    private static string Problem(string release, string folder)
    {
        if (!folder.StartsWith(release + Path.DirectorySeparatorChar, StringComparison.Ordinal)
            || !string.Equals(Path.GetDirectoryName(folder), release, StringComparison.Ordinal))
        {
            return "it is not a folder right in " + release;
        }

        var name = Path.GetFileName(folder);
        if (!IsPackageName(name) && !name.Equals(SinglePackageName, StringComparison.OrdinalIgnoreCase))
        {
            return "its name is not a package's";
        }

        var app = Path.Combine(folder, AppFolderName);
        if (IsLinkOrMissing(folder) || IsLinkOrMissing(app))
        {
            return "it is missing, or is a link to another folder";
        }

        return File.Exists(Path.Combine(folder, LoopFileName)) && File.Exists(Path.Combine(app, ProgramName + ".exe"))
            ? ""
            : $"it has no {LoopFileName} or {AppFolderName}\\{ProgramName}.exe";
    }

    private static bool IsLinkOrMissing(string folder)
    {
        var directory = new DirectoryInfo(folder);
        return !directory.Exists || directory.Attributes.HasFlag(FileAttributes.ReparsePoint);
    }

    [GeneratedRegex("^DevKit_[0-9]{2}_[0-9]{2}_[0-9]{4}_[0-9]{2}_[0-9]{2}_[AP]M$", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex PackageNamePattern();
}
