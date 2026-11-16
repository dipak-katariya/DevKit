using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Runtime.Versioning;
using System.Text;
using DevKit.Web.Services;
using Xunit;

namespace DevKit.Tests;

/// <summary>
/// A switch to another package, run the way "Run Latest DevKit.bat" runs it (DevKit.Web.exe --maintenance --package),
/// against the built program, in a Release folder of the test's own with packages whose keep-alive scripts the test
/// chooses. Whatever happens, DevKit is never left down when it was up before: a package whose DevKit does not start
/// is marked as failed and the one that ran starts again, a DevKit that runs is never stopped when the switch cannot
/// go on, and two switches never run at once.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class DevKitSwitchTests : IDisposable
{
    private const string EarlierRun = "earlierrun";

    private static readonly TimeSpan SwitchTimeout = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan StartTimeout = TimeSpan.FromSeconds(60);

    private readonly string _root = Path.Combine(AppContext.BaseDirectory, "switch", Guid.NewGuid().ToString("N"));
    private readonly int _port = DevKitProcess.FreeLoopbackPort();
    private readonly List<Process> _started = new();
    private readonly MessageRecordingLogger<DevKitSwitchTests> _log = new();

    public DevKitSwitchTests()
    {
        // The settings folder is DevKit's folder too: shared DevKit does not start in one that others may change.
        Directory.CreateDirectory(_root);
        ScratchPackages.Lock(_root);
        Directory.CreateDirectory(Release);
        ScratchPackages.Lock(Release);
    }

    private string Release => Path.Combine(_root, "Release");

    private string StatePath => Path.Combine(_root, $"live-{_port}.txt");

    private string Package(string name) => Path.Combine(Release, name);

    [Fact]
    public async Task The_package_takes_the_port_over_from_the_maintenance_page()
    {
        var package = ScratchPackages.Add(Package(ScratchPackages.Name), ScratchPackages.DevKitLoop);

        var (outcome, output) = await SwitchAsync(package);

        Assert.True(outcome == SwitchOutcome.Switched, output);
        var state = await File.ReadAllLinesAsync(StatePath);
        Assert.Equal(package, state[0]);
        Assert.NotNull(PortHandover.RunFrom(state[1]));
        Assert.Equal(ScratchPackages.Name + Environment.NewLine + state[1] + Environment.NewLine,
            await File.ReadAllTextAsync(Path.Combine(Release, "live.txt")));
        Assert.Equal(HttpStatusCode.NoContent, await HealthAsync());
    }

    [Fact]
    public async Task The_DevKit_that_runs_finishes_what_it_serves_and_makes_way()
    {
        var earlier = ScratchPackages.Add(Package("DevKit_31_12_2029_09_00_AM"), ScratchPackages.DevKitLoop);
        var package = ScratchPackages.Add(Package(ScratchPackages.Name), ScratchPackages.DevKitLoop);
        var running = await RunningDevKitAsync(earlier, EarlierRun);

        var (outcome, output) = await SwitchAsync(package);

        Assert.True(outcome == SwitchOutcome.Switched, output);
        Assert.Contains("made way", output, StringComparison.Ordinal);
        await running.WaitForExitAsync().WaitAsync(StartTimeout);
        Assert.Equal(0, running.ExitCode);
        Assert.Equal(package, (await File.ReadAllLinesAsync(StatePath))[0]);
        Assert.Equal(HttpStatusCode.NoContent, await HealthAsync());
    }

    [Fact]
    public async Task A_package_whose_DevKit_does_not_start_is_marked_failed_and_the_one_that_ran_runs_again()
    {
        var earlier = ScratchPackages.Add(Package("DevKit_31_12_2029_09_00_AM"), ScratchPackages.DevKitLoop);
        var broken = ScratchPackages.Add(Package(ScratchPackages.Name), ScratchPackages.FailingLoop);
        var running = await RunningDevKitAsync(earlier, EarlierRun);

        var (outcome, output) = await SwitchAsync(broken, running.Id);

        Assert.True(outcome == SwitchOutcome.WentBack, output);
        Assert.True(File.Exists(Path.Combine(broken, DevKitSwitch.FailedFileName)));
        var state = await File.ReadAllLinesAsync(StatePath);
        Assert.Equal(earlier, state[0]);
        Assert.NotEqual(EarlierRun, state[1]);
        Assert.Equal(HttpStatusCode.NoContent, await HealthAsync());
    }

    [Fact]
    public async Task A_package_made_before_the_switch_gets_the_port_free()
    {
        var older = ScratchPackages.Add(Package(ScratchPackages.Name), ScratchPackages.DevKitLoop, current: false);

        var (outcome, output) = await SwitchAsync(older);

        Assert.True(outcome == SwitchOutcome.Switched, output);
        Assert.Contains("makes way first", output, StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.NoContent, await HealthAsync());
    }

    [Fact]
    public async Task Nothing_changes_when_what_holds_the_port_does_not_stop()
    {
        var earlier = ScratchPackages.Add(Package("DevKit_31_12_2029_09_00_AM"), ScratchPackages.DevKitLoop);
        var package = ScratchPackages.Add(Package(ScratchPackages.Name), ScratchPackages.DevKitLoop);
        var stateBefore = WriteState(earlier, EarlierRun);
        using var holder = new TcpListener(IPAddress.Loopback, _port);
        holder.Start();

        var (outcome, output) = await SwitchAsync(package);

        Assert.True(outcome == SwitchOutcome.NotStopped, output);
        Assert.Equal(stateBefore, await File.ReadAllTextAsync(StatePath));
        Assert.True(PortHandover.IsListening(_port));
        Assert.False(File.Exists(Path.Combine(package, DevKitSwitch.FailedFileName)));
    }

    [Fact]
    public async Task One_switch_runs_at_a_time()
    {
        var package = ScratchPackages.Add(Package(ScratchPackages.Name), ScratchPackages.DevKitLoop);
        using var other = DevKitSwitch.Claim(_port, _log);
        Assert.NotNull(other);

        var (outcome, output) = await SwitchAsync(package);

        Assert.True(outcome == SwitchOutcome.Busy, output);
        Assert.False(File.Exists(StatePath));
        Assert.Null(DevKitSwitch.Claim(_port, _log));
    }

    [Fact]
    public async Task A_folder_that_is_not_a_whole_package_here_is_refused_before_anything_changes()
    {
        var elsewhere = ScratchPackages.Add(Path.Combine(_root, ScratchPackages.Name), ScratchPackages.DevKitLoop);

        var (outcome, output) = await SwitchAsync(elsewhere);

        Assert.True(outcome == SwitchOutcome.Refused, output);
        Assert.False(File.Exists(StatePath));
        Assert.False(PortHandover.IsListening(_port));
    }

    [Fact]
    public async Task A_package_another_account_may_change_is_refused_before_anything_changes()
    {
        var package = ScratchPackages.Add(Package(ScratchPackages.Name), ScratchPackages.DevKitLoop);
        ScratchPackages.LetEveryoneChange(Release);

        var (outcome, output) = await SwitchAsync(package);

        Assert.True(outcome == SwitchOutcome.Refused, output);
        Assert.Contains("so it could run its own code as this account", output, StringComparison.Ordinal);
        Assert.False(File.Exists(StatePath));
        Assert.False(PortHandover.IsListening(_port));
    }

    [Fact]
    public async Task A_DevKit_folder_another_account_may_change_is_refused_before_the_DevKit_that_runs_stops()
    {
        var earlier = ScratchPackages.Add(Package("DevKit_31_12_2029_09_00_AM"), ScratchPackages.DevKitLoop);
        var package = ScratchPackages.Add(Package(ScratchPackages.Name), ScratchPackages.DevKitLoop);
        var stateBefore = WriteState(earlier, EarlierRun);
        // Shared DevKit would stop at start in it, so the switch must not stop the one that runs first.
        ScratchPackages.LetEveryoneChange(_root);

        var (outcome, output) = await SwitchAsync(package);

        Assert.True(outcome == SwitchOutcome.Refused, output);
        Assert.Contains("DevKit's folder cannot be trusted", output, StringComparison.Ordinal);
        Assert.Equal(stateBefore, await File.ReadAllTextAsync(StatePath));
        Assert.False(PortHandover.IsListening(_port));
        Assert.False(File.Exists(Path.Combine(package, DevKitSwitch.FailedFileName)));
    }

    [Fact]
    public async Task A_package_whose_files_changed_after_it_was_made_is_refused()
    {
        var package = ScratchPackages.Add(Package(ScratchPackages.Name), ScratchPackages.DevKitLoop);
        await File.AppendAllTextAsync(Path.Combine(package, "devkit-run.bat"), "rem changed\r\n");

        var (outcome, output) = await SwitchAsync(package);

        Assert.True(outcome == SwitchOutcome.Refused, output);
        Assert.Contains("devkit-run.bat is not the file the package was made with", output, StringComparison.Ordinal);
        Assert.False(File.Exists(StatePath));
    }

    [Fact]
    public async Task A_switch_whose_address_is_on_another_port_changes_nothing()
    {
        var package = ScratchPackages.Add(Package(ScratchPackages.Name), ScratchPackages.DevKitLoop);
        var otherPort = DevKitProcess.FreeLoopbackPort();

        var (outcome, output) = await SwitchAsync(package, listenPort: otherPort);

        Assert.True(outcome == SwitchOutcome.Refused, output);
        Assert.Contains($"but not on port {_port}", output, StringComparison.Ordinal);
        Assert.False(File.Exists(StatePath));
        Assert.False(PortHandover.IsListening(_port));
        Assert.False(PortHandover.IsListening(otherPort));
    }

    [Fact]
    public async Task A_page_that_cannot_load_its_certificate_leaves_the_record_of_what_runs_as_it_was()
    {
        var earlier = ScratchPackages.Add(Package("DevKit_31_12_2029_09_00_AM"), ScratchPackages.DevKitLoop);
        var package = ScratchPackages.Add(Package(ScratchPackages.Name), ScratchPackages.DevKitLoop);
        var stateBefore = WriteState(earlier, EarlierRun);
        var https = new Dictionary<string, string>
        {
            ["Kestrel__Endpoints__Https__Url"] = $"https://127.0.0.1:{_port}",
            ["Kestrel__Endpoints__Https__Certificate__Path"] = Path.Combine(_root, "missing.pfx")
        };

        var (outcome, output) = await SwitchAsync(package, environment: https);

        Assert.True(outcome == SwitchOutcome.NotStopped, output);
        Assert.Equal(stateBefore, await File.ReadAllTextAsync(StatePath));
        Assert.False(File.Exists(Path.Combine(package, DevKitSwitch.FailedFileName)));
    }

    [Theory]
    [InlineData("DevKit_29_10_2026_05_56_PM", true)]
    [InlineData("DevKit_29_10_2026_5_56_PM", false)]
    [InlineData("DevKit_29_10_2026_05_56_XM", false)]
    [InlineData("..\\DevKit_29_10_2026_05_56_PM", false)]
    [InlineData("DevKit_Previous", false)]
    public void Only_a_package_name_names_a_package(string name, bool expected) =>
        Assert.Equal(expected, DevKitSwitch.IsPackageName(name));

    [Theory]
    [InlineData("DevKit_30_10_2026_03_29_PM", "2026-10-30 15:29")]
    [InlineData("DevKit_01_01_2030_12_05_AM", "2030-01-01 00:05")]
    [InlineData("DevKit_31_02_2026_03_29_PM", null)]
    [InlineData("DevKit_30_10_2026_13_29_PM", null)]
    [InlineData("DevKit_Previous", null)]
    public void A_package_was_made_when_its_name_says(string name, string? expected) =>
        Assert.Equal(expected, DevKitSwitch.MadeAt(name)?.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture));

    [Fact]
    public void Only_a_whole_package_right_in_the_Release_folder_can_be_switched_to()
    {
        var whole = ScratchPackages.Add(Package(ScratchPackages.Name), ScratchPackages.DevKitLoop);
        var single = ScratchPackages.Add(Package(DevKitSwitch.SinglePackageName), ScratchPackages.DevKitLoop, current: false);
        var noProgram = ScratchPackages.Add(Package("DevKit_02_01_2030_10_00_AM"), ScratchPackages.DevKitLoop);
        File.Delete(Path.Combine(noProgram, "app", "DevKit.Web.exe"));
        var wrongName = ScratchPackages.Add(Package("DevKit_latest"), ScratchPackages.DevKitLoop);
        var linked = Package("DevKit_03_01_2030_10_00_AM");
        SharedSettingsScratch.MakeJunction(linked, whole);

        Assert.True(DevKitSwitch.TryPackage(Release, whole, _log, out var folder, out _));
        Assert.Equal(whole, folder);
        Assert.True(DevKitSwitch.TryPackage(Release, single, _log, out _, out _));
        Assert.False(DevKitSwitch.TryPackage(Release, noProgram, _log, out _, out _));
        Assert.False(DevKitSwitch.TryPackage(Release, wrongName, _log, out _, out _));
        Assert.False(DevKitSwitch.TryPackage(Release, linked, _log, out _, out _));
        Assert.False(DevKitSwitch.TryPackage(Release, Path.Combine(whole, "app"), _log, out _, out _));
        Assert.False(DevKitSwitch.TryPackage(Release, Path.Combine(Release, "..", "Release2", ScratchPackages.Name), _log, out _, out _));
        Assert.False(DevKitSwitch.TryPackage(Release, "", _log, out _, out _));
    }

    [Fact]
    public void The_request_names_the_package_the_process_to_stop_and_leaves_the_page_its_address()
    {
        var log = new MessageRecordingLogger<DevKitSwitchTests>();

        var request = DevKitSwitch.ParseRequest(
            [MaintenanceMode.Switch, DevKitSwitch.PackageOption, "C:\\R\\P", DevKitSwitch.StopOption, "4242", "--urls", "http://*:5850"], log);

        Assert.NotNull(request);
        Assert.Equal("C:\\R\\P", request.Package);
        Assert.Equal(4242, request.StopProcess);
        Assert.Equal(["--urls", "http://*:5850"], request.PageArgs);
        Assert.Null(DevKitSwitch.ParseRequest([MaintenanceMode.Switch], log));
        Assert.Contains(log.Entries, entry => entry.Message.Contains("Run Latest DevKit.bat", StringComparison.Ordinal));
    }

    public void Dispose()
    {
        // The DevKit a test left running makes way, as for the next switch, so nothing is left on the port.
        if (File.Exists(StatePath) && File.ReadAllLines(StatePath) is [_, var run, ..])
        {
            PortHandover.AskToMakeWay(_port, run, new MessageRecordingLogger<DevKitSwitchTests>());
        }

        PortHandover.AskToMakeWay(_port, EarlierRun, new MessageRecordingLogger<DevKitSwitchTests>());
        PortHandover.WaitUntilAsync(() => !PortHandover.IsListening(_port), StartTimeout, CancellationToken.None).GetAwaiter().GetResult();
        foreach (var process in _started)
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                process.WaitForExit(StartTimeout);
            }

            process.Dispose();
        }

        foreach (var link in new DirectoryInfo(Release).EnumerateDirectories().Where(d => d.Attributes.HasFlag(FileAttributes.ReparsePoint)))
        {
            link.Delete();
        }

        DevKitProcess.DeleteWhenReleasedAsync(_root).GetAwaiter().GetResult();
    }

    /// <summary>
    /// Runs the switch to <paramref name="package"/>, stopping <paramref name="stopProcess"/> if it does not make way, its
    /// page on <paramref name="listenPort"/> (the switch's port when left out), with <paramref name="environment"/> added.
    /// </summary>
    private async Task<(SwitchOutcome Outcome, string Output)> SwitchAsync(
        string package, int? stopProcess = null, int? listenPort = null, IReadOnlyDictionary<string, string>? environment = null)
    {
        var arguments = $"{MaintenanceMode.Switch} {DevKitSwitch.PackageOption} \"{package}\"" +
                        (stopProcess is { } id ? $" {DevKitSwitch.StopOption} {id}" : "") +
                        $" --urls http://127.0.0.1:{listenPort ?? _port}";
        var output = new StringBuilder();
        var process = Start(arguments, run: null, output, environment);
        // Once it has exited and all its output is read.
        await process.WaitForExitAsync().WaitAsync(SwitchTimeout);
        lock (output)
        {
            return ((SwitchOutcome)process.ExitCode, output.ToString());
        }
    }

    /// <summary>DevKit from <paramref name="package"/>, started by a switch of <paramref name="run"/>, listening on the port.</summary>
    private async Task<Process> RunningDevKitAsync(string package, string run)
    {
        WriteState(package, run);
        var process = Start($"--urls http://127.0.0.1:{_port}", run, new StringBuilder());
        Assert.True(await PortHandover.WaitUntilAsync(() => PortHandover.HolderListens(_port, run), StartTimeout, CancellationToken.None));
        return process;
    }

    private string WriteState(string package, string run)
    {
        var text = package + Environment.NewLine + run + Environment.NewLine;
        File.WriteAllText(StatePath, text);
        return text;
    }

    private Process Start(string arguments, string? run, StringBuilder output, IReadOnlyDictionary<string, string>? environment = null)
    {
        var info = DevKitProcess.ProgramStartInfo(arguments, Path.Combine(_root, "usersettings.json"));
        HostedDevKitProcess.UseHostedMode(info.Environment, Path.Combine(_root, "audit"));
        foreach (var (name, value) in environment ?? new Dictionary<string, string>())
        {
            info.Environment[name] = value;
        }

        info.Environment[PortHandover.PortVariable] = _port.ToString(CultureInfo.InvariantCulture);
        info.Environment[DevKitSwitch.ReleaseVariable] = Release;
        if (run is null)
        {
            info.Environment.Remove(PortHandover.RunVariable);
        }
        else
        {
            info.Environment[PortHandover.RunVariable] = run;
        }

        var process = Process.Start(info) ?? throw new InvalidOperationException("DevKit.Web.exe did not start.");
        process.OutputDataReceived += (_, line) => Append(output, line.Data);
        process.ErrorDataReceived += (_, line) => Append(output, line.Data);
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        _started.Add(process);
        return process;
    }

    private static void Append(StringBuilder output, string? line)
    {
        lock (output)
        {
            output.AppendLine(line);
        }
    }

    private async Task<HttpStatusCode> HealthAsync()
    {
        using var client = new HttpClient();
        using var response = await client.GetAsync($"http://127.0.0.1:{_port}/healthz");
        return response.StatusCode;
    }

}
