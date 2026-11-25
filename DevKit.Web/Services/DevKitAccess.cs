using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.RegularExpressions;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components.Server;
using Microsoft.AspNetCore.Components.Server.Circuits;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.HostFiltering;
using Microsoft.Extensions.Configuration.EnvironmentVariables;
using Microsoft.Extensions.Configuration.Json;

namespace DevKit.Web.Services;

/// <summary>How DevKit is run.</summary>
public enum DevKitMode
{
    /// <summary>For the person who started it, on this machine only, opened with the link it prints.</summary>
    Local,

    /// <summary>Shared on a server: everyone opens it by machine name and port and signs in with their Windows account.</summary>
    Hosted
}

/// <summary>
/// This installation's own configuration, kept on the machine and never in the repository: devkit.json in DevKit's
/// folder of the account it runs as, beside the settings file (<see cref="SettingsFile.PathVariable"/> moves both).
/// Only that account, SYSTEM and administrators can open the folder. The file holds the "DevKit" section (shared mode,
/// the TFS server, the session length) and anything else ASP.NET Core reads from configuration, such as an https
/// endpoint for Kestrel. Without it DevKit runs in local mode.
/// </summary>
public static class DevKitMachineConfig
{
    public const string FileName = "devkit.json";

    /// <summary>DevKit's folder on this machine, which also holds the settings file, the sign-in audit and the keys.</summary>
    public static string Folder => Path.GetDirectoryName(SettingsFile.ResolvePath())!;

    public static string FilePath => Path.Combine(Folder, FileName);

    /// <summary>
    /// Why DevKit's folder cannot be trusted: a link, or another account than this one, SYSTEM and administrators may
    /// change it or something in it. Whoever may change it may change devkit.json, the keys that sign people in or the
    /// list of sign-outs. Null when nobody else may, and when the folder is not there yet.
    /// </summary>
    [SupportedOSPlatform("windows")]
    public static string? OpenToOthers() => OpenToOthers(Folder);

    /// <summary>Why <paramref name="path"/>, as DevKit's folder, cannot be trusted (<see cref="OpenToOthers()"/>).</summary>
    [SupportedOSPlatform("windows")]
    internal static string? OpenToOthers(string path)
    {
        var folder = new DirectoryInfo(path);
        if (!folder.Exists)
        {
            return null;
        }

        var problem = folder.Attributes.HasFlag(FileAttributes.ReparsePoint)
            ? $"{folder.FullName} is a link to another folder"
            : FolderAccess.OtherWriter(folder.FullName, AccessScope.FolderAndContents);
        return problem is null
            ? null
            : $"DevKit's folder cannot be trusted: {problem}. Whoever may change it may change devkit.json and the keys that sign " +
              $"people in. Leave it to this account, SYSTEM and administrators only (tools\\devkit-lock.bat \"{folder.FullName}\"), " +
              "and make this account the owner of anything another account owns in it.";
    }

    /// <summary>
    /// Shared mode: makes DevKit's folder when it is not there yet, open to this account, SYSTEM and administrators only,
    /// and refuses to start when it is open to anyone else (<see cref="OpenToOthers"/>).
    /// </summary>
    [SupportedOSPlatform("windows")]
    public static void EnsureClosed() => EnsureClosed(Folder);

