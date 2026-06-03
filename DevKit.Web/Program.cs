using DevKit.Web.Services;

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

app.UseStaticFiles();
app.UseAntiforgery();

app.MapRazorComponents<DevKit.Web.Components.App>()
    .AddInteractiveServerRenderMode();

// Auto-open browser
var url = app.Urls.FirstOrDefault() ?? "http://localhost:5850";
_ = Task.Run(async () =>
{
    await Task.Delay(1500);
    try
    {
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
        {
            FileName = url,
            UseShellExecute = true
        });
    }
    catch { }
});

app.Run();
