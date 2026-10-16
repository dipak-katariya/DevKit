using System.Net.Security;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Claims;
using System.Security.Principal;
using Microsoft.AspNetCore.Connections.Features;
using Microsoft.Win32.SafeHandles;

namespace DevKit.Web.Services;

/// <summary>Someone signed in to shared DevKit: their Windows account's security identifier and name.</summary>
/// <param name="IsOwner">Whether this is the Windows account DevKit itself runs as.</param>
public sealed record SignedInUser(string Id, string Name, bool IsOwner)
{
    /// <summary>The account with this identifier and name; it is the owner when it is the account DevKit runs as.</summary>
    public static SignedInUser Of(string id, string name, string ownerId) =>
        new(id, name, string.Equals(id, ownerId, StringComparison.Ordinal));

    /// <summary>The person a signed-in principal names, or null when it names nobody.</summary>
    public static SignedInUser? From(ClaimsPrincipal principal, string ownerId)
    {
        ArgumentNullException.ThrowIfNull(principal);
        var id = principal.FindFirstValue(ClaimTypes.NameIdentifier);
        var name = principal.Identity?.Name;
        return principal.Identity?.IsAuthenticated == true && !string.IsNullOrEmpty(id) && !string.IsNullOrEmpty(name)
            ? Of(id, name, ownerId)
            : null;
    }

    /// <summary>The person a Windows identity belongs to, or null for one without a security identifier.</summary>
    [SupportedOSPlatform("windows")]
    public static SignedInUser? From(WindowsIdentity identity, string ownerId)
    {
        ArgumentNullException.ThrowIfNull(identity);
        return identity.User?.Value is { } id ? Of(id, identity.Name, ownerId) : null;
    }
}

/// <summary>The Windows account DevKit runs as: the only one that may use Windows sign-in to TFS in shared mode.</summary>
public sealed class OwnerAccount
{
    public OwnerAccount(string id)
    {
        Id = id;
    }

    /// <summary>The account's security identifier.</summary>
    public string Id { get; }

    [SupportedOSPlatform("windows")]
    public static OwnerAccount Current()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return new OwnerAccount(
            identity.User?.Value ?? throw new InvalidOperationException("The Windows account DevKit runs as has no security identifier."));
    }
}

/// <summary>What checking a Windows username and password found: the person, or why there is none.</summary>
/// <param name="Error">What the sign-in page says. It never tells a guesser that an account exists, is locked or is disabled.</param>
/// <param name="Detail">Why the sign-in was refused, for the audit log only.</param>
public readonly record struct CredentialCheck(SignedInUser? User, string Error, string Detail)
{
    /// <summary>The longest username or password accepted; anything longer is refused before Windows sees it.</summary>
    public const int MaxLength = 256;

    public static CredentialCheck Accepted(SignedInUser user) => new(user, "", "");

    public static CredentialCheck Refused(string error, string detail) => new(null, error, detail);
}

/// <summary>Asks Windows whether a password belongs to a username. DevKit itself never keeps either.</summary>
public interface IWindowsCredentialChecker
{
    CredentialCheck Check(string userName, string password);

    /// <summary>
    /// One key for every way of typing the same account ("MT\jdoe", "jdoe", "jdoe@corp.example", "corp.example\jdoe"),
    /// so guesses are counted per account however they are typed.
    /// </summary>
    string AccountKey(string userName);
}

/// <summary>Where a single sign-on handshake stands after one request.</summary>
internal enum SingleSignOnState
{
    /// <summary>The browser has not started: it is asked to sign in with NTLM.</summary>
    Challenge,

    /// <summary>The browser's first message was answered; its proof comes on the next request.</summary>
    Continue,

    /// <summary>Windows accepted the proof: <see cref="SingleSignOnStep.User"/> is the person.</summary>
    Completed,

    /// <summary>The handshake failed: <see cref="SingleSignOnStep.Error"/> says why.</summary>
    Failed
}

