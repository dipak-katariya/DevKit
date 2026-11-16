using System.Net;
using System.Net.Sockets;
using System.Runtime.Versioning;
using System.Security.Principal;
using DevKit.Web.Services;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace DevKit.Tests;

/// <summary>
/// package.txt: every file of a package hashed when it is made, and any file changed, added or missing since then
/// found before the switch runs anything.
/// </summary>
public sealed class PackageManifestTests : IDisposable
{
    private readonly string _package = Path.Combine(AppContext.BaseDirectory, "manifest", Guid.NewGuid().ToString("N"));

    public PackageManifestTests()
    {
        Directory.CreateDirectory(Path.Combine(_package, "app", "wwwroot"));
        File.WriteAllText(Path.Combine(_package, "app", "DevKit.Web.exe"), "program");
        File.WriteAllText(Path.Combine(_package, "app", "wwwroot", "app.css"), "body{}");
        File.WriteAllText(Path.Combine(_package, "devkit-run.bat"), "@echo off\r\n");
    }

    [Fact]
    public void A_package_as_it_was_made_matches_its_manifest()
    {
        PackageManifest.Write(_package);

        var lines = File.ReadAllLines(Path.Combine(_package, DevKitSwitch.ManifestFileName));
        Assert.Equal("files=3", lines[0]);
        Assert.Equal("bytes=" + (7 + 6 + 11), lines[1]);
        Assert.Contains(lines, line => line.StartsWith("sha256=", StringComparison.Ordinal) && line.EndsWith(" app\\wwwroot\\app.css", StringComparison.Ordinal));
        Assert.Equal("", PackageManifest.Problem(_package));
    }

    [Fact]
    public void A_file_changed_after_the_package_was_made_is_found()
    {
        PackageManifest.Write(_package);
        File.WriteAllText(Path.Combine(_package, "app", "DevKit.Web.exe"), "PROGRAM");

        Assert.Equal("app\\DevKit.Web.exe is not the file the package was made with", PackageManifest.Problem(_package));
    }

    [Fact]
    public void A_file_added_or_missing_since_is_found()
    {
        PackageManifest.Write(_package);
        File.WriteAllText(Path.Combine(_package, "app", "version.dll"), "planted");

        Assert.Equal("it has app\\version.dll, which package.txt does not list", PackageManifest.Problem(_package));

        File.Delete(Path.Combine(_package, "app", "version.dll"));
        File.Delete(Path.Combine(_package, "app", "wwwroot", "app.css"));

        Assert.Equal("app\\wwwroot\\app.css is missing", PackageManifest.Problem(_package));
    }

    [Fact]
    public void The_files_the_package_writes_itself_are_left_out()
    {
        PackageManifest.Write(_package);
        File.WriteAllText(Path.Combine(_package, DevKitSwitch.FailedFileName), "failed");

        Assert.Equal("", PackageManifest.Problem(_package));
    }

    [Fact]
    public void A_manifest_without_hashes_is_refused_and_a_package_without_one_is_left_to_the_scripts()
    {
        Assert.Equal("", PackageManifest.Problem(_package));

        File.WriteAllText(Path.Combine(_package, DevKitSwitch.ManifestFileName), "files=3\r\nbytes=24\r\n");

        Assert.Contains("lists no hashes", PackageManifest.Problem(_package), StringComparison.Ordinal);
    }

    [Fact]
    public void The_program_writes_the_manifest_of_the_folder_it_is_given()
    {
        Assert.Equal(0, PackageManifest.Run([PackageManifest.Option, _package]));
        Assert.True(File.Exists(Path.Combine(_package, DevKitSwitch.ManifestFileName)));
        Assert.Equal(1, PackageManifest.Run([PackageManifest.Option, Path.Combine(_package, "none")]));
        Assert.Equal(1, PackageManifest.Run([PackageManifest.Option]));
    }

    [Fact]
    public void A_self_contained_package_on_a_runtime_without_the_latest_security_fixes_gets_no_manifest()
    {
        CarryRuntime("10.0.13", "10.0.13");

        Assert.Contains("Microsoft.NETCore.App 10.0.13", PackageManifest.RuntimeProblem(_package), StringComparison.Ordinal);
        Assert.Equal(1, PackageManifest.Run([PackageManifest.Option, _package]));
        Assert.False(File.Exists(Path.Combine(_package, DevKitSwitch.ManifestFileName)));
    }

