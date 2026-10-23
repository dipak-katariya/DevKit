using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Security.Claims;
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
/// ends the other tabs of that browser, whose pages are already open, and any copy of the cookie. It lives in memory
/// only: after a restart a copied cookie works again until it expires, which is why sessions do not outlive
/// <see cref="DevKitHostingOptions.SessionDays"/>, and only from the address it signed in from
/// (<see cref="SessionClaims.CameFrom"/>).
/// </summary>
internal sealed class SessionRegistry
{
    private readonly ConcurrentDictionary<string, DateTimeOffset> _signedOut = new(StringComparer.Ordinal);
    private readonly TimeProvider _clock;

    public SessionRegistry(TimeProvider clock)
    {
        _clock = clock;
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

        if (principal.FindFirstValue(SessionClaims.Id) is { Length: > 0 } id)
        {
            _signedOut[id] = SessionClaims.ExpiresAt(principal) ?? now;
        }
    }

    /// <summary>Whether <paramref name="principal"/> is a session that has neither expired nor been signed out.</summary>
    public bool IsActive(ClaimsPrincipal principal)
    {
        ArgumentNullException.ThrowIfNull(principal);
        return principal.Identity?.IsAuthenticated == true
               && principal.FindFirstValue(SessionClaims.Id) is { Length: > 0 } id
               && SessionClaims.ExpiresAt(principal) is { } expires
               && expires > _clock.GetUtcNow()
               && !_signedOut.ContainsKey(id);
    }
}

/// <summary>
/// The person behind one open page, and whether their settings are ready for the pages to use. The layout shows no
/// page until <see cref="StartAsync"/> has made them ready.
/// </summary>
public abstract class UserSession
{
    /// <summary>Whether people sign in (shared mode) rather than the starter using DevKit directly (local mode).</summary>
    public abstract bool IsShared { get; }

    /// <summary>Whether the settings are loaded for this page.</summary>
    public bool IsReady { get; protected set; }

    /// <summary>Who is signed in; null in local mode, and in shared mode before <see cref="StartAsync"/>.</summary>
    public SignedInUser? User { get; protected set; }

    /// <summary>Makes the settings ready. False when nobody is signed in any more, and the page must sign in again.</summary>
    public abstract Task<bool> StartAsync();
}

/// <summary>Local mode: the settings file was read at startup, and the person is whoever started DevKit.</summary>
internal sealed class LocalUserSession : UserSession
{
    public LocalUserSession()
    {
        IsReady = true;
    }

    public override bool IsShared => false;

    public override Task<bool> StartAsync() => Task.FromResult(true);
}

/// <summary>
/// Shared mode: reads who is signed in to this page, then loads their own settings from their own browser. When it
/// keeps none yet, a copy an earlier DevKit kept on this server or in this browser moves into it, and is deleted once it
/// is there.
/// </summary>
internal sealed class SharedUserSession : UserSession
{
    private readonly AuthenticationStateProvider _authentication;
    private readonly ClientSettingsStore _store;
    private readonly EarlierSettingsCopies _earlier;
    private readonly SettingsService _settings;
    private readonly OwnerAccount _owner;

    public SharedUserSession(
        AuthenticationStateProvider authentication,
        ClientSettingsStore store,
        EarlierSettingsCopies earlier,
        SettingsService settings,
        OwnerAccount owner)
    {
        _authentication = authentication;
        _store = store;
        _earlier = earlier;
        _settings = settings;
        _owner = owner;
    }

    public override bool IsShared => true;

    public override async Task<bool> StartAsync()
    {
        if (IsReady)
        {
            return true;
        }

        User = SignedInUser.From((await _authentication.GetAuthenticationStateAsync()).User, _owner.Id);
        if (User is null)
        {
            return false;
        }

        await _store.OpenAsync(User);
        if (_store.HasSettings)
        {
            _settings.LoadFromStore(User.IsOwner);
            // A server copy left by a move whose delete failed. One is kept while the browser's copy cannot be read here.
            if (_settings.LoadProblem is null)
            {
                _earlier.DeleteServerCopy(User);
            }
        }
        else
        {
            await MoveEarlierCopyAsync(User);
        }

        IsReady = true;
        return true;
    }

    /// <summary>Puts the newest earlier copy into the browser, and deletes the copies once the browser has kept it.</summary>
    private async Task MoveEarlierCopyAsync(SignedInUser user)
    {
        var earlier = await _earlier.ReadAsync(user);
        _settings.LoadFromStore(user.IsOwner, earlier);
        // A save refused before it reached the browser leaves no write to wait for, only the error.
        if (earlier is not null && _settings.LastSaveError is null && await _store.LastWriteAsync())
        {
            await _earlier.DeleteAsync(user);
        }
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

/// <summary>
/// Stops password guessing before Windows sees it: after <see cref="MaxFailures"/> wrong passwords for one account
/// within <see cref="Window"/>, that account is refused until the window has passed. The limit sits below common
/// domain lockout thresholds, so a guesser cannot lock the real account out of Windows through DevKit. Accounts are
/// keyed by <see cref="IWindowsCredentialChecker.AccountKey"/>, and those whose window has passed are forgotten, so
/// names typed once never pile up.
/// </summary>
internal sealed class SignInThrottle
{
    public const int MaxFailures = 3;
    public static readonly TimeSpan Window = TimeSpan.FromMinutes(15);

    private readonly ConcurrentDictionary<string, Failures> _failures = new(StringComparer.OrdinalIgnoreCase);
    private readonly TimeProvider _clock;

    public SignInThrottle(TimeProvider clock)
    {
        _clock = clock;
    }

    /// <summary>The accounts still remembered, for the tests that check old ones are forgotten.</summary>
    internal int Remembered => _failures.Count;

    /// <summary>Whether <paramref name="account"/> must wait, and for how long.</summary>
    public bool IsBlocked(string account, out TimeSpan wait)
    {
        wait = TimeSpan.Zero;
        if (!_failures.TryGetValue(account, out var failures))
        {
            return false;
        }

        var now = _clock.GetUtcNow();
        var (count, first) = failures.Read();
        if (now - first >= Window)
        {
            _failures.TryRemove(account, out _);
            return false;
        }

        wait = first + Window - now;
        return count >= MaxFailures;
    }

    public void RecordFailure(string account)
    {
        var now = _clock.GetUtcNow();
        foreach (var passed in _failures.Where(pair => now - pair.Value.Read().First >= Window).Select(pair => pair.Key).ToList())
        {
            _failures.TryRemove(passed, out _);
        }

        _failures.AddOrUpdate(account, _ => new Failures(now), (_, failures) => failures.Add(now, Window));
    }

    public void RecordSuccess(string account) => _failures.TryRemove(account, out _);

    private sealed class Failures
    {
        private readonly object _sync = new();
        private int _count = 1;
        private DateTimeOffset _first;

        public Failures(DateTimeOffset first)
        {
            _first = first;
        }

        public (int Count, DateTimeOffset First) Read()
        {
            lock (_sync)
            {
                return (_count, _first);
            }
        }

        /// <summary>Counts one more failure, starting a new window when the last one has passed.</summary>
        public Failures Add(DateTimeOffset now, TimeSpan window)
        {
            lock (_sync)
            {
                if (now - _first >= window)
                {
                    _first = now;
                    _count = 0;
                }

                _count++;
                return this;
            }
        }
    }
}
