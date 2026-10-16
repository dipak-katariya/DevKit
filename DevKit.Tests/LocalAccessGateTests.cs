using System.Net;
using DevKit.Web.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DevKit.Tests;

/// <summary>
/// Who may use a running DevKit: only a browser holding this run's key, on this machine, addressing
/// localhost, from DevKit's own origin. Everyone else signed in to the machine, and any web page, is refused.
/// </summary>
public sealed class LocalAccessGateTests
{
    private const int Port = 5850;
    private const string Host = "localhost:5850";
    private const string LockedText = "only opens for the person who started it";

    private readonly LocalAccessGate _gate = new(NullLogger<LocalAccessGate>.Instance);

    private string ValidCookie => $"{LocalAccessGate.CookieName(Port)}={_gate.Key}";

    private static DefaultHttpContext NewRequest(string path = "/", string query = "", string host = Host)
    {
        var context = new DefaultHttpContext();
        context.Connection.RemoteIpAddress = IPAddress.Loopback;
        context.Connection.LocalPort = Port;
        context.Request.Scheme = "http";
        context.Request.Host = new HostString(host);
        context.Request.Path = path;
        context.Request.QueryString = new QueryString(query);
        context.Response.Body = new MemoryStream();
        return context;
    }

    private async Task<(int Status, bool Passed)> SendAsync(DefaultHttpContext context)
    {
        var passed = false;
        await _gate.InvokeAsync(context, _ =>
        {
            passed = true;
            return Task.CompletedTask;
        });
        return (context.Response.StatusCode, passed);
    }

    private static string Body(DefaultHttpContext context)
    {
        context.Response.Body.Position = 0;
        using var reader = new StreamReader(context.Response.Body);
        return reader.ReadToEnd();
    }

    [Fact]
    public async Task A_browser_without_the_cookie_gets_the_locked_page()
    {
        var request = NewRequest("/settings");

        var (status, passed) = await SendAsync(request);

        Assert.Equal(StatusCodes.Status401Unauthorized, status);
        Assert.False(passed);
        Assert.Contains(LockedText, Body(request), StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_health_probe_answers_without_the_cookie()
    {
        var (_, passed) = await SendAsync(NewRequest("/healthz"));

        Assert.True(passed);
    }

    [Fact]
    public async Task The_sign_in_link_sets_a_strict_http_only_cookie_and_drops_the_key_from_the_address()
    {
        var request = NewRequest("/settings", $"?tab=2&k={_gate.Key}");

        var (status, passed) = await SendAsync(request);

        Assert.Equal(StatusCodes.Status302Found, status);
        Assert.False(passed);
        Assert.Equal("/settings?tab=2", request.Response.Headers.Location.ToString());
        var cookie = request.Response.Headers.SetCookie.ToString();
        Assert.StartsWith(ValidCookie + ";", cookie, StringComparison.Ordinal);
        Assert.Contains("httponly", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("samesite=strict", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("path=/", cookie, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task A_wrong_key_signs_nothing_in()
    {
        var request = NewRequest("/", "?k=not-the-key");

        var (status, passed) = await SendAsync(request);

        Assert.Equal(StatusCodes.Status401Unauthorized, status);
        Assert.False(passed);
        Assert.Empty(request.Response.Headers.SetCookie.ToString());
    }

    [Fact]
    public async Task The_cookie_lets_every_request_through()
    {
        var request = NewRequest("/settings");
        request.Request.Headers.Cookie = ValidCookie;

        var (_, passed) = await SendAsync(request);

        Assert.True(passed);
    }

    [Fact]
    public async Task A_cookie_from_another_run_or_for_another_port_is_refused()
    {
        var previousRun = new LocalAccessGate(NullLogger<LocalAccessGate>.Instance);
        var fromPreviousRun = NewRequest();
        fromPreviousRun.Request.Headers.Cookie = $"{LocalAccessGate.CookieName(Port)}={previousRun.Key}";
        var forOtherPort = NewRequest();
        forOtherPort.Request.Headers.Cookie = $"{LocalAccessGate.CookieName(Port + 4)}={_gate.Key}";

        Assert.Equal(StatusCodes.Status401Unauthorized, (await SendAsync(fromPreviousRun)).Status);
        Assert.Equal(StatusCodes.Status401Unauthorized, (await SendAsync(forOtherPort)).Status);
    }

    [Fact]
    public async Task Another_machine_is_refused_even_with_the_cookie()
    {
        var request = NewRequest();
        request.Connection.RemoteIpAddress = IPAddress.Parse("10.20.30.40");
        request.Request.Headers.Cookie = ValidCookie;

        var (status, passed) = await SendAsync(request);

        Assert.Equal(StatusCodes.Status403Forbidden, status);
        Assert.False(passed);
    }

    [Theory]
    [InlineData("evil.example:5850")]
    [InlineData("devkit.attacker.test")]
    [InlineData("10.20.30.40:5850")]
    public async Task A_request_addressed_to_another_host_name_is_refused_even_with_the_cookie(string host)
    {
        var request = NewRequest(host: host);
        request.Request.Headers.Cookie = ValidCookie;

        var (status, passed) = await SendAsync(request);

        Assert.Equal(StatusCodes.Status400BadRequest, status);
        Assert.False(passed);
    }

    [Theory]
    [InlineData("localhost:5850")]
    [InlineData("LOCALHOST:5850")]
    [InlineData("127.0.0.1:5850")]
    [InlineData("[::1]:5850")]
    public async Task Every_name_for_this_machine_is_accepted(string host)
    {
        var request = NewRequest(host: host);
        request.Request.Headers.Cookie = ValidCookie;

        Assert.True((await SendAsync(request)).Passed);
    }

    [Theory]
    [InlineData("http://evil.example")]
    [InlineData("http://localhost:5851")]
    [InlineData("https://localhost:5850")]
    [InlineData("null")]
    public async Task A_request_from_another_origin_is_refused_even_with_the_cookie(string origin)
    {
        var request = NewRequest("/_blazor");
        request.Request.Headers.Cookie = ValidCookie;
        request.Request.Headers.Origin = origin;

        var (status, passed) = await SendAsync(request);

        Assert.Equal(StatusCodes.Status403Forbidden, status);
        Assert.False(passed);
    }

    [Fact]
    public async Task DevKits_own_origin_is_accepted()
    {
        var request = NewRequest("/_blazor");
        request.Request.Headers.Cookie = ValidCookie;
        request.Request.Headers.Origin = "http://localhost:5850";

        Assert.True((await SendAsync(request)).Passed);
    }

    [Fact]
    public void Each_run_has_its_own_key_of_256_random_bits()
    {
        var other = new LocalAccessGate(NullLogger<LocalAccessGate>.Instance);

        Assert.NotEqual(_gate.Key, other.Key);
        Assert.Equal(43, _gate.Key.Length);
        Assert.Equal($"http://localhost:5850/?k={_gate.Key}", _gate.SignInLink("http://localhost:5850/"));
    }
}
