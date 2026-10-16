using System.Globalization;
using System.Net;
using System.Text.RegularExpressions;
using Xunit;

namespace DevKit.Tests;

/// <summary>
/// Shared (hosted) DevKit checked over HTTP against the real app: every page needs a sign-in, Windows single sign-on
/// starts a five-day session, a wrong password is refused and audited, and other sites, other host names and requests
/// without the page's own form token get nowhere.
/// </summary>
public sealed partial class HostedEndToEndTests : IClassFixture<HostedDevKitProcess>
{
    private const string SessionCookie = "devkit-session";

    private readonly HostedDevKitProcess _devKit;

    public HostedEndToEndTests(HostedDevKitProcess devKit) => _devKit = devKit;

    /// <summary>A client that signs in as the Windows account running the tests when DevKit asks.</summary>
    private HttpClient WindowsClient() =>
        new(new HttpClientHandler { UseCookies = false, AllowAutoRedirect = false, Credentials = CredentialCache.DefaultCredentials })
        {
            BaseAddress = _devKit.BaseAddress
        };

    /// <summary>Everything the hosted DevKit's audit has recorded so far.</summary>
    private string AuditText() => string.Join("\n", Directory.GetFiles(_devKit.AuditFolder).Select(File.ReadAllText));

    private async Task<string> SignInWithWindowsAsync()
    {
        using var client = WindowsClient();
        using var response = await client.GetAsync("/signin/windows?returnUrl=%2Fsettings");
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        return response.Headers.GetValues("Set-Cookie").Single(c => c.StartsWith(SessionCookie + "=", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_page_without_a_sign_in_goes_to_the_sign_in_page()
    {
        using var client = _devKit.NewClient();

        using var response = await client.GetAsync("/settings");

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal("/signin?returnUrl=%2Fsettings", response.Headers.Location!.PathAndQuery);
    }

    [Fact]
    public async Task The_sign_in_page_offers_Windows_and_a_password_and_no_token()
    {
        using var client = _devKit.NewClient();

        var html = await client.GetStringAsync("/signin");

        Assert.Contains("Sign in with your Windows account", html, StringComparison.Ordinal);
        Assert.Contains("name=\"username\"", html, StringComparison.Ordinal);
        Assert.Contains("type=\"password\"", html, StringComparison.Ordinal);
        Assert.Contains("__RequestVerificationToken", html, StringComparison.Ordinal);
        Assert.DoesNotContain("Sign in with token", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(DevKitProcess.Pat, html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Windows_single_sign_on_starts_a_five_day_session_and_is_audited()
    {
        var before = DateTimeOffset.UtcNow;

        var cookie = await SignInWithWindowsAsync();

        var expires = DateTimeOffset.Parse(ExpiresAttribute().Match(cookie).Groups["at"].Value, CultureInfo.InvariantCulture);
        Assert.InRange(expires, before.AddDays(5).AddMinutes(-1), DateTimeOffset.UtcNow.AddDays(5).AddMinutes(1));
        Assert.Contains("httponly", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("samesite=lax", cookie, StringComparison.OrdinalIgnoreCase);

        using var client = _devKit.NewClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/settings");
        request.Headers.Add("Cookie", cookie.Split(';')[0]);
        using var page = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, page.StatusCode);

        Assert.Contains($"\tsign-in\twindows\t{Environment.UserDomainName}\\{Environment.UserName}@", AuditText(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task A_wrong_password_is_refused_without_saying_why_and_the_reason_is_audited()
    {
        using var client = _devKit.NewClient();
        using var page = await client.GetAsync("/signin");
        var antiforgeryCookie = page.Headers.GetValues("Set-Cookie").Single().Split(';')[0];
        var token = FormToken().Match(await page.Content.ReadAsStringAsync()).Groups["token"].Value;
        var madeUpUser = $"{Environment.UserDomainName}\\devkit-e2e-nobody-{Guid.NewGuid():N}"[..40];
        using var post = new HttpRequestMessage(HttpMethod.Post, "/signin/password")
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["__RequestVerificationToken"] = token,
                ["username"] = madeUpUser,
                ["password"] = "not-a-real-password",
                ["returnUrl"] = "/"
            })
        };
        post.Headers.Add("Cookie", antiforgeryCookie);

        using var response = await client.SendAsync(post);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        var html = await response.Content.ReadAsStringAsync();
        Assert.Contains("Windows did not accept this username and password.", html, StringComparison.Ordinal);
        Assert.DoesNotContain("wrong username or password", html, StringComparison.Ordinal);
        Assert.DoesNotContain(response.Headers.TryGetValues("Set-Cookie", out var set) ? set : [], c => c.StartsWith(SessionCookie, StringComparison.Ordinal));
        var audit = AuditText();
        Assert.Contains($"\tfailed\tpassword\t{madeUpUser}@", audit, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("\twrong username or password", audit, StringComparison.Ordinal);
        Assert.DoesNotContain("not-a-real-password", audit, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_signed_out_browser_cannot_open_the_live_connection()
    {
        using var client = _devKit.NewClient();

        using var response = await client.PostAsync("/_blazor/negotiate?negotiateVersion=1", null);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Another_site_cannot_post_the_sign_in_form()
    {
        using var client = _devKit.NewClient();
        using var post = new HttpRequestMessage(HttpMethod.Post, "/signin/password") { Content = new StringContent("") };
        post.Headers.Add("Origin", "http://evil.example");

        using var response = await client.SendAsync(post);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task A_host_name_this_machine_does_not_answer_to_is_refused()
    {
        using var client = _devKit.NewClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/signin");
        request.Headers.Host = "evil.example";

        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Sign_out_needs_the_pages_own_form_token()
    {
        var cookie = (await SignInWithWindowsAsync()).Split(';')[0];
        using var client = _devKit.NewClient();
        using var signOut = new HttpRequestMessage(HttpMethod.Post, "/signout") { Content = new StringContent("") };
        signOut.Headers.Add("Cookie", cookie);

        using var refused = await client.SendAsync(signOut);
        using var still = new HttpRequestMessage(HttpMethod.Get, "/settings");
        still.Headers.Add("Cookie", cookie);
        using var page = await client.SendAsync(still);

        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        Assert.Equal(HttpStatusCode.OK, page.StatusCode);
    }

    [GeneratedRegex("expires=(?<at>[^;]+)", RegexOptions.IgnoreCase)]
    private static partial Regex ExpiresAttribute();

    [GeneratedRegex("name=\"__RequestVerificationToken\" value=\"(?<token>[^\"]+)\"")]
    private static partial Regex FormToken();
}
