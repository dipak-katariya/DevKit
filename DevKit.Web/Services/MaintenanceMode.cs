using System.Globalization;
using System.Net;
using Microsoft.AspNetCore.HostFiltering;
using Microsoft.AspNetCore.Http.Extensions;

namespace DevKit.Web.Services;

/// <summary>
/// The page that answers on DevKit's port while DevKit is updated, and the switch around it (<see cref="DevKitSwitch"/>).
/// "Run Latest DevKit.bat" starts DevKit.Web.exe with <see cref="Switch"/>: every request then gets "DevKit is being
/// updated" with 503, on the same endpoints devkit.json gives DevKit (https included), and the browser loads it again
/// every few seconds until DevKit itself answers. The new DevKit asks it to make way as it is about to listen
/// (<see cref="PortHandover"/>), so the port is closed for a moment only.
/// </summary>
internal static class MaintenanceMode
{
    public const string Switch = "--maintenance";

    /// <summary>Seconds between the browser's tries, sent as Retry-After and Refresh.</summary>
    internal const string RetrySeconds = "10";

    // The page links only to itself and has no forms, so it names no page to anyone.
    private const string ReferrerPolicy = "no-referrer";

    // What the switch says in its window, one line each.
    private const string SwitchCategory = "Switch";

    public static bool IsRequested(string[] args) => args.Contains(Switch, StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Runs a switch to the package --package names (<see cref="DevKitSwitch"/>) and returns its
    /// <see cref="SwitchOutcome"/> as the exit code. The port comes from <see cref="PortHandover.PortVariable"/>, which the
    /// scripts set; the address and certificate come from --urls or devkit.json, as for DevKit itself.
    /// </summary>
    public static async Task<int> RunAsync(string[] args)
    {
        using var loggers = LoggerFactory.Create(ConfigureLogging);
        var log = loggers.CreateLogger(SwitchCategory);
        if (PortHandover.ConfiguredPort() is not { } port)
        {
            log.LogError("{Variable} must name the port to switch; start the switch with Run Latest DevKit.bat", PortHandover.PortVariable);
            return (int)SwitchOutcome.Refused;
        }

        if (DevKitSwitch.ParseRequest(args, log) is not { } request || DevKitSwitch.Create(port, request, log) is not { } devKitSwitch)
        {
            return (int)SwitchOutcome.Refused;
        }

        using var claim = DevKitSwitch.Claim(port, log);
        if (claim is null)
        {
            log.LogError("Another switch on port {Port} is still running; wait for it to finish, then run this again if needed", port);
            return (int)SwitchOutcome.Busy;
        }

        return (int)await devKitSwitch.RunAsync(takePort => BuildPage(request.PageArgs, port, takePort, () => devKitSwitch.Status));
    }

    /// <summary>
    /// The maintenance page, which runs <paramref name="takePort"/> once it is ready, just before it listens, and shows
    /// <paramref name="status"/> as it is at each request. Throws, before anything has changed, when its addresses leave
    /// out <paramref name="port"/>, or when shared DevKit's configuration, or its folder, is one DevKit itself would refuse.
    /// </summary>
    private static WebApplication BuildPage(string[] args, int port, Func<ILogger, CancellationToken, Task> takePort, Func<DevKitSwitch.SwitchStatus> status)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            Args = args,
            // The page's styles are beside the program, whichever folder it was started in.
            ContentRootPath = AppContext.BaseDirectory
        });
        builder.WebHost.UseStaticWebAssets();
        DevKitAccess.HideServerName(builder.WebHost);
        ConfigureLogging(builder.Logging);
        builder.Services.Configure<ConsoleLifetimeOptions>(lifetime => lifetime.SuppressStatusMessages = true);
        DevKitMachineConfig.AddTo(builder.Configuration);
        var options = DevKitHostingOptions.Read(builder.Configuration);
        HostEndpoints.EnsureServes(builder.Configuration, port);
        if (options.IsHosted)
        {
            // The names shared DevKit answers to, as HostedAccess sets them; local mode keeps appsettings.json's.
            builder.Services.Configure<HostFilteringOptions>(hosts => hosts.AllowedHosts = options.HostNames.ToList());
            if (OperatingSystem.IsWindows())
            {
                // Shared DevKit stops at start in a folder others may change, so the switch refuses it before stopping the one that runs.
                DevKitMachineConfig.EnsureClosed();
            }
        }

        PortHandover.AddBeforeListening(builder.Services, takePort);
        var app = builder.Build();
        DevKitAccess.UseSecurityHeaders(app, ReferrerPolicy);
        app.Use((context, next) => MaintenancePage.Frame.Serves(context.Request) ? next(context) : WritePageAsync(context, status()));
        app.UseStaticFiles();
        return app;
    }

    /// <summary>One line per message in the switch's window; the web server's own only when something is wrong.</summary>
    private static void ConfigureLogging(ILoggingBuilder logging)
    {
        logging.ClearProviders();
        logging.AddSimpleConsole(console =>
        {
            console.SingleLine = true;
            console.TimestampFormat = "HH:mm:ss ";
        });
        logging.AddFilter("Microsoft", LogLevel.Warning);
        logging.AddFilter("System", LogLevel.Warning);
    }

    /// <summary>
    /// The page for every request but its own styles: browsers show it, and DevKit's probe and connection fail. The browser
    /// loads it again by itself (Refresh) until a person stops that with the page's link.
    /// </summary>
    private static Task WritePageAsync(HttpContext context, DevKitSwitch.SwitchStatus status)
    {
        var visit = MaintenancePage.Visit.Of(context.Request);
        var response = context.Response;
        response.StatusCode = StatusCodes.Status503ServiceUnavailable;
        response.ContentType = "text/html; charset=utf-8";
        response.Headers.RetryAfter = RetrySeconds;
        if (visit.Checks == MaintenancePage.Checks.Automatic)
        {
            response.Headers["Refresh"] = RetrySeconds;
        }

        response.Headers.CacheControl = "no-store";
        return response.WriteAsync(MaintenancePage.Render(status, DateTimeOffset.UtcNow, visit));
    }
}