    /// <summary><see cref="EnsureClosed()"/> for <paramref name="path"/> as DevKit's folder.</summary>
    [SupportedOSPlatform("windows")]
    internal static void EnsureClosed(string path)
    {
        var folder = new DirectoryInfo(path);
        if (!folder.Exists)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(folder.FullName)!);
            folder.Create(ClosedFolder());
        }

        if (OpenToOthers(path) is { } problem)
        {
            throw new InvalidOperationException(problem);
        }
    }

    [SupportedOSPlatform("windows")]
    private static DirectorySecurity ClosedFolder()
    {
        using var account = WindowsIdentity.GetCurrent();
        var security = new DirectorySecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        foreach (var trusted in new[]
                 {
                     account.User ?? throw new InvalidOperationException("The Windows account DevKit runs as has no security identifier."),
                     new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
                     new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null)
                 })
        {
            security.AddAccessRule(new FileSystemAccessRule(trusted, FileSystemRights.FullControl,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        }

        return security;
    }

    /// <summary>
    /// Reads the file after appsettings.json and before the environment variables and the command line: a value there
    /// replaces the shipped default, and a DevKit__… variable or a --DevKit:… argument still replaces it.
    /// </summary>
    public static void AddTo(IConfigurationBuilder configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var source = new JsonConfigurationSource { Path = FilePath, Optional = true, ReloadOnChange = false };
        source.ResolveFileProvider();
        var sources = configuration.Sources;
        var environment = sources.ToList().FindLastIndex(s => s is EnvironmentVariablesConfigurationSource { Prefix: null or "" });
        sources.Insert(environment < 0 ? sources.Count : environment, source);
    }
}

/// <summary>
/// The "DevKit" configuration section, from this machine's devkit.json (<see cref="DevKitMachineConfig"/>), DevKit__…
/// environment variables or --DevKit:… arguments. Only <see cref="Mode"/> matters in local mode.
/// </summary>
public sealed partial class DevKitHostingOptions
{
    public const string SectionName = "DevKit";
    private const string SidPrefix = "S-1-";
    public const int MaxSessionDays = 30;

    // A month: with automatic Windows sign-in, people see the sign-in page about never.
    public const int DefaultSessionDays = 30;

    public DevKitMode Mode { get; set; } = DevKitMode.Local;

    /// <summary>The one TFS server everyone works against in hosted mode; people cannot point DevKit elsewhere.</summary>
    public string TfsUrl { get; set; } = "";

    /// <summary>The domain a username typed without one belongs to; this machine's domain when left out.</summary>
    public string Domain { get; set; } = "";

    /// <summary>
    /// How long a sign-in lasts. It does not slide: after this many days everyone signs in again, which a browser on
    /// the domain does by itself (<see cref="SignInFlow.AutomaticPath"/>).
    /// </summary>
    public int SessionDays { get; set; } = DefaultSessionDays;

    /// <summary>
    /// The names browsers may use to reach DevKit; a request for any other name is refused, which stops DNS-rebinding
    /// pages. This machine's own names and addresses when left out.
    /// </summary>
    public List<string> HostNames { get; set; } = new();

    /// <summary>Where the sign-in audit log is written; "audit" in <see cref="DevKitMachineConfig.Folder"/> when left out.</summary>
    public string AuditFolder { get; set; } = "";

    /// <summary>
    /// Whether shared DevKit may listen on plain http that other machines reach, where sign-ins and settings cross the
    /// network unencrypted. Off unless devkit.json turns it on: shared DevKit then refuses to start without https.
    /// </summary>
    public bool AllowPlainHttp { get; set; }

    /// <summary>
    /// The Windows groups (names such as MT\DevKit Users, or SIDs) whose members may sign in; everyone the domain knows
    /// when left out. The account DevKit runs as may always sign in. A removal from a group takes effect at the next
    /// sign-in, so at the latest after <see cref="SessionDays"/>.
    /// </summary>
    public List<string> AllowedGroups { get; set; } = new();

