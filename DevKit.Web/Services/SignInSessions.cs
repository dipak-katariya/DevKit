using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Security.Claims;
using System.Text;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components.Server;

namespace DevKit.Web.Services;

/// <summary>The claims a shared-mode session cookie carries besides the account's identifier and name.</summary>
internal static class SessionClaims
{
    /// <summary>A random id per sign-in, so one sign-out can end exactly that session.</summary>
    public const string Id = "devkit:session";

    /// <summary>When the session ends, in Unix seconds; it never slides.</summary>
    public const string Expires = "devkit:expires";

    /// <summary>"windows" for single sign-on, "password" for a typed username and password.</summary>
    public const string Method = "devkit:method";

    /// <summary>The address the browser signed in from; the cookie works from that address only.</summary>
    public const string Address = "devkit:address";

    // Every loopback address is the same place: a browser on the server itself uses 127.0.0.1 or ::1 as it likes.
    private const string ThisMachine = "loopback";

    public static ClaimsPrincipal Create(SignedInUser user, string method, DateTimeOffset expires, IPAddress? address)
    {
        ArgumentNullException.ThrowIfNull(user);
        var identity = new ClaimsIdentity(new[]
        {
            new Claim(ClaimTypes.NameIdentifier, user.Id),
            new Claim(ClaimTypes.Name, user.Name),
            new Claim(Id, Guid.NewGuid().ToString("N")),
            new Claim(Expires, expires.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture)),
            new Claim(Method, method),
            new Claim(Address, AddressKey(address))
        }, CookieAuthenticationDefaults.AuthenticationScheme);
        return new ClaimsPrincipal(identity);
    }

    public static DateTimeOffset? ExpiresAt(ClaimsPrincipal principal) =>
        long.TryParse(principal.FindFirstValue(Expires), NumberStyles.None, CultureInfo.InvariantCulture, out var seconds)
            ? DateTimeOffset.FromUnixTimeSeconds(seconds)
            : null;

    /// <summary>
    /// Whether the session is used from the address it signed in from (<see cref="LocalAccessGate.ClientAddress"/>).
    /// A copy of the cookie, read off the network or out of a browser profile, is then useless on any other machine.
    /// </summary>
    public static bool CameFrom(ClaimsPrincipal principal, IPAddress? address) =>
        principal.FindFirstValue(Address) is { } started
        && string.Equals(started, AddressKey(address), StringComparison.Ordinal);

    private static string AddressKey(IPAddress? address)
    {
        if (address is null)
        {
            return "";
        }

        return IPAddress.IsLoopback(address) ? ThisMachine : address.ToString();
    }
}

/// <summary>
/// Sessions signed out before they expired. A sign-out deletes the cookie in the browser that asked; this list also
/// ends the other tabs of that browser, whose pages are already open, and any copy of the cookie. Given a file, the list
/// is also kept there, one line per sign-out until that session would have ended, so a restart or a package switch never
/// brings a signed-out session back, and the DevKit a switch is replacing learns of sign-outs the new one took.
/// </summary>
internal sealed class SessionRegistry
{
    private const int SessionIdChars = 32;
    private const long MaxFileBytes = 1 << 20;
    private const char Separator = '\t';
    private static readonly TimeSpan LookAgainAfter = TimeSpan.FromSeconds(5);

    private readonly ConcurrentDictionary<string, DateTimeOffset> _signedOut = new(StringComparer.Ordinal);
    private readonly TimeProvider _clock;
    private readonly string? _file;
    private readonly ILogger? _logger;
    private readonly object _fileSync = new();
    private DateTime _fileWritten;
    private DateTimeOffset _lookedAt = DateTimeOffset.MinValue;

    /// <summary>Sign-outs kept in memory alone, for tests.</summary>
    public SessionRegistry(TimeProvider clock)
    {
        _clock = clock;
    }

