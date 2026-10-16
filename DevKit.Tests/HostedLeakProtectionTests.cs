using System.Net;
using System.Runtime.Versioning;
using DevKit.Web.Services;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DevKit.Tests;

/// <summary>
/// What keeps a password or a session from leaking out of shared DevKit: a typed password is taken only where it never
/// crosses the network readable, and a session cookie works only from the address it signed in from.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class HostedLeakProtectionTests : IDisposable
{
    private const string Windows = "windows";
    private static readonly SignedInUser Alice = new("S-1-5-21-1-2-3-1001", @"MT\alice", IsOwner: false);
    private static readonly IPAddress OtherMachine = IPAddress.Parse("10.1.2.3");

    private readonly string _auditFolder = Path.Combine(AppContext.BaseDirectory, "audit-" + Guid.NewGuid().ToString("N"));
    private readonly ServiceProvider _services;
    private readonly SignInAudit _audit;
    private readonly SessionRegistry _sessions = new(new ManualClock());
    private readonly FakeCredentialChecker _checker = new();

    public HostedLeakProtectionTests()
    {
        var services = new ServiceCollection().AddLogging();
        // Keys in memory only: the tests never write Data Protection keys into the profile of whoever runs them.
        services.AddDataProtection().UseEphemeralDataProtectionProvider();
        services.AddAntiforgery();
        services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme).AddCookie();
        _services = services.BuildServiceProvider();
        _audit = new SignInAudit(_auditFolder, NullLogger<SignInAudit>.Instance);
    }

    public void Dispose()
    {
        _audit.Dispose();
        _services.Dispose();
        if (Directory.Exists(_auditFolder))
        {
            Directory.Delete(_auditFolder, recursive: true);
        }
    }

    private SignInFlow Flow() => new(
        new DevKitHostingOptions { Mode = DevKitMode.Hosted, Domain = "MT" },
        _services.GetRequiredService<IAntiforgery>(),
        _sessions,
        new WindowsSignIn(_checker, new SignInThrottle(new ManualClock()), OwnerAccount.Current()),
        _audit);

    private DefaultHttpContext Request(string from, bool https = false)
    {
        var context = new DefaultHttpContext { RequestServices = _services };
        context.Connection.RemoteIpAddress = IPAddress.Parse(from);
        context.Request.IsHttps = https;
        return context;
    }

    private string AuditText() =>
        string.Concat(Directory.GetFiles(_auditFolder).Select(File.ReadAllText));

    [Theory]
    [InlineData("::ffff:10.1.2.3", "10.1.2.3")]
    [InlineData("::ffff:127.0.0.1", "127.0.0.1")]
    [InlineData("10.1.2.3", "10.1.2.3")]
    [InlineData("fe80::1", "fe80::1")]
    public void An_IPv4_client_of_a_listener_on_every_address_is_seen_as_its_IPv4_address(string arrived, string client)
    {
        Assert.Equal(IPAddress.Parse(client), LocalAccessGate.ClientAddress(Request(arrived)));
    }

    [Theory]
    [InlineData("10.1.2.3", false, false)]
    [InlineData("::ffff:10.1.2.3", false, false)]
    [InlineData("10.1.2.3", true, true)]
    [InlineData("127.0.0.1", false, true)]
    [InlineData("::ffff:127.0.0.1", false, true)]
    [InlineData("::1", false, true)]
    public void A_password_is_taken_only_over_https_or_on_this_machine(string from, bool https, bool accepted)
    {
        Assert.Equal(accepted, SignInFlow.AcceptsPassword(Request(from, https)));
    }

    [Fact]
    public void Without_https_the_page_has_no_password_field_and_says_why()
    {
        var model = new SignInPage.Model("MT", "/", "__RequestVerificationToken", "token", "", "", AcceptsPassword: false);

        var page = SignInPage.Render(model);
        var withForm = SignInPage.Render(model with { AcceptsPassword = true });

        Assert.DoesNotContain("type=\"password\"", page, StringComparison.Ordinal);
        Assert.DoesNotContain(SignInFlow.PasswordPath, page, StringComparison.Ordinal);
        Assert.Contains("Typing a password is turned off here", page, StringComparison.Ordinal);
        Assert.Contains(SignInFlow.WindowsPath, page, StringComparison.Ordinal);
        Assert.Contains("type=\"password\"", withForm, StringComparison.Ordinal);
        Assert.DoesNotContain("Typing a password is turned off here", withForm, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_password_posted_over_plain_http_from_another_machine_is_refused_before_Windows_sees_it()
    {
        _checker.Add("alice", "right", Alice);
        var context = Request(OtherMachine.ToString());

        var result = await Flow().SignInWithPasswordAsync(context, new PasswordSignInForm { Username = "alice", Password = "right" });

        var page = Assert.IsType<ContentHttpResult>(result);
        Assert.Equal(StatusCodes.Status403Forbidden, page.StatusCode);
        Assert.DoesNotContain("type=\"password\"", page.ResponseContent, StringComparison.Ordinal);
        Assert.Equal(0, _checker.Calls);
        Assert.DoesNotContain(
            CookieAuthenticationDefaults.CookiePrefix + CookieAuthenticationDefaults.AuthenticationScheme,
            context.Response.Headers.SetCookie.ToString(),
            StringComparison.Ordinal);
        var audit = AuditText();
        Assert.Contains("password sent over plain http from another machine", audit, StringComparison.Ordinal);
        Assert.DoesNotContain("right", audit, StringComparison.Ordinal);
    }

    [Fact]
    public async Task On_this_machine_a_password_still_signs_in_over_plain_http()
    {
        _checker.Add("alice", "right", Alice);

        var result = await Flow().SignInWithPasswordAsync(Request("::ffff:127.0.0.1"), new PasswordSignInForm { Username = "alice", Password = "right" });

        Assert.IsType<RedirectHttpResult>(result);
        Assert.Equal(1, _checker.Calls);
    }

    [Fact]
    public void A_session_works_only_from_the_address_it_signed_in_from()
    {
        var (principal, _) = _sessions.Start(Alice, Windows, TimeSpan.FromDays(5), OtherMachine);

        Assert.True(SessionClaims.CameFrom(principal, IPAddress.Parse("10.1.2.3")));
        Assert.False(SessionClaims.CameFrom(principal, IPAddress.Parse("10.1.2.4")));
        Assert.False(SessionClaims.CameFrom(principal, IPAddress.Loopback));
        Assert.False(SessionClaims.CameFrom(principal, null));
    }

    [Fact]
    public void Every_loopback_address_is_the_same_machine()
    {
        var (principal, _) = _sessions.Start(Alice, Windows, TimeSpan.FromDays(5), IPAddress.Loopback);

        Assert.True(SessionClaims.CameFrom(principal, IPAddress.IPv6Loopback));
        Assert.True(SessionClaims.CameFrom(principal, IPAddress.Parse("127.0.0.2")));
        Assert.False(SessionClaims.CameFrom(principal, OtherMachine));
    }

    private CookieValidatePrincipalContext SessionCheck(HttpContext context, IPAddress signedInFrom)
    {
        var (principal, _) = _sessions.Start(Alice, Windows, TimeSpan.FromDays(5), signedInFrom);
        return new CookieValidatePrincipalContext(
            context,
            new AuthenticationScheme(CookieAuthenticationDefaults.AuthenticationScheme, null, typeof(CookieAuthenticationHandler)),
            new CookieAuthenticationOptions(),
            new AuthenticationTicket(principal, CookieAuthenticationDefaults.AuthenticationScheme));
    }

    [Fact]
    public async Task A_session_cookie_used_from_another_address_is_refused_deleted_and_audited()
    {
        var context = Request("::1");
        var check = SessionCheck(context, OtherMachine);

        await Flow().ValidateSessionAsync(check);

        Assert.Null(check.Principal);
        Assert.Contains("expires=Thu, 01 Jan 1970", context.Response.Headers.SetCookie.ToString(), StringComparison.OrdinalIgnoreCase);
        var audit = AuditText();
        Assert.Contains("refused session", audit, StringComparison.Ordinal);
        Assert.Contains("signed in from 10.1.2.3, used from another address", audit, StringComparison.Ordinal);
        Assert.Contains(@"MT\alice@", audit, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_session_cookie_used_from_its_own_address_is_kept()
    {
        var context = Request("::ffff:127.0.0.1");
        var check = SessionCheck(context, IPAddress.IPv6Loopback);

        await Flow().ValidateSessionAsync(check);

        Assert.NotNull(check.Principal);
        Assert.Empty(context.Response.Headers.SetCookie.ToString());
        Assert.False(Directory.Exists(_auditFolder));
    }

    [Fact]
    public async Task A_signed_out_session_is_refused_wherever_it_comes_from()
    {
        var context = Request("127.0.0.1");
        var check = SessionCheck(context, IPAddress.Loopback);
        _sessions.SignOut(check.Principal!);

        await Flow().ValidateSessionAsync(check);

        Assert.Null(check.Principal);
        Assert.False(Directory.Exists(_auditFolder));
    }
}
