using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Security.Principal;
using System.Text.RegularExpressions;
using DevKit.Web.Services;
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
        // DevKit's folder, as the settings file's: shared DevKit does not start in one that others may change.
        ScratchPackages.Lock(_folder);
        await File.WriteAllTextAsync(SettingsPath,
            $$"""{ "Tfs": { "Url": "{{UnreachableTfsUrl}}", "Pat": "{{Pat}}" }, "TeamName": "E2E" }""");

        var port = FreeLoopbackPort();
        BaseAddress = new Uri($"{Scheme}://127.0.0.1:{port}");
        Prepare();
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

        return DeleteWhenReleasedAsync(_folder);
    }

    /// <summary>
    /// Deletes <paramref name="folder"/> once nothing holds a file in it: a keep-alive script a switch started ends a
    /// moment after its DevKit, and cmd holds its file and folder until then.
    /// </summary>
    internal static async Task DeleteWhenReleasedAsync(string folder) =>
        Assert.True(await PortHandover.WaitUntilAsync(() => Deleted(folder), StartTimeout, CancellationToken.None), $"{folder} is still in use");

    private static bool Deleted(string folder)
    {
        try
        {
            Directory.Delete(folder, recursive: true);
            return true;
        }
        catch (IOException)
        {
            return false;
        }
    }

    /// <summary>The exit code once the process has ended by itself; throws when it is still running after <paramref name="timeout"/>.</summary>
    public async Task<int> ExitCodeAsync(TimeSpan timeout)
    {
        var process = _process ?? throw new InvalidOperationException("DevKit was not started.");
        await process.WaitForExitAsync().WaitAsync(timeout);
        return process.ExitCode;
    }

    /// <summary>A client that keeps no cookies and follows no redirects, so each test says exactly what it sends.</summary>
    public HttpClient NewClient() =>
        new(new HttpClientHandler { UseCookies = false, AllowAutoRedirect = false }) { BaseAddress = BaseAddress };

    private ProcessStartInfo StartInfo(int port)
    {
        var info = ProgramStartInfo($"{Arguments} --urls {Scheme}://127.0.0.1:{port}".TrimStart(), SettingsPath);
        Configure(info.Environment);
        return info;
    }

    /// <summary>
    /// The built program with <paramref name="arguments"/>, its output read by the caller, its settings in
    /// <paramref name="settingsPath"/>, in Production, and opening no browser.
    /// </summary>
    internal static ProcessStartInfo ProgramStartInfo(string arguments, string settingsPath)
    {
        var program = Path.Combine(AppContext.BaseDirectory, OperatingSystem.IsWindows() ? "DevKit.Web.exe" : "DevKit.Web");
        var info = new ProcessStartInfo(program, arguments)
        {
            WorkingDirectory = AppContext.BaseDirectory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        info.Environment["DEVKIT_SETTINGS_PATH"] = settingsPath;
        info.Environment["DEVKIT_NO_BROWSER"] = "1";
        info.Environment["ASPNETCORE_ENVIRONMENT"] = "Production";
        info.Environment.Remove("ASPNETCORE_URLS");
        return info;
    }

    /// <summary>What DevKit is started with besides its address.</summary>
    protected virtual string Arguments => "";

    /// <summary>http, or https for a DevKit given a certificate.</summary>
    protected virtual string Scheme => "http";

    /// <summary>Puts what the program needs in <see cref="Folder"/> before it starts.</summary>
    protected virtual void Prepare()
    {
        // DevKit itself needs only its settings file, written already.
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

    internal static int FreeLoopbackPort()
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
public class HostedDevKitProcess : DevKitProcess
{
    /// <summary>Where this DevKit writes its sign-in audit.</summary>
    public string AuditFolder => Path.Combine(Folder, "audit");

    protected override void Configure(IDictionary<string, string?> environment) => UseHostedMode(environment, AuditFolder);

    /// <summary>Hosted mode against a TFS server that never answers, with the sign-in audit in <paramref name="auditFolder"/>.</summary>
    internal static void UseHostedMode(IDictionary<string, string?> environment, string auditFolder)
    {
        environment["DevKit__Mode"] = "Hosted";
        environment["DevKit__TfsUrl"] = UnreachableTfsUrl;
        environment["DevKit__AuditFolder"] = auditFolder;
    }

    protected override string? ReadyLink(string line) =>
        line.StartsWith("DevKit is shared at:", StringComparison.Ordinal) ? "" : null;
}

/// <summary>
/// Shared DevKit over https alone, with a certificate made for this test run (localhost and 127.0.0.1) that only the
/// test's own client trusts, by its thumbprint.
/// </summary>
public sealed class HttpsHostedDevKitProcess : HostedDevKitProcess
{
    private readonly string _password = Guid.NewGuid().ToString("N");

    /// <summary>The thumbprint of the certificate DevKit serves.</summary>
    public string Thumbprint { get; private set; } = "";

    private string CertificatePath => Path.Combine(Folder, "devkit-test.pfx");

    protected override string Scheme => "https";

    protected override void Prepare()
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest("CN=localhost", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var names = new SubjectAlternativeNameBuilder();
        names.AddDnsName("localhost");
        names.AddIpAddress(IPAddress.Loopback);
        request.CertificateExtensions.Add(names.Build());
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension([new Oid("1.3.6.1.5.5.7.3.1")], critical: false));
        using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddDays(1));
        File.WriteAllBytes(CertificatePath, certificate.Export(X509ContentType.Pfx, _password));
        Thumbprint = certificate.Thumbprint;
    }

    protected override void Configure(IDictionary<string, string?> environment)
    {
        base.Configure(environment);
        environment["Kestrel__Certificates__Default__Path"] = CertificatePath;
        environment["Kestrel__Certificates__Default__Password"] = _password;
    }

    /// <summary>A client that trusts this run's certificate alone, and signs in as the Windows account running the tests.</summary>
    public HttpClient NewWindowsClient() =>
        new(new HttpClientHandler
        {
            UseCookies = false,
            AllowAutoRedirect = false,
            Credentials = CredentialCache.DefaultCredentials,
            ServerCertificateCustomValidationCallback = (_, served, _, _) => string.Equals(served?.Thumbprint, Thumbprint, StringComparison.OrdinalIgnoreCase)
        })
        {
            BaseAddress = BaseAddress
        };
}