    /// <summary>Sign-outs also kept in <paramref name="file"/>, which every DevKit on this server reads.</summary>
    public SessionRegistry(TimeProvider clock, string file, ILogger<SessionRegistry> logger)
        : this(clock)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(file);
        _file = file;
        _logger = logger;
        ReadFile(dropEnded: true);
    }

    /// <summary>
    /// A new session for <paramref name="user"/> that ends <paramref name="length"/> from now, whatever happens, and
    /// works only from <paramref name="address"/>.
    /// </summary>
    public (ClaimsPrincipal Principal, DateTimeOffset Expires) Start(SignedInUser user, string method, TimeSpan length, IPAddress? address)
    {
        var expires = _clock.GetUtcNow() + length;
        return (SessionClaims.Create(user, method, expires, address), expires);
    }

    /// <summary>Ends the session <paramref name="principal"/> carries, everywhere it is open.</summary>
    public void SignOut(ClaimsPrincipal principal)
    {
        ArgumentNullException.ThrowIfNull(principal);
        var now = _clock.GetUtcNow();
        foreach (var ended in _signedOut.Where(pair => pair.Value <= now).Select(pair => pair.Key).ToList())
        {
            _signedOut.TryRemove(ended, out _);
        }

        if (principal.FindFirstValue(SessionClaims.Id) is { Length: SessionIdChars } id)
        {
            var until = SessionClaims.ExpiresAt(principal) ?? now;
            _signedOut[id] = until;
            KeepInFile(id, until);
        }
    }

    /// <summary>Whether <paramref name="principal"/> is a session that has neither expired nor been signed out.</summary>
    public bool IsActive(ClaimsPrincipal principal)
    {
        ArgumentNullException.ThrowIfNull(principal);
        if (principal.Identity?.IsAuthenticated != true
            || principal.FindFirstValue(SessionClaims.Id) is not { Length: > 0 } id
            || SessionClaims.ExpiresAt(principal) is not { } expires
            || expires <= _clock.GetUtcNow())
        {
            return false;
        }

        LookAtFile();
        return !_signedOut.ContainsKey(id);
    }

    private void KeepInFile(string id, DateTimeOffset until)
    {
        if (_file is null)
        {
            return;
        }

        try
        {
            lock (_fileSync)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_file)!);
                using var stream = new FileStream(_file, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
                stream.Write(Encoding.ASCII.GetBytes($"{id}{Separator}{until.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture)}\n"));
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger?.LogError(ex, "A sign-out could not be kept in {File}; it holds until DevKit restarts", _file);
        }
    }

    /// <summary>Reads sign-outs another DevKit kept, looking at most every few seconds, and only when the file changed.</summary>
    private void LookAtFile()
    {
        var now = _clock.GetUtcNow();
        if (_file is null || now - _lookedAt < LookAgainAfter)
        {
            return;
        }

        _lookedAt = now;
        ReadFile();
    }

    /// <summary>
    /// Takes in the sign-outs the file keeps, when it changed since it was last read. Lines of sessions that have ended
    /// anyway are dropped from it when DevKit starts, so it never grows past the sign-outs of the last few days.
    /// </summary>
    private void ReadFile(bool dropEnded = false)
    {
        try
        {
            var file = new FileInfo(_file!);
            if (!file.Exists || file.LastWriteTimeUtc == _fileWritten)
            {
                return;
            }

            _fileWritten = file.LastWriteTimeUtc;
            if (file.Length > MaxFileBytes)
            {
                _logger?.LogError("{File} holds {Bytes} bytes, more than sign-outs ever take, so it is not read", _file, file.Length);
                return;
            }

            var lines = File.ReadAllLines(file.FullName, Encoding.ASCII);
            var now = _clock.GetUtcNow();
            var current = new List<string>();
            foreach (var line in lines)
            {
                if (TryParse(line, out var id, out var until) && until > now)
                {
                    _signedOut[id] = until;
                    current.Add(line);
                }
            }

            if (dropEnded && current.Count < lines.Length)
            {
                Rewrite(current);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger?.LogError(ex, "The sign-outs kept in {File} could not be read; DevKit tries again in a few seconds", _file);
        }
    }

    private void Rewrite(List<string> current)
    {
        lock (_fileSync)
        {
            var partial = _file + ".partial";
            File.WriteAllLines(partial, current, Encoding.ASCII);
            File.Move(partial, _file!, overwrite: true);
            _fileWritten = File.GetLastWriteTimeUtc(_file!);
        }
    }

    private static bool TryParse(string line, out string id, out DateTimeOffset until)
    {
        var parts = line.Split(Separator);
        id = parts[0];
        until = default;
        if (parts.Length != 2 || id.Length != SessionIdChars || !id.All(char.IsAsciiHexDigitLower)
            || !long.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var seconds))
        {
            return false;
        }

        until = DateTimeOffset.FromUnixTimeSeconds(Math.Min(seconds, DateTimeOffset.MaxValue.ToUnixTimeSeconds()));
        return true;
    }
}

/// <summary>
/// Re-checks an open page's session every minute, so a page left open past the session's end, or after a sign-out
/// in another tab, stops working as that person and goes back to the sign-in page.
/// </summary>
internal sealed class SessionAuthenticationStateProvider : RevalidatingServerAuthenticationStateProvider
{
    private readonly SessionRegistry _sessions;

    public SessionAuthenticationStateProvider(ILoggerFactory loggerFactory, SessionRegistry sessions)
        : base(loggerFactory)
    {
        _sessions = sessions;
    }

    protected override TimeSpan RevalidationInterval => TimeSpan.FromMinutes(1);

    protected override Task<bool> ValidateAuthenticationStateAsync(AuthenticationState authenticationState, CancellationToken cancellationToken) =>
        Task.FromResult(_sessions.IsActive(authenticationState.User));
}
