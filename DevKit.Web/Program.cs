using DevKit.Web.Services;

const int browserLaunchDelayMs = 1500;

var builder = WebApplication.CreateBuilder(args);

// Blazor Server
builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

// Services
builder.Services.AddSingleton<SettingsService>();
builder.Services.AddSingleton<ThemeService>();
builder.Services.AddScoped<GitCommandService>();
builder.Services.AddScoped<PageStateService>();
builder.Services.AddHttpClient<TfsApiService>();

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
