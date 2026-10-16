using System.Globalization;
using System.Net;
using System.Net.Security;
using System.Runtime.Versioning;
using System.Security.Claims;
using System.Security.Principal;
using System.Text;
using DevKit.Web.Services;
using Microsoft.AspNetCore.Connections.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Configuration.EnvironmentVariables;
using Microsoft.Extensions.Configuration.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DevKit.Tests;

/// <summary>
/// The pieces of shared (hosted) DevKit's sign-in: what configuration it needs, where a sign-in may send the browser,
/// how guesses are counted and stopped, how long a session lasts and how it ends, what the audit records, and the
/// Windows single sign-on handshake itself.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class HostedSignInTests : IDisposable
{
    private const string TfsUrl = "https://tfs.example.test/tfs/Collection";
    private static readonly SignedInUser Alice = new("S-1-5-21-1-2-3-1001", @"MT\alice", IsOwner: false);

    private readonly string _auditFolder = Path.Combine(AppContext.BaseDirectory, "audit-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_auditFolder))
        {
            Directory.Delete(_auditFolder, recursive: true);
        }
    }

    private static DevKitHostingOptions Read(params (string Key, string Value)[] settings) =>
        DevKitHostingOptions.Read(new ConfigurationBuilder()
            .AddInMemoryCollection(settings.Select(s => new KeyValuePair<string, string?>(s.Key, s.Value)))
            .Build());

    [Fact]
    public void Without_configuration_DevKit_stays_in_local_mode()
    {
        Assert.Equal(DevKitMode.Local, Read().Mode);
        Assert.False(Read().IsHosted);
    }

    [Fact]
    public void Hosted_mode_fills_in_this_machines_names_domain_and_audit_folder()
    {
        var options = Read(("DevKit:Mode", "hosted"), ("DevKit:TfsUrl", " " + TfsUrl + "/ "));

        Assert.True(options.IsHosted);
        Assert.Equal(TfsUrl, options.TfsUrl);
        Assert.Equal(Environment.UserDomainName, options.Domain);
        Assert.Equal(TimeSpan.FromDays(5), options.SessionLength);
        Assert.Contains(Environment.MachineName, options.HostNames, StringComparer.OrdinalIgnoreCase);
        Assert.Contains("localhost", options.HostNames);
        Assert.Equal(Path.Combine(DevKitMachineConfig.Folder, "audit"), options.AuditFolder);
    }

    [Theory]
    [InlineData("", "5")]
    [InlineData("not a url", "5")]
    [InlineData(TfsUrl, "0")]
    [InlineData(TfsUrl, "31")]
    public void Hosted_mode_refuses_to_start_without_a_valid_server_and_session_length(string url, string days)
    {
        Assert.Throws<InvalidOperationException>(() =>
            Read(("DevKit:Mode", "Hosted"), ("DevKit:TfsUrl", url), ("DevKit:SessionDays", days)));
    }

    /// <summary>
    /// This machine's devkit.json sits above the shipped appsettings.json and below the environment and the command
    /// line, so the machine sets DevKit up and a DevKit__… variable or --DevKit:… argument can still override it.
    /// </summary>
    [Fact]
    public void This_machines_devkit_json_configures_DevKit_and_the_environment_still_overrides_it()
    {
        File.WriteAllText(DevKitMachineConfig.FilePath,
            $$"""{ "DevKit": { "Mode": "Hosted", "TfsUrl": "{{TfsUrl}}", "SessionDays": 7 } }""");
        try
        {
            var configuration = new ConfigurationManager();
            configuration.AddInMemoryCollection(new Dictionary<string, string?> { ["DevKit:SessionDays"] = "2" });
            configuration.Sources.Add(new EnvironmentVariablesConfigurationSource());
            DevKitMachineConfig.AddTo(configuration);

            var fromMachine = DevKitHostingOptions.Read(configuration);
            configuration.AddInMemoryCollection(new Dictionary<string, string?> { ["DevKit:SessionDays"] = "9" });

            var sources = configuration.Sources.ToList();
            Assert.True(sources.FindIndex(s => s is JsonConfigurationSource) < sources.FindIndex(s => s is EnvironmentVariablesConfigurationSource));
            Assert.True(fromMachine.IsHosted);
            Assert.Equal(TfsUrl, fromMachine.TfsUrl);
            Assert.Equal(7, fromMachine.SessionDays);
            Assert.Equal(9, DevKitHostingOptions.Read(configuration).SessionDays);
        }
        finally
        {
            File.Delete(DevKitMachineConfig.FilePath);
        }
    }

    [Fact]
    public void Without_a_devkit_json_DevKit_runs_in_local_mode()
    {
        var configuration = new ConfigurationManager();
        DevKitMachineConfig.AddTo(configuration);

        Assert.False(File.Exists(DevKitMachineConfig.FilePath));
        Assert.False(DevKitHostingOptions.Read(configuration).IsHosted);
    }

    [Theory]
    [InlineData("/settings?tab=1", "/settings?tab=1")]
    [InlineData("/", "/")]
    [InlineData(null, "/")]
    [InlineData("", "/")]
    [InlineData("https://evil.example/", "/")]
    [InlineData("//evil.example/", "/")]
    [InlineData("/\\evil.example/", "/")]
    [InlineData("/ok\nSet-Cookie: x", "/")]
    public void A_sign_in_only_ever_sends_the_browser_on_to_a_page_of_this_site(string? asked, string sentTo)
    {
        Assert.Equal(sentTo, SignInFlow.LocalUrl(asked));
    }

    [Theory]
    [InlineData(@"MT\jdoe", "jdoe", "MT")]
    [InlineData("jdoe", "jdoe", "MT")]
    [InlineData("  jdoe@metatreesystems.com  ", "jdoe@metatreesystems.com", null)]
    public void A_typed_username_is_split_the_way_Windows_reads_it(string typed, string user, string? domain)
    {
        Assert.True(WindowsCredentialChecker.TrySplit(typed, "MT", out var splitUser, out var splitDomain));
        Assert.Equal(user, splitUser);
        Assert.Equal(domain, splitDomain);
    }

    [Theory]
    [InlineData("")]
    [InlineData(@"MT\")]
    [InlineData(@"\jdoe")]
    [InlineData(@"MT\a\b")]
    [InlineData("jd\toe")]
    public void A_username_Windows_could_misread_is_refused_before_Windows_sees_it(string typed)
    {
        Assert.False(WindowsCredentialChecker.TrySplit(typed, "MT", out _, out _));
    }

    [Fact]
    public void Every_spelling_of_a_real_account_counts_as_that_account()
    {
        using var me = WindowsIdentity.GetCurrent();
        var domain = Environment.UserDomainName;
        var checker = new WindowsCredentialChecker(
            new DevKitHostingOptions { Domain = domain }, OwnerAccount.Current(), NullLogger<WindowsCredentialChecker>.Instance);

        var keys = new[] { $@"{domain}\{Environment.UserName}", Environment.UserName, $@"{domain.ToLowerInvariant()}\{Environment.UserName.ToUpperInvariant()}" }
            .Select(checker.AccountKey).Distinct().ToList();

        Assert.Equal(me.User!.Value, Assert.Single(keys));
    }

    [Fact]
    public void Every_spelling_of_a_name_Windows_does_not_know_counts_together()
    {
        var checker = new WindowsCredentialChecker(
            new DevKitHostingOptions { Domain = "MT" }, OwnerAccount.Current(), NullLogger<WindowsCredentialChecker>.Instance);

        var keys = new[] { @"MT\devkit-no-such-user", "devkit-no-such-user", @"mt\DEVKIT-NO-SUCH-USER", " devkit-no-such-user " }
            .Select(checker.AccountKey).Distinct().ToList();

        Assert.Equal(@"MT\DEVKIT-NO-SUCH-USER", Assert.Single(keys));
    }

    [Fact]
    public void Names_whose_window_has_passed_are_forgotten()
    {
        var clock = new ManualClock();
        var throttle = new SignInThrottle(clock);
        throttle.RecordFailure("TYPO-1");
        throttle.RecordFailure("TYPO-2");

        clock.Now += SignInThrottle.Window;
        throttle.RecordFailure("TYPO-3");

        Assert.Equal(1, throttle.Remembered);
    }

    [Fact]
    public void Three_wrong_passwords_stop_further_tries_for_fifteen_minutes()
    {
        var clock = new ManualClock();
        var throttle = new SignInThrottle(clock);

        for (var i = 0; i < SignInThrottle.MaxFailures; i++)
        {
            Assert.False(throttle.IsBlocked(@"MT\JDOE", out _));
            throttle.RecordFailure(@"MT\JDOE");
        }

        clock.Now += TimeSpan.FromMinutes(5);
        Assert.True(throttle.IsBlocked(@"MT\JDOE", out var wait));
        Assert.Equal(TimeSpan.FromMinutes(10), wait);
        Assert.False(throttle.IsBlocked(@"MT\SOMEONE", out _));

        clock.Now += TimeSpan.FromMinutes(10);
        Assert.False(throttle.IsBlocked(@"MT\JDOE", out _));
    }

    [Fact]
    public void A_right_password_clears_the_wrong_ones_before_it()
    {
        var throttle = new SignInThrottle(new ManualClock());
        throttle.RecordFailure(@"MT\JDOE");
        throttle.RecordFailure(@"MT\JDOE");

        throttle.RecordSuccess(@"MT\JDOE");
        throttle.RecordFailure(@"MT\JDOE");
        throttle.RecordFailure(@"MT\JDOE");

        Assert.False(throttle.IsBlocked(@"MT\JDOE", out _));
    }

    [Fact]
    public void Once_an_account_is_stopped_Windows_is_not_even_asked_however_the_name_is_typed()
    {
        var checker = new FakeCredentialChecker();
        checker.Add(@"MT\alice", "right", Alice);
        var windows = new WindowsSignIn(checker, new SignInThrottle(new ManualClock()), new OwnerAccount("S-1-5-21-9"));

        foreach (var typed in new[] { @"MT\alice", "alice", @"mt\ALICE" })
        {
            Assert.Null(windows.CheckPassword(typed, "wrong").User);
        }

        var blocked = windows.CheckPassword("alice", "right");

        Assert.Null(blocked.User);
        Assert.Contains("Too many wrong passwords", blocked.Error, StringComparison.Ordinal);
        Assert.Equal("stopped after too many wrong passwords", blocked.Detail);
        Assert.Equal(3, checker.Calls);
    }

    [Theory]
    [InlineData("http://[::]:5850")]
    [InlineData("http://0.0.0.0:5850")]
    public async Task The_link_to_share_for_http_on_every_address_is_this_machines_name(string listening)
    {
        Assert.Equal($"http://{Environment.MachineName}:5850",
            await HostedAccess.LinkToShareAsync(listening, NullLogger.Instance, CancellationToken.None));
    }

    [Fact]
    public async Task A_named_address_is_shared_as_it_is()
    {
        Assert.Equal("https://devbox.example.com:5850",
            await HostedAccess.LinkToShareAsync("https://devbox.example.com:5850", NullLogger.Instance, CancellationToken.None));
    }

    [Fact]
    public void A_session_lasts_its_days_and_not_a_moment_longer()
    {
        var clock = new ManualClock();
        var sessions = new SessionRegistry(clock);

        var (principal, expires) = sessions.Start(Alice, "windows", TimeSpan.FromDays(5), IPAddress.Loopback);

        Assert.Equal(clock.Now + TimeSpan.FromDays(5), expires);
        Assert.Equal(expires, SessionClaims.ExpiresAt(principal));
        Assert.Equal(Alice.Id, principal.FindFirstValue(ClaimTypes.NameIdentifier));
        Assert.True(sessions.IsActive(principal));

        clock.Now = expires - TimeSpan.FromSeconds(1);
        Assert.True(sessions.IsActive(principal));
        clock.Now = expires;
        Assert.False(sessions.IsActive(principal));
    }

    [Fact]
    public void Signing_out_ends_that_session_everywhere_and_no_other()
    {
        var sessions = new SessionRegistry(new ManualClock());
        var (signedOut, _) = sessions.Start(Alice, "windows", TimeSpan.FromDays(5), IPAddress.Loopback);
        var (otherBrowser, _) = sessions.Start(Alice, "password", TimeSpan.FromDays(5), IPAddress.Loopback);

        sessions.SignOut(signedOut);

        Assert.False(sessions.IsActive(signedOut));
        Assert.True(sessions.IsActive(otherBrowser));
    }

    [Fact]
    public void A_principal_that_is_not_a_DevKit_session_is_never_active()
    {
        var sessions = new SessionRegistry(new ManualClock());

        Assert.False(sessions.IsActive(new ClaimsPrincipal(new ClaimsIdentity())));
        Assert.False(sessions.IsActive(new ClaimsPrincipal(new ClaimsIdentity(new[] { new Claim(ClaimTypes.Name, "x") }, "Cookies"))));
    }

    [Fact]
    public void Typed_text_cannot_forge_an_audit_line()
    {
        Assert.Equal(@"MT\alicesign-inwindows", SignInAudit.Clean("MT\\alice\tsign-in\r\nwindows"));
        Assert.Equal(200, SignInAudit.Clean(new string('x', 500)).Length);
    }

    [Fact]
    public async Task The_audit_records_user_at_machine_one_line_per_event_with_no_password()
    {
        using var audit = new SignInAudit(_auditFolder, NullLogger<SignInAudit>.Instance);
        var context = new DefaultHttpContext();
        context.Connection.RemoteIpAddress = IPAddress.Loopback;

        await audit.RecordAsync(context, "sign-in", "windows", @"MT\alice");
        await audit.RecordAsync(context, "failed", "password", "MT\\mallory\tforged", "wrong username or password");

        var file = Assert.Single(Directory.GetFiles(_auditFolder));
        Assert.Equal(string.Format(CultureInfo.InvariantCulture, "signin-{0:yyyy-MM}.log", DateTime.UtcNow), Path.GetFileName(file));
        var bytes = await File.ReadAllBytesAsync(file);
        Assert.NotEqual(0xEF, bytes[0]);
        var lines = Encoding.UTF8.GetString(bytes).Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(2, lines.Length);
        Assert.Equal(new[] { "sign-in", "windows", $@"MT\alice@{Environment.MachineName}", "127.0.0.1", "" }, lines[0].Split('\t')[1..]);
        Assert.Equal(6, lines[1].Split('\t').Length);
        Assert.Contains(@"MT\malloryforged@", lines[1], StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_audit_line_that_cannot_be_written_is_logged_and_the_sign_in_goes_on()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_auditFolder)!);
        await File.WriteAllTextAsync(_auditFolder, "a file where the folder should be");
        var logger = new RecordingLogger<SignInAudit>();
        using var audit = new SignInAudit(_auditFolder, logger);
        try
        {
            await audit.RecordAsync(new DefaultHttpContext(), "sign-in", "windows", @"MT\alice");

            Assert.Contains(logger.Entries, e => e.Level == LogLevel.Error && e.Exception is IOException);
        }
        finally
        {
            File.Delete(_auditFolder);
        }
    }

    private static DefaultHttpContext SignOnRequest(FakeConnectionItems connection, string? authorization = null)
    {
        var context = new DefaultHttpContext();
        context.Features.Set<IConnectionItemsFeature>(connection);
        if (authorization is not null)
        {
            context.Request.Headers.Authorization = authorization;
        }

        return context;
    }

    private static WindowsSignIn SingleSignOn() =>
        new(new FakeCredentialChecker(), new SignInThrottle(new ManualClock()), OwnerAccount.Current());

    [Theory]
    [InlineData(null)]
    [InlineData("Negotiate YIIBhgYGKwYBBQUCoIIBejCCAXagMDAuBgkqhkiC9xIBAgIGCSqGSIb3EgECAgYKKwYBBAGCNwICHgYKKwYBBAGCNwICCg==")]
    [InlineData("Basic YWxpY2U6c2VjcmV0")]
    public void A_request_without_an_NTLM_message_is_asked_for_one(string? authorization)
    {
        var context = SignOnRequest(new FakeConnectionItems(), authorization);

        var step = SingleSignOn().ContinueSingleSignOn(context);

        Assert.Equal(SingleSignOnState.Challenge, step.State);
        Assert.Equal("NTLM", context.Response.Headers.WWWAuthenticate.ToString());
    }

    [Fact]
    public void An_NTLM_message_that_is_not_base64_fails_without_reaching_Windows()
    {
        var step = SingleSignOn().ContinueSingleSignOn(SignOnRequest(new FakeConnectionItems(), "NTLM %%%not-base64%%%"));

        Assert.Equal(SingleSignOnState.Failed, step.State);
        Assert.Null(step.User);
    }

    [Fact]
    public void A_proof_on_a_connection_that_never_started_a_handshake_is_refused()
    {
        var connection = new FakeConnectionItems();
        var forgedAuthenticate = "NTLM " + Convert.ToBase64String(Encoding.ASCII.GetBytes("NTLMSSP\0\u0003\0\0\0" + new string('\0', 64)));

        var step = SingleSignOn().ContinueSingleSignOn(SignOnRequest(connection, forgedAuthenticate));

        Assert.Equal(SingleSignOnState.Failed, step.State);
        Assert.Empty(connection.Items);
    }

    /// <summary>
    /// The whole handshake, run against Windows in this process as the account running the tests: the browser's part
    /// is played by .NET's own NTLM client. The person signed in is exactly that account, and the handshake leaves
    /// nothing behind on the connection.
    /// </summary>
    [Fact]
    public void A_full_NTLM_handshake_signs_in_the_Windows_account_that_answered()
    {
        var windows = SingleSignOn();
        var connection = new FakeConnectionItems();
        using var browser = new NegotiateAuthentication(new NegotiateAuthenticationClientOptions
        {
            Package = "NTLM",
            Credential = CredentialCache.DefaultNetworkCredentials,
            TargetName = "HTTP/localhost"
        });

        Assert.Equal(SingleSignOnState.Challenge, windows.ContinueSingleSignOn(SignOnRequest(connection)).State);
        var second = SignOnRequest(connection, "NTLM " + Convert.ToBase64String(browser.GetOutgoingBlob(ReadOnlySpan<byte>.Empty, out _)!));
        Assert.Equal(SingleSignOnState.Continue, windows.ContinueSingleSignOn(second).State);
        var authenticate = browser.GetOutgoingBlob(
            Convert.FromBase64String(second.Response.Headers.WWWAuthenticate.ToString()["NTLM ".Length..]), out _)!;
        var done = windows.ContinueSingleSignOn(SignOnRequest(connection, "NTLM " + Convert.ToBase64String(authenticate)));

        using var me = WindowsIdentity.GetCurrent();
        Assert.Equal(SingleSignOnState.Completed, done.State);
        Assert.Equal(me.User!.Value, done.User!.Id);
        Assert.Equal(me.Name, done.User.Name);
        Assert.True(done.User.IsOwner);
        Assert.Empty(connection.Items);
    }
}