    /// <summary>The security identifiers of <see cref="AllowedGroups"/>, looked up once at start; empty when anyone may sign in.</summary>
    internal IReadOnlySet<string> AllowedGroupIds { get; private set; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    /// <summary>Whether DevKit listens on https alone, so its session cookie can be one browsers only ever send over https.</summary>
    internal bool HttpsOnly { get; private set; }

    public bool IsHosted => Mode == DevKitMode.Hosted;

    public TimeSpan SessionLength => TimeSpan.FromDays(SessionDays);

    /// <summary>
    /// Reads the section and fills in what was left out from this machine. Throws at startup, before anyone can
    /// connect, when hosted mode lacks what it needs.
    /// </summary>
    public static DevKitHostingOptions Read(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var options = configuration.GetSection(SectionName).Get<DevKitHostingOptions>() ?? new DevKitHostingOptions();
        if (!options.IsHosted)
        {
            return options;
        }

        if (!TfsConnectionForm.TryValidateUrl(options.TfsUrl, out var url, out var error))
        {
            throw new InvalidOperationException(
                $"{SectionName}:{nameof(TfsUrl)} is needed in hosted mode; set it in {DevKitMachineConfig.FilePath}. {error}");
        }

        if (options.SessionDays is < 1 or > MaxSessionDays)
        {
            throw new InvalidOperationException($"{SectionName}:{nameof(SessionDays)} must be between 1 and {MaxSessionDays}.");
        }

        if (!options.AllowPlainHttp && HostEndpoints.PlainHttpForOthers(configuration) is { } plain)
        {
            throw new InvalidOperationException(
                $"Shared DevKit would listen on {plain}, plain http that other machines reach, so sign-ins and settings would cross " +
                $"the network unencrypted. Give it an https endpoint in the \"Kestrel\" section of {DevKitMachineConfig.FilePath}, " +
                $"or set {SectionName}:{nameof(AllowPlainHttp)} to true there to allow plain http all the same.");
        }

        options.TfsUrl = url.TrimEnd('/');
        options.Domain = string.IsNullOrWhiteSpace(options.Domain) ? Environment.UserDomainName : options.Domain.Trim();
        options.HostNames = options.HostNames.Count > 0
            ? options.HostNames.Select(name => name.Trim()).Where(name => name.Length > 0).ToList()
            : ThisMachinesNames();
        options.AuditFolder = string.IsNullOrWhiteSpace(options.AuditFolder)
            ? Path.Combine(DevKitMachineConfig.Folder, "audit")
            : Path.GetFullPath(options.AuditFolder);
        if (OperatingSystem.IsWindows())
        {
            options.AllowedGroupIds = GroupIds(options.AllowedGroups);
        }

        options.HttpsOnly = HostEndpoints.Read(configuration)
            .All(address => address.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase));
        return options;
    }

    /// <summary>
    /// The security identifier of each group, given by name or as a SID. Throws at start for one Windows does not know,
    /// rather than letting nobody, or everybody, in.
    /// </summary>
    [SupportedOSPlatform("windows")]
    internal static HashSet<string> GroupIds(IEnumerable<string> groups)
    {
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var group in groups.Select(name => name.Trim()).Where(name => name.Length > 0))
        {
            if (group.StartsWith(SidPrefix, StringComparison.OrdinalIgnoreCase))
            {
                if (!SidShape().IsMatch(group))
                {
                    throw new InvalidOperationException($"{SectionName}:{nameof(AllowedGroups)} names \"{group}\", which is not a SID.");
                }

                ids.Add(new SecurityIdentifier(group).Value);
                continue;
            }

            if (new IdentityReferenceCollection { new NTAccount(group) }.Translate(typeof(SecurityIdentifier), forceSuccess: false)[0] is not SecurityIdentifier id)
            {
                throw new InvalidOperationException($"{SectionName}:{nameof(AllowedGroups)} names \"{group}\", which Windows does not know.");
            }

            ids.Add(id.Value);
        }

