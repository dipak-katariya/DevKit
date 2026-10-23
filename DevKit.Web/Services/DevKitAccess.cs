using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.Versioning;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components.Server;
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
public sealed class DevKitHostingOptions
{
    public const string SectionName = "DevKit";
    public const int MaxSessionDays = 30;

    public DevKitMode Mode { get; set; } = DevKitMode.Local;

    /// <summary>The one TFS server everyone works against in hosted mode; people cannot point DevKit elsewhere.</summary>
    public string TfsUrl { get; set; } = "";

    /// <summary>The domain a username typed without one belongs to; this machine's domain when left out.</summary>
    public string Domain { get; set; } = "";

    /// <summary>How long a sign-in lasts. It does not slide: after this many days everyone signs in again.</summary>
    public int SessionDays { get; set; } = 5;

    /// <summary>
    /// The names browsers may use to reach DevKit; a request for any other name is refused, which stops DNS-rebinding
    /// pages. This machine's own names and addresses when left out.
    /// </summary>
    public List<string> HostNames { get; set; } = new();

    /// <summary>Where the sign-in audit log is written; "audit" in <see cref="DevKitMachineConfig.Folder"/> when left out.</summary>
    public string AuditFolder { get; set; } = "";

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

        options.TfsUrl = url.TrimEnd('/');
        options.Domain = string.IsNullOrWhiteSpace(options.Domain) ? Environment.UserDomainName : options.Domain.Trim();
        options.HostNames = options.HostNames.Count > 0
            ? options.HostNames.Select(name => name.Trim()).Where(name => name.Length > 0).ToList()
            : ThisMachinesNames();
        options.AuditFolder = string.IsNullOrWhiteSpace(options.AuditFolder)
            ? Path.Combine(DevKitMachineConfig.Folder, "audit")
            : Path.GetFullPath(options.AuditFolder);
        return options;
    }

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
    private const string BlazorHubPath = "/_blazor";
    private const int PasswordAttemptsPerMinute = 10;

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
        // Made now, so the log says at start whether the domain locks keys for the account DevKit runs as.
        app.Services.GetRequiredService<ClientVault>();
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

        services.AddSingleton(OwnerAccount.Current());
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<SessionRegistry>();
        services.AddSingleton<SignInThrottle>();
        // Replaces appsettings.json's localhost-only AllowedHosts: shared DevKit answers to this machine's names.
        services.Configure<HostFilteringOptions>(hosts => hosts.AllowedHosts = Options.HostNames.ToList());
        // Other people's browsers never get a circuit's exception details, even when DevKit runs in Development.
        services.PostConfigure<CircuitOptions>(circuit => circuit.DetailedErrors = false);
        services.AddSingleton(sp => new SignInAudit(Options.AuditFolder, sp.GetRequiredService<ILogger<SignInAudit>>()));
        services.AddSingleton<IWindowsCredentialChecker, WindowsCredentialChecker>();
        services.AddSingleton<WindowsSignIn>();
        services.AddSingleton<SignInFlow>();

        services.AddSingleton(NewClientVault);
        services.AddScoped<ClientSettingsStore>();
        services.AddScoped(sp =>
        {
            var store = sp.GetRequiredService<ClientSettingsStore>();
            var settings = new SettingsService(sp.GetRequiredService<ILogger<SettingsService>>(), store, Options.TfsUrl);
            store.WriteFailed += settings.ReportSaveFailed;
            return settings;
        });
        // The files an earlier DevKit kept on this server, moved into each person's browser on their next visit.
        services.AddSingleton(sp => new ServerSettingsFolder(
            Path.Combine(DevKitMachineConfig.Folder, ServerSettingsFolder.FolderName), sp.GetRequiredService<IDataProtectionProvider>()));
        services.AddScoped<EarlierSettingsCopies>();
        services.AddScoped<UserSession, SharedUserSession>();
        services.AddScoped<AuthenticationStateProvider, SessionAuthenticationStateProvider>();

        // The keys that encrypt sessions, and that open the settings files an earlier DevKit kept on this server until
        // each has moved into its person's browser. They stay on this server, encrypted for the account DevKit runs as;
        // losing them signs everyone out, and leaves any file not moved yet unreadable.
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

    /// <summary>At most <paramref name="perMinute"/> requests a minute from one address.</summary>
    private static Func<HttpContext, RateLimitPartition<string>> PerAddress(int perMinute) =>
        context => RateLimitPartition.GetFixedWindowLimiter(
            LocalAccessGate.ClientAddress(context)?.ToString() ?? "unknown",
            _ => new FixedWindowRateLimiterOptions { PermitLimit = perMinute, Window = TimeSpan.FromMinutes(1) });

    private void ConfigureSessionCookie(CookieAuthenticationOptions cookie)
    {
        cookie.Cookie.Name = SessionCookieName;
        cookie.Cookie.HttpOnly = true;
        cookie.Cookie.SameSite = SameSiteMode.Lax;
        cookie.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
        cookie.ExpireTimeSpan = Options.SessionLength;
        cookie.SlidingExpiration = false;
        cookie.LoginPath = SignInEndpoints.SignInPath;
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