internal readonly record struct SingleSignOnStep(SingleSignOnState State, SignedInUser? User, string Error)
{
    public static SingleSignOnStep Of(SingleSignOnState state) => new(state, null, "");

    public static SingleSignOnStep Fail(string error) => new(SingleSignOnState.Failed, null, error);
}

/// <summary>
/// Turns a Windows sign-in, single sign-on or a typed password, into the person it names. Typed passwords are
/// throttled per account before Windows sees them (see <see cref="SignInThrottle"/>).
/// <para>
/// Single sign-on is NTLM, run here with .NET's own <see cref="NegotiateAuthentication"/>. DevKit runs under a
/// person's Windows account, which cannot read the Kerberos tickets browsers ask the domain for when they reach this
/// machine by name, so offering only NTLM keeps them from trying Kerberos: sign-on then works by machine name, full
/// name or address with nothing registered by IT. The password never crosses the network: the browser answers
/// NTLM's challenge, and Windows checks the answer with the domain. NTLM's three messages must share one connection,
/// so the handshake is kept with the connection, never longer.
/// </para>
/// </summary>
internal sealed class WindowsSignIn
{
    private const string NtlmScheme = "NTLM";
    private const string NtlmPrefix = NtlmScheme + " ";
    private const int NtlmTypeOffset = 8;
    private const byte NtlmNegotiateType = 1;

    private static readonly object HandshakeKey = new();

    private readonly IWindowsCredentialChecker _checker;
    private readonly SignInThrottle _throttle;
    private readonly OwnerAccount _owner;

    public WindowsSignIn(IWindowsCredentialChecker checker, SignInThrottle throttle, OwnerAccount owner)
    {
        _checker = checker;
        _throttle = throttle;
        _owner = owner;
    }

    /// <summary>
    /// Takes the single sign-on handshake one step on this request. A request that does not carry an NTLM message
    /// starts it again, with the challenge header set on the response; any handshake already on the connection is
    /// dropped, so an attempt never resumes another one.
    /// </summary>
    public SingleSignOnStep ContinueSingleSignOn(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var connection = context.Features.Get<IConnectionItemsFeature>()?.Items;
        var header = context.Request.Headers.Authorization.ToString();
        if (connection is null || !header.StartsWith(NtlmPrefix, StringComparison.OrdinalIgnoreCase))
        {
            DropHandshake(connection);
            context.Response.Headers.WWWAuthenticate = NtlmScheme;
            return SingleSignOnStep.Of(SingleSignOnState.Challenge);
        }

        if (!TryDecode(header[NtlmPrefix.Length..], out var incoming))
        {
            DropHandshake(connection);
            return SingleSignOnStep.Fail("The browser sent a Windows sign-in DevKit could not read.");
        }

        if (IsFirstMessage(incoming))
        {
            DropHandshake(connection);
        }

        var handshake = connection.TryGetValue(HandshakeKey, out var kept) && kept is NegotiateAuthentication existing
            ? existing
            : StartHandshake(context, connection);
        var outgoing = handshake.GetOutgoingBlob(incoming, out var status);
        if (status == NegotiateAuthenticationStatusCode.ContinueNeeded && outgoing is not null)
        {
            context.Response.Headers.WWWAuthenticate = $"{NtlmScheme} {Convert.ToBase64String(outgoing)}";
            return SingleSignOnStep.Of(SingleSignOnState.Continue);
        }

        try
        {
            return status == NegotiateAuthenticationStatusCode.Completed && UserOf(handshake) is { } user
                ? new SingleSignOnStep(SingleSignOnState.Completed, user, "")
                : SingleSignOnStep.Fail($"Windows did not accept the sign-in ({status}).");
        }
        finally
        {
            DropHandshake(connection);
        }
    }

    public CredentialCheck CheckPassword(string userName, string password)
    {
        var account = _checker.AccountKey(userName);
        if (_throttle.IsBlocked(account, out var wait))
        {
            return CredentialCheck.Refused(
                $"Too many wrong passwords for this account. Try again in {Math.Max(1, (int)Math.Ceiling(wait.TotalMinutes))} minute(s), or use \"Sign in with your Windows account\".",
                "stopped after too many wrong passwords");
        }

        var check = _checker.Check(userName, password);
        if (check.User is null)
        {
            _throttle.RecordFailure(account);
        }
        else
        {
            _throttle.RecordSuccess(account);
        }

        return check;
    }

