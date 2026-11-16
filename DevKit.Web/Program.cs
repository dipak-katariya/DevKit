using DevKit.Web.Services;
using Microsoft.Extensions.DependencyInjection.Extensions;

// "Run Latest DevKit.bat" starts the same program to switch packages, with the page that answers meanwhile.
if (MaintenanceMode.IsRequested(args))
{
    Environment.ExitCode = await MaintenanceMode.RunAsync(args);
    return;
}

// devkit-deploy.bat has a new package's own program list and hash its files, last.
if (PackageManifest.IsRequested(args))
{
    Environment.ExitCode = PackageManifest.Run(args);
    return;
}

var builder = WebApplication.CreateBuilder(args);
DevKitAccess.HideServerName(builder.WebHost);

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
// enough for the browser to rejoin it with its state intact. Fewer are kept than before, and in shared mode each
// person has at most PersonCircuits.MaxPerPerson, so kept circuits cannot fill the server's memory.
//
// This limit is on what a browser sends, not on render batches, which go the other way. The largest is the settings
// copy an earlier DevKit kept in the browser's local storage, read once as it moves to the server; a settings import
// streams in pieces well below it.
const int maxSignalRMessageBytes = 256 * 1024;

builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents(options =>
    {
        options.DisconnectedCircuitRetentionPeriod = TimeSpan.FromMinutes(30);
        options.DisconnectedCircuitMaxRetained = 100;
        options.JSInteropDefaultCallTimeout = TimeSpan.FromMinutes(5);
    })
    .AddHubOptions(options =>
    {
        options.KeepAliveInterval = TimeSpan.FromSeconds(10);
        options.ClientTimeoutInterval = TimeSpan.FromMinutes(3);
        options.HandshakeTimeout = TimeSpan.FromSeconds(30);
        options.MaximumReceiveMessageSize = maxSignalRMessageBytes;
    });

// Services. Who gets in and where settings live depend on this machine's devkit.json: local mode (the starter only,
// with the printed link) or hosted mode (shared on a server, with Windows sign-in).
var access = DevKitAccess.AddTo(builder);
builder.Services.AddScoped<PageStateService>();
builder.Services.AddScoped<BranchCacheService>();
builder.Services.AddScoped<BranchCleanupService>();
builder.Services.AddTfsServices();
// Hosted mode already registers the clock for sign-in; the Workday Calculator reads it in either mode.
builder.Services.TryAddSingleton(TimeProvider.System);
// During an update the maintenance page holds DevKit's port until the DevKit its switch started is about to listen.
var handoverPort = PortHandover.ConfiguredPort();
var handoverRun = PortHandover.ConfiguredRun();
if (handoverPort is { } port)
{
    PortHandover.AddTakeOver(builder.Services, port, handoverRun);
}

await using var app = builder.Build();

// And it makes way in turn, finishing the requests it is serving, when the next switch asks.
if (handoverPort is { } heldPort && handoverRun is { } run)
{
    PortHandover.MakeWayWhenAsked(app, heldPort, run);
}

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

DevKitAccess.UseSecurityHeaders(app, access.ReferrerPolicy);

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