    [Fact]
    public void One_framework_behind_is_enough_to_refuse_the_package()
    {
        CarryRuntime(PackageManifest.PatchedRuntime.ToString(), "10.0.13");

        Assert.Contains("Microsoft.AspNetCore.App 10.0.13", PackageManifest.RuntimeProblem(_package), StringComparison.Ordinal);
    }

    [Fact]
    public void A_self_contained_package_on_the_patched_runtime_gets_its_manifest()
    {
        var patched = PackageManifest.PatchedRuntime.ToString();
        CarryRuntime(patched, patched);

        Assert.Equal("", PackageManifest.RuntimeProblem(_package));
        Assert.Equal(0, PackageManifest.Run([PackageManifest.Option, _package]));
        Assert.True(File.Exists(Path.Combine(_package, DevKitSwitch.ManifestFileName)));
    }

    // 10.0.14 (2026-10-25) is the first .NET 10 release with the fixes for CVE-2026-70197 and CVE-2026-111497.
    [Fact]
    public void The_patched_runtime_comes_from_the_project_and_never_drops_below_the_october_fixes()
    {
        Assert.True(PackageManifest.PatchedRuntime >= new Version(10, 0, 14), PackageManifest.PatchedRuntime.ToString());
    }

    /// <summary>Makes the package self-contained, carrying these versions of .NET and ASP.NET Core.</summary>
    private void CarryRuntime(string core, string aspNetCore)
    {
        File.WriteAllText(Path.Combine(_package, "app", "coreclr.dll"), "runtime");
        File.WriteAllText(Path.Combine(_package, "app", "DevKit.Web.runtimeconfig.json"),
            $$"""
            { "runtimeOptions": { "tfm": "net10.0", "includedFrameworks": [
              { "name": "Microsoft.NETCore.App", "version": "{{core}}" },
              { "name": "Microsoft.AspNetCore.App", "version": "{{aspNetCore}}" } ] } }
            """);
    }

    public void Dispose() => Directory.Delete(_package, recursive: true);
}

/// <summary>Who besides this account, SYSTEM and administrators may change a folder, and so run code as DevKit's account.</summary>
[SupportedOSPlatform("windows")]
public sealed class FolderAccessTests : IDisposable
{
    private readonly string _folder = Path.Combine(AppContext.BaseDirectory, "access", Guid.NewGuid().ToString("N"));

    public FolderAccessTests()
    {
        Directory.CreateDirectory(_folder);
        ScratchPackages.Lock(_folder);
        Directory.CreateDirectory(Path.Combine(_folder, "app"));
        File.WriteAllText(Path.Combine(_folder, "app", "DevKit.Web.exe"), "");
    }

    [Fact]
    public void A_locked_folder_has_no_other_writer()
    {
        Assert.Null(FolderAccess.OtherWriter(_folder, AccessScope.Folder));
        Assert.Null(FolderAccess.OtherWriter(_folder, AccessScope.FolderAndContents));
    }

    [Fact]
    public void A_folder_every_signed_in_account_may_change_is_named_with_who()
    {
        ScratchPackages.LetEveryoneChange(_folder);

        var writer = FolderAccess.OtherWriter(_folder, AccessScope.Folder);

        Assert.NotNull(writer);
        Assert.Contains("may change " + _folder, writer, StringComparison.Ordinal);
    }

    [Fact]
    public void A_file_in_it_others_may_change_counts_only_with_the_contents()
    {
        var program = Path.Combine(_folder, "app", "DevKit.Web.exe");
        ScratchPackages.LetEveryoneChange(program);

        Assert.Null(FolderAccess.OtherWriter(_folder, AccessScope.Folder));
        Assert.Contains("may change " + program, FolderAccess.OtherWriter(_folder, AccessScope.FolderAndContents), StringComparison.Ordinal);
    }

    [Fact]
    public void An_account_that_administers_this_machine_may_change_it()
    {
        var account = new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null);
        ScratchPackages.LetChange(_folder, account);

        Assert.Null(FolderAccess.OtherWriter(_folder, AccessScope.FolderAndContents, sid => sid == account));
        Assert.Contains("may change " + _folder, FolderAccess.OtherWriter(_folder, AccessScope.FolderAndContents, _ => false), StringComparison.Ordinal);
    }

    [Fact]
    public void Each_other_account_is_looked_up_once_for_all_a_folder_holds()
    {
        ScratchPackages.LetChange(_folder, new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null));
        var lookups = 0;

        var writer = FolderAccess.OtherWriter(_folder, AccessScope.FolderAndContents, _ =>
        {
            lookups++;
            return true;
        });

        Assert.Null(writer);
        Assert.Equal(1, lookups);
    }

    [Fact]
    public void A_folder_the_machines_own_Administrator_account_may_change_is_trusted()
    {
        // As an IT account given the whole profile: a member of Administrators may take any file over anyway.
        ScratchPackages.LetChange(_folder, LocalAdministratorsTests.MachineAdministrator());

        Assert.Null(FolderAccess.OtherWriter(_folder, AccessScope.FolderAndContents));
    }

    public void Dispose() => Directory.Delete(_folder, recursive: true);
}