/// <summary>
/// What the maintenance page shows: the news at poster size on DevKit's blue, which version is starting and since when,
/// and how long until the browser checks again, which maintenance.css counts down without a script. It is made again for
/// every request, so each check shows where the switch is. A person can stop the browser's own checks, as a screen reader
/// starts again from the top at every one, and start them again.
/// </summary>
internal static class MaintenancePage
{
    /// <summary>How a load of the page came about, which decides whether the page comes in with motion.</summary>
    internal enum Load
    {
        /// <summary>The browser's own check every few seconds, which shows the page as it is.</summary>
        Check,

        /// <summary>A person opened the page or pressed Check now, so it comes in with motion once.</summary>
        Opened
    }

    /// <summary>Whether the browser loads the page again by itself every few seconds.</summary>
    internal enum Checks
    {
        /// <summary>It does, until DevKit answers instead: the page counts down to each check.</summary>
        Automatic,

        /// <summary>A person stopped that, so the page changes only when they press Check now.</summary>
        Stopped
    }

    /// <summary>What a request asks of the page, and the rest of its query, which the page's links keep for DevKit.</summary>
    internal sealed record Visit(Load Load, Checks Checks, QueryString Others)
    {
        /// <summary>
        /// A browser that does not say a person started the load counts as a check; only "checks=off" stops the checks.
        /// </summary>
        public static Visit Of(HttpRequest request)
        {
            ArgumentNullException.ThrowIfNull(request);
            var load = request.Headers[FetchUserHeader] == StartedByPerson ? Load.Opened : Load.Check;
            var checks = string.Equals(request.Query[ChecksKey], ChecksOff, StringComparison.OrdinalIgnoreCase) ? Checks.Stopped : Checks.Automatic;
            var others = new QueryBuilder(request.Query.Where(pair => !pair.Key.Equals(ChecksKey, StringComparison.OrdinalIgnoreCase)));
            return new Visit(load, checks, others.ToQueryString());
        }
    }

    // What changes once a person stops the checks: the lead's second sentence, the link that turns them off or on again
    // with the value it gives ChecksKey (none to drop it), the status's last line, and a class that hides the band.
    private sealed record Checking(string Wait, string Toggle, string? ToggleValue, string Next, string PageClass);

    /// <summary>The page's files, and DevKit's blue for the browser's own bar on a phone, as the page's field.</summary>
    public static readonly CardPage.Frame Frame = new("maintenance.css", "mt-page", ThemeColor: "#4f7cff");

