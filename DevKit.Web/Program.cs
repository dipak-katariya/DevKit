using DevKit.Web.Services;

var builder = WebApplication.CreateBuilder(args);

// scripts\devkit-host.bat runs the Release build from its build output in Production. Outside Development, ASP.NET Core
// only serves the framework's blazor.web.js and the scoped CSS from a published folder, so without this the page shows
// but nothing on it responds. A published folder has no build manifest, and there this does nothing.
builder.WebHost.UseStaticWebAssets();

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

// Services. Who gets in and where settings live depend on this machine's devkit.json: local mode (the starter only,
// with the printed link) or hosted mode (shared on a server, with Windows sign-in).
var access = DevKitAccess.AddTo(builder);
builder.Services.AddScoped<PageStateService>();
builder.Services.AddScoped<BranchCacheService>();
builder.Services.AddScoped<BranchCleanupService>();
builder.Services.AddTfsServices();

await using var app = builder.Build();

// An unhandled error gets a plain page; the exception itself goes to the log. Shared DevKit uses this even in
// Development, so nobody on the network ever sees a stack trace.
if (!app.Environment.IsDevelopment() || access.AlwaysHidesErrorDetails)
{
    app.UseExceptionHandler(errors => errors.Run(context =>
    {
        context.Response.ContentType = "text/plain; charset=utf-8";
        return context.Response.WriteAsync("DevKit hit an unexpected error. It has been logged; please try again.");
    }));
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
        headers["Referrer-Policy"] = access.ReferrerPolicy;
        headers["Content-Security-Policy"] = contentSecurityPolicy;
        // No other site's window can reach DevKit's, no other site may embed its files, and pages get no camera,
        // microphone or location.
        headers["Cross-Origin-Opener-Policy"] = "same-origin";
        headers["Cross-Origin-Resource-Policy"] = "same-origin";
        headers["Permissions-Policy"] = "camera=(), microphone=(), geolocation=()";
        return Task.CompletedTask;
    });
    await next();
});

// Before everything else that answers a request, static files included.
access.UseGate(app);

app.UseStaticFiles();
access.UseSignIn(app);
app.UseAntiforgery();

// Liveness probe for the client-side reconnect overlay. It lets the browser tell a circuit
// that is merely resuming (server still answering) from the app actually being gone, so the
// "Reconnecting" overlay is only raised in the second case.
app.MapGet("/healthz", () => Results.NoContent()).AllowAnonymous();

// In hosted mode every page needs a sign-in: the fallback authorization policy covers these and the WebSocket.
app.MapRazorComponents<DevKit.Web.Components.App>()
    .AddInteractiveServerRenderMode();

// What RunAsync does, with the announcement once DevKit listens: shared mode's one test connection to itself needs the
// server running.
await app.StartAsync();
await access.AnnounceAsync(app);
await app.WaitForShutdownAsync();
