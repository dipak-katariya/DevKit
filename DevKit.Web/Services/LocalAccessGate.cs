using System.Buffers.Text;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Http.Extensions;

namespace DevKit.Web.Services;

/// <summary>
/// Keeps DevKit to the person who started it. A port on localhost can be reached by everyone signed in
/// to the same machine, and by any web page open in the browser; all of them would act with the stored
/// PAT. Each start makes a random key that only the starter sees, in the link DevKit prints and opens.
/// The link swaps the key for a cookie, and every request without that cookie is refused. Requests from
/// another machine, for another host name (DNS rebinding) or from another origin (a page talking to the
/// app's WebSocket) are refused before the key is even looked at.
/// </summary>
public sealed class LocalAccessGate
{
    /// <summary>The query parameter of the sign-in link.</summary>
    public const string KeyParameter = "k";

    private const string CookiePrefix = "devkit-access-";
    private const int KeyBytes = 32;

    private const string LockedPage = """
        <!DOCTYPE html>
        <html lang="en"><head><meta charset="utf-8" /><title>DevKit is locked</title></head>
        <body style="font-family:system-ui,sans-serif;max-width:560px;margin:80px auto;padding:0 20px;line-height:1.5;">
        <h1 style="font-size:1.3rem;">DevKit only opens for the person who started it</h1>
        <p>Open DevKit from the link it printed in its window when it started; it also opens that link in your
        browser. A tab from before DevKit restarted needs the new link.</p>
        </body></html>
        """;

    private static readonly PathString HealthPath = new("/healthz");
    /// <summary>The names this machine answers to locally; hosted mode starts its own list from them.</summary>
    internal static readonly string[] LocalHostNames = { "localhost", "127.0.0.1", "[::1]" };

    private readonly byte[] _key;
    private readonly ILogger<LocalAccessGate> _logger;

    public LocalAccessGate(ILogger<LocalAccessGate> logger)
    {
        _logger = logger;
        Key = Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(KeyBytes));
        _key = Encoding.ASCII.GetBytes(Key);
    }

    /// <summary>This run's key. It changes on every start, so a restart signs every browser out.</summary>
    public string Key { get; }

    /// <summary>The link that signs a browser in to DevKit listening at <paramref name="address"/>.</summary>
    public string SignInLink(string address) => $"{address.TrimEnd('/')}/?{KeyParameter}={Key}";

    /// <summary>The cookie for one port: browsers share cookies across the ports of a host.</summary>
    public static string CookieName(int port) => CookiePrefix + port.ToString(System.Globalization.CultureInfo.InvariantCulture);

    public Task InvokeAsync(HttpContext context, RequestDelegate next)
    {
        if (!IsFromThisMachine(context))
        {
            _logger.LogWarning("Refused a request from another machine");
            return Refuse(context, StatusCodes.Status403Forbidden);
        }

        if (!IsForLocalHost(context))
        {
            _logger.LogWarning("Refused a request addressed to another host name");
            return Refuse(context, StatusCodes.Status400BadRequest);
        }

        if (RefusedOtherOrigin(context, _logger))
        {
            return Task.CompletedTask;
        }

        if (context.Request.Query.TryGetValue(KeyParameter, out var offered))
        {
            return SignIn(context, offered.ToString());
        }

        if (context.Request.Path.Equals(HealthPath) || HasValidCookie(context))
        {
            return next(context);
        }

        return ShowLocked(context);
    }

    /// <summary>
    /// The address the request came from, as the client itself has it. A listener on every address (http://*:port)
    /// takes IPv4 clients over IPv6, so they arrive as ::ffff:a.b.c.d, and ::ffff:127.0.0.1 is not loopback to
    /// <see cref="IPAddress.IsLoopback"/>.
    /// </summary>
    internal static IPAddress? ClientAddress(HttpContext context) =>
        context.Connection.RemoteIpAddress is { IsIPv4MappedToIPv6: true } mapped
            ? mapped.MapToIPv4()
            : context.Connection.RemoteIpAddress;

    /// <summary>Whether the request came from this machine itself, so nothing it carries crossed the network.</summary>
    internal static bool IsFromThisMachine(HttpContext context) =>
        ClientAddress(context) is { } remote && IPAddress.IsLoopback(remote);

    private static bool IsForLocalHost(HttpContext context) =>
        LocalHostNames.Contains(context.Request.Host.Host, StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Refuses a request another origin sent: 403, with a warning logged. Browsers send Origin on every cross-origin
    /// request and on WebSocket upgrades, which CORS never covers. Hosted mode refuses the same way.
    /// </summary>
    internal static bool RefusedOtherOrigin(HttpContext context, ILogger logger)
    {
        var origin = context.Request.Headers.Origin.ToString();
        if (origin.Length == 0
            || string.Equals(origin, $"{context.Request.Scheme}://{context.Request.Host}", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        logger.LogWarning("Refused a request sent from another origin");
        context.Response.StatusCode = StatusCodes.Status403Forbidden;
        return true;
    }

    private bool Matches(string offered) =>
        CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(offered), _key);

    private bool HasValidCookie(HttpContext context) =>
        context.Request.Cookies.TryGetValue(CookieName(context.Connection.LocalPort), out var value)
        && value is not null
        && Matches(value);

    /// <summary>
    /// Sets the cookie for a matching key and sends the browser on without it, so the key leaves the
    /// address bar and the history entry the browser keeps.
    /// </summary>
    private Task SignIn(HttpContext context, string offered)
    {
        if (!Matches(offered))
        {
            _logger.LogWarning("Refused a sign-in link with the wrong key");
            return ShowLocked(context);
        }

        context.Response.Cookies.Append(CookieName(context.Connection.LocalPort), Key, new CookieOptions
        {
            HttpOnly = true,
            SameSite = SameSiteMode.Strict,
            Secure = context.Request.IsHttps,
            Path = "/",
            IsEssential = true
        });

        var rest = new QueryBuilder(context.Request.Query
            .Where(pair => pair.Key != KeyParameter)
            .SelectMany(pair => pair.Value.Select(value => new KeyValuePair<string, string>(pair.Key, value ?? ""))));
        context.Response.Redirect($"{context.Request.PathBase}{context.Request.Path}{rest.ToQueryString()}");
        return Task.CompletedTask;
    }

    private static Task ShowLocked(HttpContext context)
    {
        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
        context.Response.ContentType = "text/html; charset=utf-8";
        return context.Response.WriteAsync(LockedPage);
    }

    private static Task Refuse(HttpContext context, int status)
    {
        context.Response.StatusCode = status;
        return Task.CompletedTask;
    }
}