        return ids;
    }

    [GeneratedRegex(@"^S-1-\d+(-\d+)+$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SidShape();

    /// <summary>This machine's full name on its domain, such as devbox.example.com, read without asking DNS.</summary>
    internal static string FullMachineName()
    {
        var network = IPGlobalProperties.GetIPGlobalProperties();
        return string.IsNullOrEmpty(network.DomainName) ? network.HostName : $"{network.HostName}.{network.DomainName}";
    }

    /// <summary>This machine's DNS domain, such as example.com; empty when it has none.</summary>
    internal static string MachineDnsDomain() => IPGlobalProperties.GetIPGlobalProperties().DomainName;

    /// <summary>Localhost, this machine's name and full name, and its IPv4 addresses, read without asking DNS.</summary>
    private static List<string> ThisMachinesNames() =>
        LocalAccessGate.LocalHostNames
            .Append(Environment.MachineName)
            .Append(FullMachineName())
            .Concat(NetworkInterface.GetAllNetworkInterfaces()
                .Where(card => card.OperationalStatus == OperationalStatus.Up)
                .SelectMany(card => card.GetIPProperties().UnicastAddresses)
                .Where(unicast => unicast.Address.AddressFamily == AddressFamily.InterNetwork)
                .Select(unicast => unicast.Address.ToString()))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
}

/// <summary>
/// How people get into DevKit, chosen once from this machine's configuration: <see cref="LocalAccess"/> or
/// <see cref="HostedAccess"/>. Everything that differs between the two lives behind this, so startup never asks which
/// mode it is in.
/// </summary>
public abstract class DevKitAccess
{
    // Images may come from https sites as well, for the pictures a document in the Markdown Viewer links to: the app is
    // one page, so a policy for that page alone would hold for every page after it. Nothing else leaves DevKit's origin:
    // 'self' covers its own WebSocket (ws: and wss: of the same address).
    internal const string ContentSecurityPolicy =
        "default-src 'self'; " +
        "script-src 'self'; " +
        "style-src 'self' 'unsafe-inline'; " +
        "img-src 'self' data: https:; " +
        "font-src 'self'; " +
        "connect-src 'self'; " +
        "object-src 'none'; " +
        "frame-ancestors 'none'; " +
        "base-uri 'self'; " +
        "form-action 'self'";

    private const string StrictTransportSecurity = "max-age=31536000";

    protected DevKitAccess(DevKitHostingOptions options)
    {
        Options = options;
    }

    public DevKitHostingOptions Options { get; }

    /// <summary>The Referrer-Policy header every response carries.</summary>
    public abstract string ReferrerPolicy { get; }

    /// <summary>Whether an unhandled error gets the plain page even in Development, because other people can see it.</summary>
    public abstract bool AlwaysHidesErrorDetails { get; }

    /// <summary>Reads this machine's devkit.json, then picks the access for its mode and registers what it needs.</summary>
    public static DevKitAccess AddTo(WebApplicationBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        DevKitMachineConfig.AddTo(builder.Configuration);
        var options = DevKitHostingOptions.Read(builder.Configuration);
        DevKitAccess access = options.IsHosted ? new HostedAccess(options) : new LocalAccess(options);
        builder.Services.AddSingleton(options);
        access.AddServices(builder.Services);
        return access;
    }

    /// <summary>
    /// The security headers every response carries, the maintenance page's too. The CSP allows inline styles (the UI
    /// uses many style="…" attributes) but no inline/eval scripts, which neutralizes injected-markup script vectors.
    /// Set via OnStarting so these are authoritative over framework-emitted defaults (the Blazor endpoint otherwise adds
    /// its own frame-ancestors directive, producing a duplicate header).
    /// </summary>
    public static void UseSecurityHeaders(WebApplication app, string referrerPolicy)
    {
        ArgumentNullException.ThrowIfNull(app);
        app.Use(async (context, next) =>
        {
            context.Response.OnStarting(() =>
            {
                var headers = context.Response.Headers;
                headers["X-Content-Type-Options"] = "nosniff";
                headers["X-Frame-Options"] = "DENY";
                headers["Referrer-Policy"] = referrerPolicy;
                headers["Content-Security-Policy"] = ContentSecurityPolicy;
                // No other site's window can reach DevKit's, no other site may embed its files, and pages get no camera,
                // microphone or location.
                headers["Cross-Origin-Opener-Policy"] = "same-origin";
                headers["Cross-Origin-Resource-Policy"] = "same-origin";
                headers["Permissions-Policy"] = "camera=(), microphone=(), geolocation=()";
                // A browser that has seen DevKit over https never tries plain http for it again, for a year. Only this
                // name: the domain's other sites are none of DevKit's business.
                if (context.Request.IsHttps)
                {
                    headers.StrictTransportSecurity = StrictTransportSecurity;
                }

                return Task.CompletedTask;
            });
            await next();
        });
    }

    /// <summary>Leaves out the Server header, which would tell every visitor which web server DevKit runs on.</summary>
    public static void HideServerName(IWebHostBuilder webHost) =>
        webHost.ConfigureKestrel(kestrel => kestrel.AddServerHeader = false);

    /// <summary>The checks that run before anything else answers, static files included.</summary>
    public abstract void UseGate(WebApplication app);

    /// <summary>Who is signed in, and the routes that sign people in and out; runs after static files.</summary>
    public abstract void UseSignIn(WebApplication app);

    /// <summary>Tells whoever started DevKit where to open it, once it listens.</summary>
    public abstract Task AnnounceAsync(WebApplication app);

    protected abstract void AddServices(IServiceCollection services);
}

/// <summary>
/// Local mode: only the person who started DevKit, on this machine, with the link it prints and opens. Settings live
/// in their own settings file.
/// </summary>
internal sealed class LocalAccess : DevKitAccess
{
    // Set to 1 by tests and side-by-side runs, which read the sign-in link from the output instead.
    private const string NoBrowserVariable = "DEVKIT_NO_BROWSER";

    public LocalAccess(DevKitHostingOptions options)
        : base(options)
    {
    }

    public override string ReferrerPolicy => "no-referrer";

    public override bool AlwaysHidesErrorDetails => false;

    public override void UseGate(WebApplication app) => app.Use(app.Services.GetRequiredService<LocalAccessGate>().InvokeAsync);

    public override void UseSignIn(WebApplication app)
    {
        // The run's key, checked by the gate, is the only sign-in.
    }

    public override Task AnnounceAsync(WebApplication app)
    {
        // Local mode keeps no sign-in keys there, so an open folder is said rather than refused.
        if (OperatingSystem.IsWindows() && DevKitMachineConfig.OpenToOthers() is { } open)
        {
            app.Logger.LogWarning("{Problem}", open);
        }

        Announce(app);
        return Task.CompletedTask;
    }

    /// <summary>
    /// The sign-in link carries this run's key, so it goes to the starter's own console and browser and never through
    /// the logger, whose output may be kept.
    /// </summary>
    private static void Announce(WebApplication app)
    {
        var address = app.Urls.FirstOrDefault(IsLocalLaunchUrl);
        if (address is null)
        {
            Console.WriteLine("DevKit only answers on localhost; start it with --urls http://localhost:<port>.");
            return;
        }

        var link = app.Services.GetRequiredService<LocalAccessGate>().SignInLink(address);
        Console.WriteLine($"DevKit is ready: {link}");
        if (Environment.GetEnvironmentVariable(NoBrowserVariable) == "1")
        {
            return;
        }

        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo { FileName = link, UseShellExecute = true })?.Dispose();
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            app.Logger.LogError(ex, "The browser could not be opened; open the link DevKit printed instead");
        }
    }

    protected override void AddServices(IServiceCollection services)
    {
        services.AddSingleton<IConnectionAccounts>(OperatingSystem.IsWindows() ? new WindowsConnectionAccounts() : new UncheckedConnectionAccounts());
        services.AddSingleton<LocalAccessGate>();
        services.AddSingleton<SettingsService>();
        services.AddScoped<UserSession, LocalUserSession>();
    }

    private static bool IsLocalLaunchUrl(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri)
        && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps)
        && uri.IsLoopback;
}

