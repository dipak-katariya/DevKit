using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Runtime.Versioning;
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
/// Shared mode: reads who is signed in to this page, then loads their own settings from the server's settings folder
/// (<see cref="PersonSettingsStore"/>). On their first visit since the folder came in, the newest copy an earlier DevKit
/// kept (in their browser's local storage, or in an older server folder) moves into it, and is deleted once it is there.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class SharedUserSession : UserSession
{
    private readonly AuthenticationStateProvider _authentication;
    private readonly PersonSettingsStore _store;
    private readonly EarlierSettingsCopies _earlier;
    private readonly SettingsService _settings;
    private readonly OwnerAccount _owner;

    public SharedUserSession(
        AuthenticationStateProvider authentication,
        PersonSettingsStore store,
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

        _store.Open(User);
        if (_store.HasSettings)
        {
            _settings.LoadFromStore(User.IsOwner);
            // An older server folder's copy left by a move whose delete failed; one is kept while these do not open.
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

    /// <summary>
    /// Keeps the newest earlier copy in the settings folder, and deletes the copies once it is there. The browser drops
    /// the copy its cookies may keep, which is never moved.
    /// </summary>
    private async Task MoveEarlierCopyAsync(SignedInUser user)
    {
        var earlier = await _earlier.ReadAsync(user);
        _settings.LoadFromStore(user.IsOwner, earlier);
        if (earlier is not null && _settings.LastSaveError is null)
        {
            await _earlier.DeleteAsync(user);
        }

        await _earlier.ClearCookieCopyAsync();
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
/// domain lockout thresholds, so a guesser cannot lock the real account out of Windows through DevKit. Attempts Windows
/// is still checking count too (<see cref="TryBegin"/>), so guesses sent in parallel cannot pass the limit together. Past
/// <see cref="MaxFailuresForEveryone"/> wrong passwords a minute on this DevKit, every typed password waits, so one
/// password guessed for many accounts stays slow. Accounts are keyed by <see cref="IWindowsCredentialChecker.AccountKey"/>,
/// and those whose window has passed are forgotten, so names typed once never pile up.
/// </summary>
internal sealed class SignInThrottle
{
    public const int MaxFailures = 3;
    public static readonly TimeSpan Window = TimeSpan.FromMinutes(15);

    /// <summary>
    /// Wrong passwords this DevKit takes in <see cref="EveryoneWindow"/> across all accounts, past which every typed
    /// password waits: a guess at one password for many accounts stays slow, while single sign-on goes on working.
    /// </summary>
    public const int MaxFailuresForEveryone = 30;

    public static readonly TimeSpan EveryoneWindow = TimeSpan.FromMinutes(1);

    private readonly ConcurrentDictionary<string, Failures> _failures = new(StringComparer.OrdinalIgnoreCase);
    private readonly TimeProvider _clock;
    private readonly ILogger<SignInThrottle>? _logger;

    // Attempts Windows is still checking, per account, and the times of the latest wrong passwords for anyone. Taken
    // together under one lock, so parallel attempts cannot all pass the check before any of them is counted.
    private readonly Lock _attempts = new();
    private readonly Dictionary<string, int> _checking = new(StringComparer.OrdinalIgnoreCase);
    private readonly Queue<DateTimeOffset> _everyoneFailures = new();
    private bool _everyoneStopped;

    public SignInThrottle(TimeProvider clock, ILogger<SignInThrottle>? logger = null)
    {
        _clock = clock;
        _logger = logger;
    }

    /// <summary>The accounts still remembered, for the tests that check old ones are forgotten.</summary>
    internal int Remembered => _failures.Count;

    /// <summary>
    /// Starts a password attempt for <paramref name="account"/>, unless it must wait: it has had its wrong passwords, or
    /// attempts still being checked would bring it there, or this DevKit took <see cref="MaxFailuresForEveryone"/> wrong
    /// passwords in the last minute. Each attempt started is ended with <see cref="End"/>.
    /// </summary>
    public AttemptStart TryBegin(string account)
    {
        lock (_attempts)
        {
            var now = _clock.GetUtcNow();
            if (EveryoneMustWait(now, out var everyoneWait))
            {
                return AttemptStart.Refused(everyoneWait, forEveryone: true);
            }

            // IsBlocked also forgets the account once its window has passed.
            IsBlocked(account, out var wait);
            if (FailuresOf(account, now) + _checking.GetValueOrDefault(account) >= MaxFailures)
            {
                return AttemptStart.Refused(wait > TimeSpan.Zero ? wait : EveryoneWindow, forEveryone: false);
            }

            _checking[account] = _checking.GetValueOrDefault(account) + 1;
            return AttemptStart.Started;
        }
    }

    /// <summary>Ends an attempt <see cref="TryBegin"/> started, counting it as Windows answered.</summary>
    public void End(string account, bool accepted)
    {
        lock (_attempts)
        {
            var left = _checking.GetValueOrDefault(account) - 1;
            if (left > 0)
            {
                _checking[account] = left;
            }
            else
            {
                _checking.Remove(account);
            }

            if (accepted)
            {
                RecordSuccess(account);
                return;
            }

            RecordFailure(account);
            _everyoneFailures.Enqueue(_clock.GetUtcNow());
        }
    }

    private int FailuresOf(string account, DateTimeOffset now)
    {
        if (!_failures.TryGetValue(account, out var failures))
        {
            return 0;
        }

        var (count, first) = failures.Read();
        return now - first < Window ? count : 0;
    }

    /// <summary>Whether every typed password waits now, and for how long; says so in the log when that starts.</summary>
    private bool EveryoneMustWait(DateTimeOffset now, out TimeSpan wait)
    {
        while (_everyoneFailures.TryPeek(out var oldest) && now - oldest >= EveryoneWindow)
        {
            _everyoneFailures.Dequeue();
        }

        wait = TimeSpan.Zero;
        if (_everyoneFailures.Count < MaxFailuresForEveryone)
        {
            _everyoneStopped = false;
            return false;
        }

        wait = _everyoneFailures.Peek() + EveryoneWindow - now;
        if (!_everyoneStopped)
        {
            _everyoneStopped = true;
            _logger?.LogWarning("{Failures} wrong passwords were typed on this DevKit within a minute, so typed passwords wait; single sign-on goes on working",
                MaxFailuresForEveryone);
        }

        return true;
    }

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

    /// <summary>What <see cref="TryBegin"/> decided: go ahead, or wait this long, for this account alone or for everyone.</summary>
    internal readonly record struct AttemptStart(bool IsStarted, TimeSpan Wait, bool ForEveryone)
    {
        public static AttemptStart Started => new(true, TimeSpan.Zero, false);

        public static AttemptStart Refused(TimeSpan wait, bool forEveryone) => new(false, wait, forEveryone);
    }

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