    /// <summary>NTLM's negotiate message: "NTLMSSP", a zero byte, then message type 1.</summary>
    private static bool IsFirstMessage(byte[] message) =>
        message.Length > NtlmTypeOffset && message[NtlmTypeOffset] == NtlmNegotiateType;

    private static bool TryDecode(string text, out byte[] message)
    {
        var trimmed = text.Trim();
        var buffer = new byte[(trimmed.Length * 3 / 4) + 3];
        if (!Convert.TryFromBase64String(trimmed, buffer, out var written) || written == 0)
        {
            message = [];
            return false;
        }

        message = buffer[..written];
        return true;
    }

    /// <summary>A new handshake kept with this connection, and released with it if the browser never finishes.</summary>
    private static NegotiateAuthentication StartHandshake(HttpContext context, IDictionary<object, object?> connection)
    {
        var handshake = new NegotiateAuthentication(new NegotiateAuthenticationServerOptions { Package = NtlmScheme });
        connection[HandshakeKey] = handshake;
        context.Features.Get<IConnectionCompleteFeature>()?.OnCompleted(
            static state =>
            {
                DropHandshake((IDictionary<object, object?>)state);
                return Task.CompletedTask;
            },
            connection);
        return handshake;
    }

    private static void DropHandshake(IDictionary<object, object?>? connection)
    {
        if (connection is not null && connection.Remove(HandshakeKey, out var kept))
        {
            (kept as IDisposable)?.Dispose();
        }
    }

    /// <summary>The person a finished handshake proved to be. The identity Windows hands out is read, then released.</summary>
    private SignedInUser? UserOf(NegotiateAuthentication handshake)
    {
        if (!OperatingSystem.IsWindows() || handshake.RemoteIdentity is not WindowsIdentity identity)
        {
            return null;
        }

        using (identity)
        {
            return SignedInUser.From(identity, _owner.Id);
        }
    }
}