/// <summary>
/// Hosted mode: shared on a server. Everyone opens DevKit by machine name and signs in with their Windows account; a
/// session lasts <see cref="DevKitHostingOptions.SessionDays"/>; each person's settings live in their own browser.
/// </summary>
internal sealed class HostedAccess : DevKitAccess
{
    internal const string PasswordSignInRateLimitPolicy = "devkit-password-sign-in";
    internal const string WindowsSignInRateLimitPolicy = "devkit-windows-sign-in";

    private const string SessionCookieName = "devkit-session";

    // Over https alone: a __Host- cookie must be Secure, for this host only and the whole site, which browsers enforce, so
    // no plain http page and no other site of the domain can set or replace it.
    private const string HttpsSessionCookieName = "__Host-devkit-session";
    private const string BlazorHubPath = "/_blazor";
    private const int PasswordAttemptsPerMinute = 10;
    private const string ThisMachinePartition = "this machine";
    private const string SignedOutFileName = "signed-out.txt";

    // A Windows sign-in takes up to three requests, and each wrong NTLM answer is a password guess the domain checks.
    private const int WindowsRequestsPerMinute = 30;

    public HostedAccess(DevKitHostingOptions options)
        : base(options)
    {
    }

    // Hosted mode posts forms (sign-in, sign-out) and checks their Origin. Under "no-referrer" a browser names a page's
    // own form post's origin "null"; "same-origin" still tells other sites nothing and gives DevKit its real origin.
    public override string ReferrerPolicy => "same-origin";

