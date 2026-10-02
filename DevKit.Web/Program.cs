using DevKit.Web.Services;

const int browserLaunchDelayMs = 1500;

var builder = WebApplication.CreateBuilder(args);

// Blazor Server.
//
// The defaults drop a circuit that goes quiet for ~30s and keep a disconnected one for only
// 3 minutes, which is what made a tab left open on a long-running sheet die and need a manual
// refresh. Keepalives are frequent enough to survive an idle proxy, the client timeout is
// generous enough to ride out a long TFS call, and a disconnected circuit is retained long
// enough for the browser to rejoin it with its state intact.
const int maxSignalRMessageBytes = 5 * 1024 * 1024;

builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents(options =>
    {
        options.DisconnectedCircuitRetentionPeriod = TimeSpan.FromMinutes(30);
        options.DisconnectedCircuitMaxRetained = 200;
        options.JSInteropDefaultCallTimeout = TimeSpan.FromMinutes(5);
    })
    .AddHubOptions(options =>
    {
        options.KeepAliveInterval = TimeSpan.FromSeconds(10);
        options.ClientTimeoutInterval = TimeSpan.FromMinutes(3);
        options.HandshakeTimeout = TimeSpan.FromSeconds(30);
        // A fully loaded merging sheet is a large render batch.
        options.MaximumReceiveMessageSize = maxSignalRMessageBytes;
    });

// Services
builder.Services.AddSingleton<SettingsService>();
builder.Services.AddSingleton<ThemeService>();
builder.Services.AddScoped<GitCommandService>();
builder.Services.AddScoped<PageStateService>();
builder.Services.AddScoped<BranchCacheService>();
builder.Services.AddScoped<BranchCleanupService>();
builder.Services.AddHttpClient<TfsApiService>();
builder.Services.AddHttpClient<CodeMergingService>();
builder.Services.AddHttpClient<CodeCompareService>();
builder.Services.AddHttpClient<MergeVerificationService>();

var app = builder.Build();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error");
}

// Security response headers. The CSP allows inline styles (the UI uses many style="…"
// attributes) but no inline/eval scripts, which neutralizes injected-markup script vectors.
// Set via OnStarting so these are authoritative over framework-emitted defaults (the Blazor
// endpoint otherwise adds its own frame-ancestors directive, producing a duplicate header).
const string contentSecurityPolicy =
    "default-src 'self'; " +
    "script-src 'self'; " +
    "style-src 'self' 'unsafe-inline'; " +
    "img-src 'self' data: http: https:; " +
    "font-src 'self'; " +
    "connect-src 'self' ws: wss:; " +
    "frame-ancestors 'none'; " +
    "base-uri 'self'; " +
    "form-action 'self'";

app.Use(async (context, next) =>
{
    context.Response.OnStarting(() =>
    {
        var headers = context.Response.Headers;
        headers["X-Content-Type-Options"] = "nosniff";
        headers["X-Frame-Options"] = "DENY";
        headers["Referrer-Policy"] = "no-referrer";
        headers["Content-Security-Policy"] = contentSecurityPolicy;
        return Task.CompletedTask;
    });
    await next();
});

app.UseStaticFiles();
app.UseAntiforgery();

// Liveness probe for the client-side reconnect overlay. It lets the browser tell a circuit
// that is merely resuming (server still answering) from the app actually being gone, so the
// "Reconnecting" overlay is only raised in the second case.
app.MapGet("/healthz", () => Results.NoContent());

app.MapRazorComponents<DevKit.Web.Components.App>()
    .AddInteractiveServerRenderMode();

// Auto-open browser — only for a local loopback URL, never an externally bound address.
var url = app.Urls.FirstOrDefault() ?? "http://localhost:5850";
if (IsLocalLaunchUrl(url))
{
    _ = Task.Run(async () =>
    {
        await Task.Delay(browserLaunchDelayMs);
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = url,
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            app.Logger.LogDebug(ex, "Could not auto-launch the browser at {Url}", url);
        }
    });
}

app.Run();

static bool IsLocalLaunchUrl(string url)
{
    if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return false;
    if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) return false;
    return uri.IsLoopback;
}