/// <summary>
/// Checks a username and password with Windows itself, as a network logon against the domain: nothing runs as the
/// person, no profile is loaded, and the token Windows hands back is closed unused. Who the account is (its security
/// identifier and its name as Windows writes it) is then looked up by name.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class WindowsCredentialChecker : IWindowsCredentialChecker
{
    private const int NetworkLogon = 3;
    private const int DefaultProvider = 0;
    private const string NotLookedUp = "password accepted, but the account could not be looked up";

    private const int LogonFailure = 1326;
    private const int AccountRestriction = 1327;
    private const int PasswordExpired = 1330;
    private const int AccountDisabled = 1331;
    private const int LogonTypeNotGranted = 1385;
    private const int MustChangePassword = 1907;
    private const int AccountLockedOut = 1909;

    // One answer for every refusal, so the page never tells a guesser that an account exists, is locked or is
    // disabled; the audit log records which it was.
    private const string NotAccepted =
        "Windows did not accept this username and password. If they are right, the account may be locked or its password expired: ask IT.";

    private readonly string _defaultDomain;
    private readonly string _ownerId;
    private readonly ILogger<WindowsCredentialChecker> _logger;

    /// <param name="options">Its <see cref="DevKitHostingOptions.Domain"/> is the domain of a username typed without one.</param>
    public WindowsCredentialChecker(DevKitHostingOptions options, OwnerAccount owner, ILogger<WindowsCredentialChecker> logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(owner);
        _defaultDomain = options.Domain;
        _ownerId = owner.Id;
        _logger = logger;
    }

    public CredentialCheck Check(string userName, string password)
    {
        if (!TrySplit(userName, _defaultDomain, out var user, out var domain)
            || string.IsNullOrEmpty(password) || password.Length > CredentialCheck.MaxLength)
        {
            return CredentialCheck.Refused(NotAccepted, "username or password in a form Windows does not take");
        }

        var accepted = LogonUser(user, domain, password, NetworkLogon, DefaultProvider, out var token);
        var error = Marshal.GetLastPInvokeError();
        using (token)
        {
            return accepted ? Identify(Account(user, domain)) : CredentialCheck.Refused(NotAccepted, Describe(error));
        }
    }

    /// <summary>
    /// The account's security identifier when Windows knows the name, however it was typed ("MT\jdoe", "jdoe",
    /// "jdoe@corp.example", "corp.example\jdoe" all give one); otherwise the name as typed, in one case, so guesses at
    /// a name Windows does not know are still counted together.
    /// </summary>
    public string AccountKey(string userName)
    {
        if (!TrySplit(userName, _defaultDomain, out var user, out var domain))
        {
            return (userName ?? "").Trim().ToUpperInvariant();
        }

        var account = Account(user, domain);
        return IdOf(account)?.Value ?? account.ToUpperInvariant();
    }

    private static string Account(string user, string? domain) => domain is null ? user : $"{domain}\\{user}";

    /// <summary>The account Windows just accepted the password of, as its identifier and its name as Windows writes it.</summary>
    private CredentialCheck Identify(string account)
    {
        if (IdOf(account) is not { } id)
        {
            return CredentialCheck.Refused(NotAccepted, NotLookedUp);
        }

        try
        {
            return CredentialCheck.Accepted(SignedInUser.Of(id.Value, ((NTAccount)id.Translate(typeof(NTAccount))).Value, _ownerId));
        }
        catch (SystemException ex)
        {
            // SecurityIdentifier.Translate reports an identifier it cannot name, or a Win32 failure, as these.
            _logger.LogError(ex, "Windows accepted a password but the account's name could not be looked up");
            return CredentialCheck.Refused(NotAccepted, NotLookedUp);
        }
    }

    /// <summary>
    /// The security identifier Windows has for <paramref name="account"/>, or null when it knows no such account. An
    /// unknown name is an answer rather than a failure, so it is asked without throwing; only a lookup that could not
    /// be made at all is logged.
    /// </summary>
    private SecurityIdentifier? IdOf(string account)
    {
        try
        {
            return new IdentityReferenceCollection { new NTAccount(account) }
                .Translate(typeof(SecurityIdentifier), forceSuccess: false)[0] as SecurityIdentifier;
        }
        catch (SystemException ex)
        {
            _logger.LogError(ex, "Windows could not look up an account name");
            return null;
        }
    }

    /// <summary>
    /// "DOMAIN\user", "user@domain" (passed whole, as Windows reads it) or a bare "user" in the default domain.
    /// Anything else, or anything too long or holding control characters, is refused before Windows sees it.
    /// </summary>
    internal static bool TrySplit(string? typed, string defaultDomain, out string user, out string? domain)
    {
        user = (typed ?? "").Trim();
        domain = null;
        if (user.Length == 0 || user.Length > CredentialCheck.MaxLength || user.Any(char.IsControl))
        {
            return false;
        }

        var slash = user.IndexOf('\\', StringComparison.Ordinal);
        if (slash >= 0)
        {
            domain = user[..slash];
            user = user[(slash + 1)..];
            return domain.Length > 0 && user.Length > 0 && !user.Contains('\\', StringComparison.Ordinal);
        }

        if (!user.Contains('@', StringComparison.Ordinal))
        {
            domain = defaultDomain;
        }

        return true;
    }

    /// <summary>Why Windows refused, for the audit log.</summary>
    private static string Describe(int error) => error switch
    {
        LogonFailure => "wrong username or password",
        AccountLockedOut => "account locked out",
        AccountDisabled => "account disabled",
        PasswordExpired or MustChangePassword => "password expired",
        AccountRestriction => "account restrictions",
        LogonTypeNotGranted => "network sign-in to this server not allowed for the account",
        _ => $"Windows error {error}"
    };

    [DllImport("advapi32.dll", EntryPoint = "LogonUserW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool LogonUser(
        string userName, string? domain, string password, int logonType, int logonProvider, out SafeAccessTokenHandle token);
}