    public override bool AlwaysHidesErrorDetails => true;

    /// <summary>
    /// A request another site sent is refused. The host name was already checked by ASP.NET Core's host filtering, set
    /// to <see cref="DevKitHostingOptions.HostNames"/>; who the person is comes after, from the sign-in.
    /// </summary>
    public override void UseGate(WebApplication app)
    {
        var logger = app.Services.GetRequiredService<ILogger<HostedAccess>>();
        app.Use((context, next) => LocalAccessGate.RefusedOtherOrigin(context, logger) ? Task.CompletedTask : next(context));
    }

    public override void UseSignIn(WebApplication app)
    {
        // Made now, so the log says at start whether the domain locks keys for the account DevKit runs as, and so DevKit
        // does not start at all when the settings folder cannot be closed to everyone but that account.
        app.Services.GetRequiredService<ClientVault>();
        if (OperatingSystem.IsWindows())
        {
            app.Services.GetRequiredService<PersonVault>();
        }
        app.UseAuthentication();
        app.UseRateLimiter();
        app.UseAuthorization();
        app.MapSignInEndpoints();
        app.MapClientVaultEndpoints();
    }

    /// <summary>Hosted mode has no link to keep secret: it prints the address to give people, who sign in with Windows.</summary>
    public override async Task AnnounceAsync(WebApplication app)
    {
        var logger = app.Services.GetRequiredService<ILogger<HostedAccess>>();
        var links = new List<string>();
        foreach (var listening in app.Urls)
        {
            links.Add(await LinkToShareAsync(listening, logger, app.Lifetime.ApplicationStopping));
        }

        Console.WriteLine($"DevKit is shared at: {string.Join(", ", links)} (everyone signs in with their Windows account)");
    }

    /// <summary>
    /// The address people open for one address Kestrel listens on. One on every address ([::] or 0.0.0.0) gets this
    /// machine's name: the short one for http, and for https the one its certificate covers (<see cref="HttpsSelfCheck"/>),
    /// or the full name, which certificates are usually issued for, when the check failed.
    /// </summary>
    internal static async Task<string> LinkToShareAsync(string listening, ILogger logger, CancellationToken cancellationToken)
    {
        if (!Uri.TryCreate(listening, UriKind.Absolute, out var uri) || uri.Host is not ("0.0.0.0" or "[::]"))
        {
            return listening;
        }

        if (uri.Scheme != Uri.UriSchemeHttps)
        {
            return $"{uri.Scheme}://{Environment.MachineName}:{uri.Port}";
        }

        var self = new IPEndPoint(uri.Host == "[::]" ? IPAddress.IPv6Loopback : IPAddress.Loopback, uri.Port);
        var name = await HttpsSelfCheck.NameToShareAsync(self, logger, cancellationToken) ?? DevKitHostingOptions.FullMachineName();
        return $"{uri.Scheme}://{name}:{uri.Port}";
    }

