using System.Net;
using DevKit.Web.Services;
using Xunit;

namespace DevKit.Tests;

/// <summary>
/// The access rules and the PAT's absence from the page, checked against the real app over HTTP: what
/// someone else signed in to the machine, or a web page in the browser, would actually get back.
/// </summary>
public sealed class LocalAccessEndToEndTests : IClassFixture<DevKitProcess>
{
    private readonly DevKitProcess _devKit;

    public LocalAccessEndToEndTests(DevKitProcess devKit) => _devKit = devKit;

    private async Task<string> SignInCookieAsync(HttpClient client)
    {
        using var response = await client.GetAsync(new Uri(_devKit.SignInLink).PathAndQuery);
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        var setCookie = Assert.Single(response.Headers.GetValues("Set-Cookie"));
        return setCookie.Split(';')[0];
    }

    [Fact]
    public async Task Without_the_sign_in_link_every_page_is_locked()
    {
        using var client = _devKit.NewClient();

        using var response = await client.GetAsync("/settings");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.DoesNotContain(DevKitProcess.Pat, await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_signed_in_settings_page_never_contains_the_pat()
    {
        using var client = _devKit.NewClient();
        var cookie = await SignInCookieAsync(client);
        using var request = new HttpRequestMessage(HttpMethod.Get, "/settings");
        request.Headers.Add("Cookie", cookie);

        using var response = await client.SendAsync(request);
        var html = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("Leave blank to keep it", html, StringComparison.Ordinal);
        Assert.Matches("type=\"password\"[^>]*value=\"\"", html);
        Assert.DoesNotContain(DevKitProcess.Pat, html, StringComparison.Ordinal);
        Assert.DoesNotContain("enc:v", html, StringComparison.Ordinal);
        foreach (var file in Directory.GetFiles(Path.GetDirectoryName(_devKit.SettingsPath)!))
        {
            Assert.DoesNotContain(DevKitProcess.Pat, await File.ReadAllTextAsync(file), StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task The_sign_in_cookie_is_http_only_and_strict()
    {
        using var client = _devKit.NewClient();

        using var response = await client.GetAsync(new Uri(_devKit.SignInLink).PathAndQuery);
        var setCookie = Assert.Single(response.Headers.GetValues("Set-Cookie"));

        Assert.StartsWith(LocalAccessGate.CookieName(_devKit.BaseAddress.Port) + "=", setCookie, StringComparison.Ordinal);
        Assert.Contains("httponly", setCookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("samesite=strict", setCookie, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("/", response.Headers.Location?.OriginalString);
    }

    [Fact]
    public async Task A_request_addressed_to_another_host_name_is_refused()
    {
        using var client = _devKit.NewClient();
        var cookie = await SignInCookieAsync(client);
        using var request = new HttpRequestMessage(HttpMethod.Get, "/settings");
        request.Headers.Host = $"devkit.attacker.test:{_devKit.BaseAddress.Port}";
        request.Headers.Add("Cookie", cookie);

        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task The_blazor_hub_refuses_another_origin_and_accepts_its_own()
    {
        using var client = _devKit.NewClient();
        var cookie = await SignInCookieAsync(client);

        var foreign = await NegotiateAsync(client, cookie, "http://evil.example");
        var own = await NegotiateAsync(client, cookie, _devKit.BaseAddress.GetLeftPart(UriPartial.Authority));

        Assert.Equal(HttpStatusCode.Forbidden, foreign);
        Assert.Equal(HttpStatusCode.OK, own);
    }

    [Fact]
    public async Task The_health_probe_answers_without_signing_in()
    {
        using var client = _devKit.NewClient();

        using var response = await client.GetAsync("/healthz");

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
    }

    private static async Task<HttpStatusCode> NegotiateAsync(HttpClient client, string cookie, string origin)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/_blazor/negotiate?negotiateVersion=1");
        request.Headers.Add("Cookie", cookie);
        request.Headers.Add("Origin", origin);
        using var response = await client.SendAsync(request);
        return response.StatusCode;
    }
}