/// <summary>
/// The page shared DevKit shows while it is updated, started the way "Run Latest DevKit.bat" starts a switch: the same
/// program with --maintenance and the package to switch to, in hosted mode, with the port it holds in DEVKIT_PORT. The
/// package's keep-alive script only waits, so the page goes on answering for the tests.
/// </summary>
public sealed class MaintenancePageProcess : HostedDevKitProcess
{
    private string Release => Path.Combine(Folder, "Release");

    private string Package => Path.Combine(Release, ScratchPackages.Name);

    /// <summary>The run of the switch: only a DevKit of this run may take the page's port.</summary>
    public string Run => File.ReadAllLines(Path.Combine(Folder, $"live-{BaseAddress.Port}.txt"))[1];

    protected override string Arguments => $"{MaintenanceMode.Switch} {DevKitSwitch.PackageOption} \"{Package}\"";

    protected override void Prepare()
    {
        Directory.CreateDirectory(Release);
        ScratchPackages.Lock(Release);
        ScratchPackages.Add(Package, ScratchPackages.WaitingLoop);
    }

    protected override void Configure(IDictionary<string, string?> environment)
    {
        base.Configure(environment);
        environment[PortHandover.PortVariable] = BaseAddress.Port.ToString(CultureInfo.InvariantCulture);
        environment[DevKitSwitch.ReleaseVariable] = Release;
    }

    protected override string? ReadyLink(string line) =>
        line.Contains("The maintenance page answers on port", StringComparison.Ordinal) ? "" : null;
}

/// <summary>Packages in a test's own Release folder, each with the keep-alive script the test gives it.</summary>
internal static class ScratchPackages
{
    public const string Name = "DevKit_01_01_2030_10_00_AM";

    /// <summary>A keep-alive script whose DevKit never comes: it waits while the maintenance page listens.</summary>
    public const string WaitingLoop = "@echo off\r\n:wait\r\nping -n 2 127.0.0.1 >nul\r\nnetstat -ano | findstr /r /c:\":%DEVKIT_PORT% .*LISTENING\" >nul && goto :wait";

    /// <summary>A keep-alive script whose DevKit does not start: it ends at once.</summary>
    public const string FailingLoop = "@exit /b 1";

    /// <summary>A keep-alive script that runs the built DevKit on the switch's port, in the environment the switch gives it.</summary>
    public static string DevKitLoop =>
        $"@cd /d \"{AppContext.BaseDirectory}\" & \"{Path.Combine(AppContext.BaseDirectory, "DevKit.Web.exe")}\" --urls http://127.0.0.1:%DEVKIT_PORT%";

    /// <summary>
    /// Makes package folder <paramref name="folder"/>: a program file, which the switch only checks is there, and
    /// <paramref name="loop"/> as its devkit-run.bat. One made with the switch (<paramref name="current"/>) has a
    /// package.txt with the hash of each file, and its DevKit takes the port from the maintenance page.
    /// </summary>
    public static string Add(string folder, string loop, bool current = true)
    {
        Directory.CreateDirectory(Path.Combine(folder, "app"));
        File.WriteAllText(Path.Combine(folder, "app", "DevKit.Web.exe"), "");
        File.WriteAllText(Path.Combine(folder, "devkit-run.bat"), loop + "\r\n");
        if (current)
        {
            PackageManifest.Write(folder);
        }

        return folder;
    }

    /// <summary>
    /// Leaves <paramref name="release"/> to this account, SYSTEM and administrators, as tools\devkit-lock.bat does: the
    /// switch refuses a Release folder anyone else may change, and the test folders are made where everyone may.
    /// </summary>
    public static void Lock(string release)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var security = new DirectorySecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        foreach (var account in new[]
                 {
                     new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
                     new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null),
                     WindowsIdentity.GetCurrent().User!
                 })
        {
            security.AddAccessRule(new FileSystemAccessRule(account, FileSystemRights.FullControl,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        }

        new DirectoryInfo(release).SetAccessControl(security);
    }

    /// <summary>Lets every signed-in account change <paramref name="path"/>, as Windows does on a new drive.</summary>
    public static void LetEveryoneChange(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            LetChange(path, new SecurityIdentifier(WellKnownSidType.AuthenticatedUserSid, null));
        }
    }

    /// <summary>Lets <paramref name="account"/> change <paramref name="path"/>, and what a folder holds, made or yet to be.</summary>
    [SupportedOSPlatform("windows")]
    public static void LetChange(string path, SecurityIdentifier account)
    {
        if (Directory.Exists(path))
        {
            var directory = new DirectoryInfo(path);
            var security = directory.GetAccessControl();
            security.AddAccessRule(new FileSystemAccessRule(account, FileSystemRights.Modify,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
            directory.SetAccessControl(security);
            return;
        }

        var file = new FileInfo(path);
        var fileSecurity = file.GetAccessControl();
        fileSecurity.AddAccessRule(new FileSystemAccessRule(account, FileSystemRights.Modify, AccessControlType.Allow));
        file.SetAccessControl(fileSecurity);
    }
}