    // No-break spaces keep the date and the time each on one line; a line may break only after the comma.
    private const string MadeFormat = "d MMM yyyy, h:mm tt";
    private const int SecondsPerMinute = 60;

    // Browsers send "?1" here only for a load a person started; the checks the Refresh header makes come without it.
    private const string FetchUserHeader = "Sec-Fetch-User";
    private const string StartedByPerson = "?1";

    // The query value the Stop checking link adds. DevKit reads no query value of this name, so it can stay in the address.
    private const string ChecksKey = "checks";
    private const string ChecksOff = "off";

    private static readonly Dictionary<Checks, Checking> CheckingTexts = new()
    {
        [Checks.Automatic] = new(
            $"Please wait a moment: this page checks again every {MaintenanceMode.RetrySeconds} seconds and opens DevKit by itself as soon as it is ready.",
            "Stop checking", ChecksOff, """<span class="mt-next" aria-hidden="true"></span>""", ""),
        [Checks.Stopped] = new(
            "Please wait a moment, then press Check now: DevKit opens if it is ready.",
            "Start checking", null, "<span>Automatic checks are off.</span>", " mt-stopped")
    };

    /// <param name="status">Where the switch is.</param>
    /// <param name="now">The time of the request, for how long the update has run.</param>
    /// <param name="visit">
    /// What the request asks: only a page a person opened comes in with motion (mt-enter), and stopped checks change the
    /// wording and the link.
    /// </param>
    public static string Render(DevKitSwitch.SwitchStatus status, DateTimeOffset now, Visit visit)
    {
        ArgumentNullException.ThrowIfNull(status);
        ArgumentNullException.ThrowIfNull(visit);
        var version = WebUtility.HtmlEncode(Version(status.PackageName));
        var happening = status.GoingBack
            ? "The new version did not start, so DevKit is going back to the version that ran before."
            : "A new version of DevKit is starting.";
        var starting = status.GoingBack ? $"Starting {version} again." : $"Starting {version}.";
        var checking = CheckingTexts[visit.Checks];
        var enter = visit.Load == Load.Opened ? " mt-enter" : "";
        // Only a query, so the link stays on this address whatever the request's path holds; "?" alone drops ChecksKey.
        var toggleQuery = checking.ToggleValue is null ? visit.Others : visit.Others.Add(ChecksKey, checking.ToggleValue);
        var toggleHref = WebUtility.HtmlEncode(toggleQuery.HasValue ? toggleQuery.Value : "?");
        return CardPage.Document("DevKit is being updated", Frame, $$"""
            <div class="mt{{enter}}{{checking.PageClass}}" style="--retry:{{MaintenanceMode.RetrySeconds}}">
              <header class="mt-bar">
                <span class="mt-brand"><span class="mt-badge" aria-hidden="true">DK</span><span class="mt-name">DevKit</span></span>
                <div class="mt-actions">
                  <a class="mt-toggle" href="{{toggleHref}}">{{checking.Toggle}}</a>
                  <a class="mt-check" href="">Check now</a>
                </div>
              </header>
              <main class="mt-main">
                <h1 class="mt-title"><span>DevKit is</span> <span>being updated</span></h1>
                <p class="mt-lead">{{happening}} {{checking.Wait}}</p>
                <p class="mt-status"><span>{{starting}}</span> <span>The update started {{Elapsed(now - status.Since)}}.</span>
                {{checking.Next}}</p>
              </main>
            </div>
            """);
    }

    /// <summary>The version a package holds, by when it was made, as its name says.</summary>
    private static string Version(string packageName) => DevKitSwitch.MadeAt(packageName) switch
    {
        { } made => "the version made " + made.ToString(MadeFormat, CultureInfo.InvariantCulture),
        null when packageName.Equals(DevKitSwitch.SinglePackageName, StringComparison.OrdinalIgnoreCase) => "the earlier version",
        null => packageName
    };

    private static string Elapsed(TimeSpan elapsed)
    {
        var seconds = Math.Max(0, (int)elapsed.TotalSeconds);
        return seconds switch
        {
            0 => "just now",
            1 => "1 second ago",
            < SecondsPerMinute => seconds.ToString(CultureInfo.InvariantCulture) + " seconds ago",
            < 2 * SecondsPerMinute => "1 minute ago",
            _ => (seconds / SecondsPerMinute).ToString(CultureInfo.InvariantCulture) + " minutes ago"
        };
    }
}
