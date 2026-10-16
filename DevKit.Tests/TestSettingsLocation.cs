using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using DevKit.Web.Services;

namespace DevKit.Tests;

/// <summary>
/// Points every SettingsService the tests create at usersettings.json beside the test assembly. Without
/// it the default is the user's own per-user settings file, and a test run would overwrite real settings.
/// </summary>
internal static class TestSettingsLocation
{
    public static readonly string SettingsPath = Path.Combine(AppContext.BaseDirectory, SettingsFile.FileName);

    [ModuleInitializer]
    [SuppressMessage("Usage", "CA2255", Justification = "Must run before any test class constructs a SettingsService.")]
    internal static void UseTheTestFolder() =>
        Environment.SetEnvironmentVariable(SettingsFile.PathVariable, SettingsPath);
}