    protected override void AddServices(IServiceCollection services)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("Hosted mode needs Windows: people sign in with their Windows accounts.");
        }

        // Before anything reads the keys or the sign-outs kept there.
        DevKitMachineConfig.EnsureClosed();
        services.AddSingleton(OwnerAccount.Current());
        services.AddSingleton(TimeProvider.System);
        // Sign-outs are kept beside devkit.json, so a restart or a package switch never brings a signed-out session back.
        services.AddSingleton(sp => new SessionRegistry(sp.GetRequiredService<TimeProvider>(),
            Path.Combine(DevKitMachineConfig.Folder, SignedOutFileName), sp.GetRequiredService<ILogger<SessionRegistry>>()));
        services.AddSingleton<SignInThrottle>();
        // Replaces appsettings.json's localhost-only AllowedHosts: shared DevKit answers to this machine's names.
        services.Configure<HostFilteringOptions>(hosts => hosts.AllowedHosts = Options.HostNames.ToList());
        // Other people's browsers never get a circuit's exception details, even when DevKit runs in Development.
        services.PostConfigure<CircuitOptions>(circuit => circuit.DetailedErrors = false);
        services.AddSingleton(sp => new SignInAudit(Options.AuditFolder, sp.GetRequiredService<ILogger<SignInAudit>>()));
        services.AddSingleton<IWindowsCredentialChecker, WindowsCredentialChecker>();
        services.AddSingleton(sp => new WindowsSignIn(sp.GetRequiredService<IWindowsCredentialChecker>(),
            sp.GetRequiredService<SignInThrottle>(), sp.GetRequiredService<OwnerAccount>(), Options.HostNames, Options.AllowedGroupIds));
        services.AddSingleton<SignInFlow>();

        services.AddSingleton(NewClientVault);
        services.AddSingleton(NewPersonVault);
        services.AddSingleton<PersonSettingsSaves>();
        services.AddScoped<PersonSettingsStore>();
        services.AddScoped(NewSharedSettings);
        // The files an even earlier DevKit kept on this server, moved into the settings folder on each person's next visit.
        services.AddSingleton(sp => new ServerSettingsFolder(
            Path.Combine(DevKitMachineConfig.Folder, ServerSettingsFolder.FolderName), sp.GetRequiredService<IDataProtectionProvider>()));
        services.AddScoped<EarlierSettingsCopies>();
        services.AddScoped<UserSession, SharedUserSession>();
        services.AddScoped<AuthenticationStateProvider, SessionAuthenticationStateProvider>();
        services.AddSingleton<PersonCircuits>();
        services.AddScoped<CircuitHandler, CircuitsPerPerson>();

        // The keys that encrypt sessions, and that open the settings files an earlier DevKit kept on this server until
        // each has moved into the settings folder. They stay on this server, encrypted for the account DevKit runs as.
        // Losing them signs everyone out, and leaves any file not moved yet unreadable.
        services.AddDataProtection()
            .SetApplicationName("DevKit")
            .PersistKeysToFileSystem(new DirectoryInfo(Path.Combine(DevKitMachineConfig.Folder, "keys")))
            .ProtectKeysWithDpapi();

        // Windows single sign-on is DevKit's own NTLM handshake (see WindowsSignIn); the session is this cookie.
        services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
            .AddCookie(ConfigureSessionCookie);
        services.AddAuthorization(authorization => authorization.FallbackPolicy =
            new AuthorizationPolicyBuilder(CookieAuthenticationDefaults.AuthenticationScheme).RequireAuthenticatedUser().Build());
        services.AddRateLimiter(limits =>
        {
            limits.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            limits.AddPolicy(PasswordSignInRateLimitPolicy, PerAddress(PasswordAttemptsPerMinute));
            limits.AddPolicy(WindowsSignInRateLimitPolicy, PerAddress(WindowsRequestsPerMinute));
        });
    }

    /// <summary>
    /// Each person's settings stay in their own browser, sealed with a key of their own that the domain locks to the
    /// account DevKit runs as, so any DevKit server running as that account opens them with nothing copied to it.
    /// </summary>
    [SupportedOSPlatform("windows")]
    private static ClientVault NewClientVault(IServiceProvider services) => new(
        DomainKeyLock.For(services.GetRequiredService<OwnerAccount>(), services.GetRequiredService<ILogger<DomainKeyLock>>()),
        DevKitHostingOptions.MachineDnsDomain());

    /// <summary>
    /// Every person's settings, sealed, in one folder beside devkit.json that the account DevKit runs as alone can open.
    /// It is closed to everyone else here, before anyone signs in; DevKit does not start when that cannot be done.
    /// </summary>
    [SupportedOSPlatform("windows")]
    private static PersonVault NewPersonVault(IServiceProvider services)
    {
        var vault = new PersonVault(
            Path.Combine(DevKitMachineConfig.Folder, PersonVault.FolderName),
            new SecurityIdentifier(services.GetRequiredService<OwnerAccount>().Id),
            services.GetRequiredService<ILogger<PersonVault>>());
        vault.Secure();
        WarnWhenRunFromADesktop(services.GetRequiredService<ILogger<PersonVault>>());
        return vault;
    }

    /// <summary>
    /// Whoever can run a program as the account DevKit runs as can open every person's settings. When that account is
    /// also someone's own, signed in to a desktop on this server, every program they start can.
    /// </summary>
    [SupportedOSPlatform("windows")]
    private static void WarnWhenRunFromADesktop(ILogger logger)
    {
        using var account = WindowsIdentity.GetCurrent();
        if (account.Groups?.Contains(new SecurityIdentifier(WellKnownSidType.InteractiveSid, null)) == true)
        {
            logger.LogWarning(
                "Shared DevKit runs from a desktop session, so every program the same account runs there can open everyone's settings. Run it as an account of its own that nobody signs in to, such as a group managed service account");
        }
    }

    /// <summary>The signed-in person's settings, kept in <see cref="PersonVault"/>, against the one TFS server.</summary>
    [SupportedOSPlatform("windows")]
    private SettingsService NewSharedSettings(IServiceProvider services) => new(
        services.GetRequiredService<ILogger<SettingsService>>(), services.GetRequiredService<PersonSettingsStore>(), Options.TfsUrl);

    /// <summary>
    /// At most <paramref name="perMinute"/> requests a minute from one address of another machine. Browsers on this
    /// server itself all come from loopback, whoever's desktop they run in, so a limit there would stop everyone on the
    /// server at once; they are not limited by address. Wrong passwords stay limited per account (<see cref="SignInThrottle"/>).
    /// </summary>
    internal static Func<HttpContext, RateLimitPartition<string>> PerAddress(int perMinute) =>
        context => LocalAccessGate.IsFromThisMachine(context)
            ? RateLimitPartition.GetNoLimiter(ThisMachinePartition)
            : RateLimitPartition.GetFixedWindowLimiter(
                LocalAccessGate.ClientAddress(context)?.ToString() ?? "unknown",
                _ => new FixedWindowRateLimiterOptions { PermitLimit = perMinute, Window = TimeSpan.FromMinutes(1) });

    private void ConfigureSessionCookie(CookieAuthenticationOptions cookie)
    {
        cookie.Cookie.Name = Options.HttpsOnly ? HttpsSessionCookieName : SessionCookieName;
        cookie.Cookie.HttpOnly = true;
        cookie.Cookie.SameSite = SameSiteMode.Lax;
        cookie.Cookie.SecurePolicy = Options.HttpsOnly ? CookieSecurePolicy.Always : CookieSecurePolicy.SameAsRequest;
        cookie.ExpireTimeSpan = Options.SessionLength;
        cookie.SlidingExpiration = false;
        // A page that needs a sign-in tries Windows sign-in first, so a browser on the domain never sees the sign-in page.
        // Sign out still goes to the page itself, where someone else can sign in.
        cookie.LoginPath = SignInFlow.AutomaticPath;
        cookie.ReturnUrlParameter = SignInEndpoints.ReturnUrlParameter;
        cookie.Events.OnRedirectToLogin = context =>
        {
            // A page goes to the sign-in page; the WebSocket and anything else just learns it is not signed in.
            if (HttpMethods.IsGet(context.Request.Method) && !context.Request.Path.StartsWithSegments(BlazorHubPath))
            {
                context.Response.Redirect(context.RedirectUri);
            }
            else
            {
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            }

            return Task.CompletedTask;
        };
        cookie.Events.OnValidatePrincipal = context =>
            context.HttpContext.RequestServices.GetRequiredService<SignInFlow>().ValidateSessionAsync(context);
    }
}