/// <summary>Who administers this machine, as Windows says: its own Administrator account does, the groups everyone is in do not.</summary>
[SupportedOSPlatform("windows")]
public sealed class LocalAdministratorsTests
{
    /// <summary>
    /// Account 500 of the machine's own domain, which Windows keeps in Administrators whatever it is named. The domain is
    /// read from DefaultAccount, which every Windows since Server 2016 has.
    /// </summary>
    internal static SecurityIdentifier MachineAdministrator()
    {
        var builtIn = (SecurityIdentifier)new NTAccount(Environment.MachineName, "DefaultAccount").Translate(typeof(SecurityIdentifier));
        return new SecurityIdentifier(WellKnownSidType.AccountAdministratorSid, builtIn.AccountDomainSid);
    }

    [Fact]
    public void The_machines_own_Administrator_account_administers_it() =>
        Assert.True(LocalAdministrators.Includes(MachineAdministrator()));

    [Theory]
    [InlineData(WellKnownSidType.BuiltinUsersSid)]
    [InlineData(WellKnownSidType.AuthenticatedUserSid)]
    [InlineData(WellKnownSidType.WorldSid)]
    [InlineData(WellKnownSidType.NetworkServiceSid)]
    public void Groups_everyone_is_in_do_not_administer_it(WellKnownSidType group) =>
        Assert.False(LocalAdministrators.Includes(new SecurityIdentifier(group, null)));
}

