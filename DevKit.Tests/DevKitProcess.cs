using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text.RegularExpressions;
using Xunit;

namespace DevKit.Tests;

/// <summary>
/// The built DevKit, started as its own process the way a user starts it: on a free loopback port, with
/// its own settings file holding a test PAT, and with no browser opened. The sign-in link is read from
/// its output. Killed, with its folder removed, when the test class is done.
/// </summary>
public partial class DevKitProcess : IAsyncLifetime
{
    public const string Pat = "e2e-test-pat-6b8a2f41";

    // Nothing listens on the discard port, so every TFS call fails at once instead of waiting.
    protected const string UnreachableTfsUrl = "http://127.0.0.1:9/tfs/Collection";
    private static readonly TimeSpan StartTimeout = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan StopTimeout = TimeSpan.FromSeconds(10);

    private readonly string _folder = Path.Combine(AppContext.BaseDirectory, "e2e", Guid.NewGuid().ToString("N"));
    private readonly TaskCompletionSource<string> _signInLink = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private Process? _process;

    /// <summary>This instance's own folder: its settings file, and in hosted mode its audit and keys.</summary>
    protected string Folder => _folder;

    public string SettingsPath => Path.Combine(_folder, "usersettings.json");

    public Uri BaseAddress { get; private set; } = null!;

    public string SignInLink { get; private set; } = "";

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(_folder);
        await File.WriteAllTextAsync(SettingsPath,
            $$"""{ "Tfs": { "Url": "{{UnreachableTfsUrl}}", "Pat": "{{Pat}}" }, "TeamName": "E2E" }""");

        var port = FreeLoopbackPort();
        BaseAddress = new Uri($"http://127.0.0.1:{port}");
        _process = Process.Start(StartInfo(port)) ?? throw new InvalidOperationException("DevKit did not start.");
        _process.OutputDataReceived += (_, line) => WatchForSignInLink(line.Data);
        _process.ErrorDataReceived += (_, _) => { };
        _process.BeginOutputReadLine();
        _process.BeginErrorReadLine();

        SignInLink = await _signInLink.Task.WaitAsync(StartTimeout);
    }

    public Task DisposeAsync()
    {
        if (_process is not null)
        {
            if (!_process.HasExited)
            {
                _process.Kill(entireProcessTree: true);
                _process.WaitForExit(StopTimeout);
            }

            _process.Dispose();
        }

        Directory.Delete(_folder, recursive: true);
        return Task.CompletedTask;
    }

    /// <summary>A client that keeps no cookies and follows no redirects, so each test says exactly what it sends.</summary>
    public HttpClient NewClient() =>
        new(new HttpClientHandler { UseCookies = false, AllowAutoRedirect = false }) { BaseAddress = BaseAddress };

    private ProcessStartInfo StartInfo(int port)
    {
        var program = Path.Combine(AppContext.BaseDirectory, OperatingSystem.IsWindows() ? "DevKit.Web.exe" : "DevKit.Web");
        var info = new ProcessStartInfo(program, $"--urls http://127.0.0.1:{port}")
        {
            WorkingDirectory = AppContext.BaseDirectory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        info.Environment["DEVKIT_SETTINGS_PATH"] = SettingsPath;
        info.Environment["DEVKIT_NO_BROWSER"] = "1";
        info.Environment["ASPNETCORE_ENVIRONMENT"] = "Production";
        info.Environment.Remove("ASPNETCORE_URLS");
        Configure(info.Environment);
        return info;
    }

    /// <summary>The mode to start in. Said outright, so a test never depends on what a devkit.json on the machine says.</summary>
    protected virtual void Configure(IDictionary<string, string?> environment) => environment["DevKit__Mode"] = "Local";

    /// <summary>The link a line of DevKit's output announces once it is ready, or null for any other line.</summary>
    protected virtual string? ReadyLink(string line) =>
        ReadyLine().Match(line) is { Success: true } ready ? ready.Groups["link"].Value : null;

    private void WatchForSignInLink(string? line)
    {
        if (line is not null && ReadyLink(line) is { } link)
        {
            _signInLink.TrySetResult(link);
        }
    }

    private static int FreeLoopbackPort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    [GeneratedRegex(@"DevKit is ready: (?<link>http\S+)")]
    private static partial Regex ReadyLine();
}

/// <summary>
/// DevKit started in hosted mode: shared, with Windows sign-in, sessions and the sign-in audit. It announces where it
/// is shared rather than a link, so <see cref="DevKitProcess.SignInLink"/> stays empty.
/// </summary>
public sealed class HostedDevKitProcess : DevKitProcess
{
    /// <summary>Where this DevKit writes its sign-in audit.</summary>
    public string AuditFolder => Path.Combine(Folder, "audit");

    protected override void Configure(IDictionary<string, string?> environment)
    {
        environment["DevKit__Mode"] = "Hosted";
        environment["DevKit__TfsUrl"] = UnreachableTfsUrl;
        environment["DevKit__AuditFolder"] = AuditFolder;
    }

    protected override string? ReadyLink(string line) =>
        line.StartsWith("DevKit is shared at:", StringComparison.Ordinal) ? "" : null;
}
