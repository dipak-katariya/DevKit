using System.Net;
using System.Net.Sockets;
using DevKit.Web.Services;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Xunit;

namespace DevKit.Tests;

/// <summary>
/// The page shared DevKit shows on its port while a switch swaps it for a new version, checked over HTTP
/// against the real program: every page and DevKit's own probe and connection get "being updated" with 503 and a time
/// to try again, nothing is cached, and only the page's own styles are served.
/// </summary>
public sealed class MaintenanceModeTests : IClassFixture<MaintenancePageProcess>
{
    private readonly MaintenancePageProcess _page;

    public MaintenanceModeTests(MaintenancePageProcess page) => _page = page;

    [Fact]
    public async Task Every_page_says_DevKit_is_being_updated_and_asks_to_try_again()
    {
        using var client = _page.NewClient();

        using var response = await client.GetAsync("/settings");

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal(TimeSpan.FromSeconds(10), response.Headers.RetryAfter?.Delta);
        Assert.Equal(MaintenanceMode.RetrySeconds, response.Headers.GetValues("Refresh").Single());
        Assert.True(response.Headers.CacheControl?.NoStore);
        Assert.Equal("DENY", response.Headers.GetValues("X-Frame-Options").Single());
        Assert.False(response.Headers.Contains("Server"));
        Assert.StartsWith("default-src 'self'", response.Headers.GetValues("Content-Security-Policy").Single(), StringComparison.Ordinal);
        var html = await response.Content.ReadAsStringAsync();
        Assert.Contains("<div class=\"mt\" style=", html, StringComparison.Ordinal);
        Assert.Contains("<h1 class=\"mt-title\"><span>DevKit is</span> <span>being updated</span></h1>", html, StringComparison.Ordinal);
        Assert.Contains("Starting the version made 1&#160;Jan&#160;2030, 10:00&#160;AM.", html, StringComparison.Ordinal);
        Assert.Contains("<a class=\"mt-check\" href=\"\">Check now</a>", html, StringComparison.Ordinal);
        Assert.Contains("/maintenance.css?v=", html, StringComparison.Ordinal);
        Assert.DoesNotContain("<script", html, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task A_page_a_person_opens_comes_in_with_motion()
    {
        using var client = _page.NewClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/");
        request.Headers.TryAddWithoutValidation("Sec-Fetch-User", "?1");

        using var response = await client.SendAsync(request);

        Assert.Contains("<div class=\"mt mt-enter\" style=", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Stopped_checks_drop_the_browsers_reload_and_keep_the_address()
    {
        using var client = _page.NewClient();

        using var response = await client.GetAsync("/merging?repo=a&checks=off");

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.False(response.Headers.Contains("Refresh"));
        Assert.Equal(TimeSpan.FromSeconds(10), response.Headers.RetryAfter?.Delta);
        var html = await response.Content.ReadAsStringAsync();
        Assert.Contains("<span>Automatic checks are off.</span>", html, StringComparison.Ordinal);
        Assert.Contains("<a class=\"mt-toggle\" href=\"?repo=a\">Start checking</a>", html, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("GET", "/healthz")]
    [InlineData("POST", "/_blazor/negotiate?negotiateVersion=1")]
    [InlineData("GET", "/blazor-reconnect.js")]
    [InlineData("GET", "/signin.css")]
    [InlineData("HEAD", "/")]
    public async Task DevKits_probe_connection_and_other_files_get_503(string method, string path)
    {
        using var client = _page.NewClient();
        using var request = new HttpRequestMessage(new HttpMethod(method), path);

        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
    }

    [Theory]
    [InlineData("/maintenance.css")]
    [InlineData("/app.css")]
    public async Task The_pages_own_styles_are_served(string path)
    {
        using var client = _page.NewClient();

        using var response = await client.GetAsync(path);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/css", response.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task A_DevKit_of_another_run_neither_takes_the_port_nor_waits_for_it()
    {
        var log = new MessageRecordingLogger<MaintenanceModeTests>();

        await PortHandover.TakeOverAsync(_page.BaseAddress.Port, "earlierrun", TimeSpan.FromSeconds(30), log, CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(5));

        using var client = _page.NewClient();
        using var response = await client.GetAsync("/");
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Empty(log.Entries);
    }

    [Fact]
    public async Task Another_host_name_is_refused_as_by_shared_DevKit()
    {
        using var client = _page.NewClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/");
        request.Headers.Host = "attacker.example";

        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }
}

/// <summary>
/// What the maintenance page tells whoever opens DevKit, made for each request from where the switch is: the version
/// that is starting, by when its package was made, how long the update has run, and the seconds to the next check.
/// </summary>
public sealed class MaintenancePageTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 30, 10, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Names_the_version_that_is_starting_by_when_it_was_made()
    {
        var html = Render("DevKit_30_10_2026_03_29_PM");

        Assert.Contains("<span>Starting the version made 30&#160;Oct&#160;2026, 3:29&#160;PM.</span>", html, StringComparison.Ordinal);
    }

    [Fact]
    public void Going_back_says_the_new_version_did_not_start()
    {
        var html = Render("DevKit_29_10_2026_05_56_PM", goingBack: true);

        Assert.Contains("<p class=\"mt-lead\">The new version did not start, so DevKit is going back to the version that ran before. Please wait", html, StringComparison.Ordinal);
        Assert.Contains("<span>Starting the version made 29&#160;Oct&#160;2026, 5:56&#160;PM again.</span>", html, StringComparison.Ordinal);
        Assert.DoesNotContain("A new version of DevKit is starting.", html, StringComparison.Ordinal);
    }

    [Fact]
    public void The_single_package_of_earlier_versions_is_the_earlier_version()
    {
        Assert.Contains("Starting the earlier version.", Render(DevKitSwitch.SinglePackageName), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(-5, "just now")]
    [InlineData(0, "just now")]
    [InlineData(1, "1 second ago")]
    [InlineData(12, "12 seconds ago")]
    [InlineData(60, "1 minute ago")]
    [InlineData(125, "2 minutes ago")]
    public void Says_how_long_the_update_has_run(int seconds, string expected)
    {
        var html = Render("DevKit_30_10_2026_03_29_PM", since: Now.AddSeconds(-seconds));

        Assert.Contains($"The update started {expected}.", html, StringComparison.Ordinal);
    }

    [Fact]
    public void Counts_down_the_seconds_the_browser_waits_and_has_no_script()
    {
        var html = Render("DevKit_30_10_2026_03_29_PM");

        Assert.Contains($"style=\"--retry:{MaintenanceMode.RetrySeconds}\"", html, StringComparison.Ordinal);
        Assert.DoesNotContain("<script", html, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Only_a_page_a_person_opened_comes_in_with_motion()
    {
        Assert.Contains("<div class=\"mt mt-enter\"", Render("DevKit_30_10_2026_03_29_PM", Visit(MaintenancePage.Load.Opened)), StringComparison.Ordinal);
        Assert.Contains("<div class=\"mt\"", Render("DevKit_30_10_2026_03_29_PM"), StringComparison.Ordinal);
    }

    [Fact]
    public void A_person_can_stop_the_checks_and_keep_the_rest_of_the_address()
    {
        var html = Render("DevKit_30_10_2026_03_29_PM", Visit(others: new QueryString("?repo=a")));

        Assert.Contains("<a class=\"mt-toggle\" href=\"?repo=a&amp;checks=off\">Stop checking</a>", html, StringComparison.Ordinal);
        Assert.Contains("checks again every 10 seconds", html, StringComparison.Ordinal);
        Assert.Contains("<span class=\"mt-next\" aria-hidden=\"true\"></span>", html, StringComparison.Ordinal);
    }

    [Fact]
    public void Stopped_checks_say_so_and_offer_to_start_them_again()
    {
        var html = Render("DevKit_30_10_2026_03_29_PM", Visit(checks: MaintenancePage.Checks.Stopped));

        Assert.Contains("<div class=\"mt mt-stopped\"", html, StringComparison.Ordinal);
        Assert.Contains("<a class=\"mt-toggle\" href=\"?\">Start checking</a>", html, StringComparison.Ordinal);
        Assert.Contains("Please wait a moment, then press Check now: DevKit opens if it is ready.", html, StringComparison.Ordinal);
        Assert.Contains("<span>Automatic checks are off.</span>", html, StringComparison.Ordinal);
        Assert.DoesNotContain("mt-next", html, StringComparison.Ordinal);
        Assert.DoesNotContain("checks again every", html, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("?1", true)]
    [InlineData("?0", false)]
    [InlineData(null, false)]
    public void A_load_is_a_persons_only_when_the_browser_says_so(string? fetchUser, bool opened)
    {
        var context = new DefaultHttpContext();
        if (fetchUser is not null)
        {
            context.Request.Headers["Sec-Fetch-User"] = fetchUser;
        }

        Assert.Equal(opened, MaintenancePage.Visit.Of(context.Request).Load == MaintenancePage.Load.Opened);
    }

    [Theory]
    [InlineData("?checks=off", true, "")]
    [InlineData("?repo=a&checks=OFF", true, "?repo=a")]
    [InlineData("?checks=on&repo=a", false, "?repo=a")]
    [InlineData("?repo=a&tab=2", false, "?repo=a&tab=2")]
    [InlineData("", false, "")]
    public void Only_checks_off_stops_the_checks_and_the_rest_of_the_query_is_kept(string query, bool stopped, string others)
    {
        var context = new DefaultHttpContext();
        context.Request.QueryString = new QueryString(query);

        var visit = MaintenancePage.Visit.Of(context.Request);

        Assert.Equal(stopped, visit.Checks == MaintenancePage.Checks.Stopped);
        Assert.Equal(others, visit.Others.Value ?? "");
    }

    [Fact]
    public void A_query_with_markup_stays_inside_the_links_address()
    {
        var context = new DefaultHttpContext();
        context.Request.QueryString = new QueryString("?q=%22%3E%3Cscript%3Ealert(1)%3C%2Fscript%3E");

        var html = Render("DevKit_30_10_2026_03_29_PM", MaintenancePage.Visit.Of(context.Request));

        Assert.DoesNotContain("<script", html, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("href=\"?q=%22%3E%3Cscript%3E", html, StringComparison.Ordinal);
        Assert.Contains("&amp;checks=off\">Stop checking</a>", html, StringComparison.Ordinal);
    }

    private static MaintenancePage.Visit Visit(
        MaintenancePage.Load load = MaintenancePage.Load.Check,
        MaintenancePage.Checks checks = MaintenancePage.Checks.Automatic,
        QueryString others = default) =>
        new(load, checks, others);

    private static string Render(string packageName, MaintenancePage.Visit? visit = null, bool goingBack = false, DateTimeOffset? since = null) =>
        MaintenancePage.Render(new DevKitSwitch.SwitchStatus(packageName, since ?? Now.AddSeconds(-3), goingBack), Now, visit ?? Visit());
}

/// <summary>
/// How the port passes from its holder to a newcomer: asked over the named event of the port and the holder's run, the
/// holder stops, and the newcomer waits while the port is still in use, but never past its limit. The maintenance page
/// makes way for the DevKit of its switch's run, and a DevKit for the next switch, finishing what it serves. Without a
/// holder of its run a DevKit neither asks nor waits.
/// </summary>
public sealed class PortHandoverTests
{
    // Port numbers only name pipes in these tests; nothing listens on them.
    internal const int UnusedPort = 59991;

    private const string Run = "run1";

    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(30);

    private readonly MessageRecordingLogger<PortHandoverTests> _log = new();

    [Fact]
    public async Task The_maintenance_page_makes_way_for_the_DevKit_of_its_run()
    {
        var page = new MaintenancePageProcess();
        await page.InitializeAsync();
        try
        {
            var port = page.BaseAddress.Port;

            var run = page.Run;

            await PortHandover.TakeOverAsync(port, run, Wait, _log, CancellationToken.None);

            Assert.False(PortHandover.IsListening(port));
            Assert.Contains(_log.Entries, entry => entry.Level == LogLevel.Information && entry.Message.Contains("make way", StringComparison.Ordinal));
            // The DevKit listening now: the switch is done once its pipe is there and something answers on the port.
            using (new HandoverRequests(port, run))
            {
                using var devKit = new TcpListener(IPAddress.Loopback, port);
                devKit.Start();
                Assert.Equal((int)SwitchOutcome.Switched, await page.ExitCodeAsync(Wait));
            }
        }
        finally
        {
            await page.DisposeAsync();
        }
    }

    [Fact]
    public async Task DevKit_asks_for_its_port_only_as_its_server_starts_then_answers_there()
    {
        using var holder = new TcpListener(IPAddress.Loopback, 0);
        holder.Start();
        var port = ((IPEndPoint)holder.LocalEndpoint).Port;
        using var requests = new HandoverRequests(port, Run);
        var builder = WebApplication.CreateBuilder(["--urls", $"http://127.0.0.1:{port}"]);
        PortHandover.AddTakeOver(builder.Services, port, Run);
        await using var app = builder.Build();
        app.MapGet("/", () => "taken over");
        Assert.False(requests.Asked.IsCompleted);
        var asked = requests.Asked.ContinueWith(_ => holder.Stop(), TaskScheduler.Default);

        await app.StartAsync();

        await asked.WaitAsync(Wait);
        using var client = new HttpClient();
        Assert.Equal("taken over", await client.GetStringAsync($"http://127.0.0.1:{port}/"));
        await app.StopAsync();
    }

    [Fact]
    public async Task A_DevKit_finishes_and_makes_way_when_the_next_switch_asks_for_its_run()
    {
        int port;
        using (var probe = new TcpListener(IPAddress.Loopback, 0))
        {
            probe.Start();
            port = ((IPEndPoint)probe.LocalEndpoint).Port;
        }

        var builder = WebApplication.CreateBuilder(["--urls", $"http://127.0.0.1:{port}"]);
        await using var app = builder.Build();
        PortHandover.MakeWayWhenAsked(app, port, Run);
        Assert.False(PortHandover.AskToMakeWay(port, Run, _log));
        await app.StartAsync();
        Assert.False(PortHandover.AskToMakeWay(port, "otherrun", _log));
        Assert.True(PortHandover.HolderListens(port, Run));

        Assert.True(PortHandover.AskToMakeWay(port, Run, _log));

        await app.WaitForShutdownAsync().WaitAsync(Wait);
        Assert.False(PortHandover.IsListening(port));
        Assert.False(PortHandover.HolderListens(port, Run));
    }

    [Fact]
    public void A_pipe_of_the_port_and_run_that_exists_already_is_not_taken()
    {
        using var first = new HandoverRequests(PortHandoverTests.UnusedPort, Run);

        var second = Record.Exception(() => new HandoverRequests(PortHandoverTests.UnusedPort, Run));

        Assert.True(second is IOException or UnauthorizedAccessException, second?.ToString());
    }

    [Fact]
    public async Task A_connection_that_asks_nothing_leaves_the_holder_where_it_is()
    {
        using var requests = new HandoverRequests(UnusedPort, Run);
        using (var idle = new System.IO.Pipes.NamedPipeClientStream(".", PortHandover.PipeName(UnusedPort, Run), System.IO.Pipes.PipeDirection.Out))
        {
            await idle.ConnectAsync(5000);
        }

        Assert.True(await PortHandover.WaitUntilAsync(() => PortHandover.HolderListens(UnusedPort, Run), Wait, CancellationToken.None));
        Assert.False(requests.Asked.IsCompleted);
        Assert.True(PortHandover.AskToMakeWay(UnusedPort, Run, _log));
        await requests.Asked.WaitAsync(Wait);
    }

    [Fact]
    public void Only_a_server_registered_by_its_type_can_take_the_port_over()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IServer>(_ => throw new InvalidOperationException("never made"));

        Assert.Throws<InvalidOperationException>(() => PortHandover.AddTakeOver(services, 5999, Run));
        Assert.Throws<InvalidOperationException>(() => PortHandover.AddBeforeListening(services, (_, _) => Task.CompletedTask));
    }

    [Fact]
    public async Task Taking_over_waits_until_the_holder_has_let_the_port_go()
    {
        using var holder = new TcpListener(IPAddress.Loopback, 0);
        holder.Start();
        var port = ((IPEndPoint)holder.LocalEndpoint).Port;
        using var requests = new HandoverRequests(port, Run);
        var asked = requests.Asked.ContinueWith(_ => holder.Stop(), TaskScheduler.Default);

        await PortHandover.TakeOverAsync(port, Run, Wait, _log, CancellationToken.None);

        await asked.WaitAsync(Wait);
        Assert.False(PortHandover.IsListening(port));
        Assert.DoesNotContain(_log.Entries, entry => entry.Level >= LogLevel.Warning);
    }

    [Fact]
    public async Task Without_a_maintenance_page_of_its_run_a_DevKit_does_not_wait_for_a_port_in_use()
    {
        using var holder = new TcpListener(IPAddress.Loopback, 0);
        holder.Start();
        var port = ((IPEndPoint)holder.LocalEndpoint).Port;
        using var otherRun = new HandoverRequests(port, "otherrun");

        await PortHandover.TakeOverAsync(port, Run, Wait, _log, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));

        Assert.False(otherRun.Asked.IsCompleted);
        Assert.True(PortHandover.IsListening(port));
        Assert.Empty(_log.Entries);
    }

    [Fact]
    public async Task Waiting_for_a_free_port_returns_at_once()
    {
        int port;
        using (var probe = new TcpListener(IPAddress.Loopback, 0))
        {
            probe.Start();
            port = ((IPEndPoint)probe.LocalEndpoint).Port;
        }

        await PortHandover.WaitForFreePortAsync(port, Wait, _log, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Empty(_log.Entries);
    }

    [Fact]
    public async Task A_port_still_in_use_after_the_wait_is_logged_as_an_error()
    {
        using var holder = new TcpListener(IPAddress.Loopback, 0);
        holder.Start();
        var port = ((IPEndPoint)holder.LocalEndpoint).Port;

        await PortHandover.WaitForFreePortAsync(port, TimeSpan.FromMilliseconds(300), _log, CancellationToken.None);

        var error = Assert.Single(_log.Entries, entry => entry.Level == LogLevel.Error);
        Assert.Contains(port.ToString(System.Globalization.CultureInfo.InvariantCulture), error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("4821733456", "4821733456")]
    [InlineData("Run42", "Run42")]
    [InlineData("", null)]
    [InlineData(null, null)]
    [InlineData("a.b", null)]
    [InlineData("Global\\x", null)]
    [InlineData("12345678901234567890123456789012345678901", null)]
    public void Only_letters_and_digits_make_a_run_id(string? value, string? expected) =>
        Assert.Equal(expected, PortHandover.RunFrom(value));
}