/// <summary>
/// DevKit's own folder, which holds devkit.json, the keys that sign people in and the sign-outs: shared DevKit starts
/// only when nobody but this account, SYSTEM and administrators may change it.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class DevKitFolderTests : IDisposable
{
    private readonly string _parent = Path.Combine(AppContext.BaseDirectory, "devkit-folder", Guid.NewGuid().ToString("N"));

    private string Folder => Path.Combine(_parent, "DevKit");

    [Fact]
    public void A_missing_folder_is_made_closed_to_others()
    {
        Directory.CreateDirectory(_parent);
        ScratchPackages.LetEveryoneChange(_parent);

        DevKitMachineConfig.EnsureClosed(Folder);

        Assert.True(Directory.Exists(Folder));
        Assert.Null(DevKitMachineConfig.OpenToOthers(Folder));
    }

    [Fact]
    public void A_folder_others_may_change_stops_shared_DevKit_and_says_how_to_close_it()
    {
        Directory.CreateDirectory(Folder);
        ScratchPackages.LetEveryoneChange(Folder);

        var refused = Assert.Throws<InvalidOperationException>(() => DevKitMachineConfig.EnsureClosed(Folder));

        Assert.Contains("may change " + Folder, refused.Message, StringComparison.Ordinal);
        Assert.Contains("devkit-lock.bat", refused.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_file_in_it_others_may_change_counts_too()
    {
        Directory.CreateDirectory(Folder);
        ScratchPackages.Lock(Folder);
        var config = Path.Combine(Folder, DevKitMachineConfig.FileName);
        File.WriteAllText(config, "{}");
        ScratchPackages.LetEveryoneChange(config);

        Assert.Contains("may change " + config, DevKitMachineConfig.OpenToOthers(Folder), StringComparison.Ordinal);
    }

    [Fact]
    public void A_closed_folder_is_trusted_and_a_link_is_not()
    {
        Directory.CreateDirectory(Folder);
        ScratchPackages.Lock(Folder);
        Assert.Null(DevKitMachineConfig.OpenToOthers(Folder));

        SharedSettingsScratch.MakeJunction(Link, Folder);

        Assert.Contains("is a link", DevKitMachineConfig.OpenToOthers(Link), StringComparison.Ordinal);
    }

    private string Link => Path.Combine(_parent, "Linked");

    public void Dispose()
    {
        // The link first, while what it points to is still there.
        if (Directory.Exists(Link))
        {
            Directory.Delete(Link);
        }

        if (Directory.Exists(_parent))
        {
            Directory.Delete(_parent, recursive: true);
        }
    }
}

/// <summary>
/// The addresses DevKit listens on, as Kestrel reads them: the switch refuses one that leaves out its port, and shared
/// DevKit refuses plain http that other machines reach unless devkit.json allows it.
/// </summary>
public sealed class HostEndpointsTests
{
    private static IConfiguration Configuration(params (string Key, string Value)[] settings) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(settings.Select(setting => new KeyValuePair<string, string?>(setting.Key, setting.Value)))
            .Build();

    [Fact]
    public void Kestrels_endpoints_win_over_urls_and_without_either_it_is_Kestrels_default()
    {
        var both = Configuration(("urls", "http://*:5850"), ("Kestrel:Endpoints:Https:Url", "https://*:5860"));

        Assert.Equal([5860], HostEndpoints.Read(both).Select(address => address.Port));
        Assert.Equal([5850, 5851], HostEndpoints.Read(Configuration(("urls", "http://*:5850; http://localhost:5851"))).Select(address => address.Port));
        Assert.Equal([5000], HostEndpoints.Read(Configuration()).Select(address => address.Port));
    }

    [Fact]
    public void An_address_that_leaves_out_the_switched_port_is_refused()
    {
        var configuration = Configuration(("Kestrel:Endpoints:Https:Url", "https://*:5855"));

        var refused = Assert.Throws<InvalidOperationException>(() => HostEndpoints.EnsureServes(configuration, 5854));

        Assert.Contains("but not on port 5854", refused.Message, StringComparison.Ordinal);
        HostEndpoints.EnsureServes(configuration, 5855);
    }

    [Theory]
    [InlineData("http://*:5850", true)]
    [InlineData("http://+:5850", true)]
    [InlineData("http://10.0.0.5:5850", true)]
    [InlineData("https://*:5850", false)]
    [InlineData("http://localhost:5850;http://127.0.0.1:5851;http://[::1]:5852", false)]
    public void Plain_http_other_machines_reach_is_found(string urls, bool reachable) =>
        Assert.Equal(reachable, HostEndpoints.PlainHttpForOthers(Configuration(("urls", urls))) is not null);

    [Fact]
    public void Shared_DevKit_refuses_plain_http_for_other_machines_unless_devkit_json_allows_it()
    {
        var plain = new[]
        {
            ("DevKit:Mode", "Hosted"),
            ("DevKit:TfsUrl", "https://tfs.example.com/tfs/Collection"),
            ("urls", "http://*:5850")
        };

        var refused = Assert.Throws<InvalidOperationException>(() => DevKitHostingOptions.Read(Configuration(plain)));

        Assert.Contains("AllowPlainHttp", refused.Message, StringComparison.Ordinal);
        Assert.True(DevKitHostingOptions.Read(Configuration([.. plain, ("DevKit:AllowPlainHttp", "true")])).AllowPlainHttp);
        Assert.False(DevKitHostingOptions.Read(Configuration(("urls", "http://*:5850"))).IsHosted);
    }
}

/// <summary>What every answer says about the server: https only from then on, when it came over https, and never its name.</summary>
public sealed class SecurityHeaderTests
{
    [Fact]
    public async Task An_answer_over_https_asks_for_https_from_then_on_and_none_names_the_server()
    {
        int port;
        using (var probe = new TcpListener(IPAddress.Loopback, 0))
        {
            probe.Start();
            port = ((IPEndPoint)probe.LocalEndpoint).Port;
        }

        var builder = WebApplication.CreateBuilder(["--urls", $"http://127.0.0.1:{port}"]);
        DevKitAccess.HideServerName(builder.WebHost);
        await using var app = builder.Build();
        // As a request that came over https looks once Kestrel has decrypted it.
        app.Use((context, next) =>
        {
            context.Request.Scheme = context.Request.Path == "/secure" ? Uri.UriSchemeHttps : Uri.UriSchemeHttp;
            return next(context);
        });
        DevKitAccess.UseSecurityHeaders(app, "no-referrer");
        app.MapGet("/{page}", () => Results.Text("ok"));
        await app.StartAsync();
        using var client = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}") };

        using var secure = await client.GetAsync("/secure");
        using var plain = await client.GetAsync("/plain");

        Assert.Equal("max-age=31536000", secure.Headers.GetValues("Strict-Transport-Security").Single());
        Assert.False(plain.Headers.Contains("Strict-Transport-Security"));
        Assert.False(secure.Headers.Contains("Server"));
        Assert.False(plain.Headers.Contains("Server"));
        await app.StopAsync();
    }
}
