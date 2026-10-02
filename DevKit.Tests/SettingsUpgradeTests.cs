using DevKit.Web.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DevKit.Tests;

/// <summary>
/// A target-framework upgrade moves a source build from bin/Debug/net9.0 to bin/Debug/net10.0. These pin
/// that the settings — TFS URL, PAT, every tool's defaults — follow it rather than the app opening empty.
/// Each test works in its own temporary folder tree, never the real build output.
/// </summary>
public sealed class SettingsUpgradeTests : IDisposable
{
    private const string FileName = "usersettings.json";
    private readonly string _root = Path.Combine(Path.GetTempPath(), "devkit-settings-upgrade-" + Guid.NewGuid().ToString("N"));

    private string Output(string folder)
    {
        var dir = Path.Combine(_root, "bin", "Debug", folder);
        Directory.CreateDirectory(dir);
        return dir;
    }

    private string WriteSettings(string folder, string content)
    {
        var path = Path.Combine(Output(folder), FileName);
        File.WriteAllText(path, content);
        return path;
    }

    private string SettingsPathIn(string folder) => Path.Combine(Output(folder), FileName);

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Fact]
    public void Settings_from_the_previous_target_are_carried_over_to_the_new_one()
    {
        var old = WriteSettings("net9.0", """{"TeamName":"FXCPMGR"}""");
        var target = SettingsPathIn("net10.0");

        var adopted = SettingsService.AdoptFromPreviousTarget(target, NullLogger.Instance);

        Assert.Equal(Path.GetFullPath(old), adopted);
        Assert.Equal(File.ReadAllText(old), File.ReadAllText(target));
        Assert.True(File.Exists(old), "the old file stays where it was");
    }

    [Fact]
    public void Settings_already_on_the_new_target_are_never_overwritten()
    {
        WriteSettings("net9.0", """{"TeamName":"OLD"}""");
        var target = WriteSettings("net10.0", """{"TeamName":"CURRENT"}""");

        var adopted = SettingsService.AdoptFromPreviousTarget(target, NullLogger.Instance);

        Assert.Null(adopted);
        Assert.Equal("""{"TeamName":"CURRENT"}""", File.ReadAllText(target));
    }

    [Fact]
    public void The_newest_older_target_wins_when_there_are_several()
    {
        WriteSettings("net8.0", """{"TeamName":"EIGHT"}""");
        var nine = WriteSettings("net9.0", """{"TeamName":"NINE"}""");
        var target = SettingsPathIn("net10.0");

        var adopted = SettingsService.AdoptFromPreviousTarget(target, NullLogger.Instance);

        Assert.Equal(Path.GetFullPath(nine), adopted);
        Assert.Contains("NINE", File.ReadAllText(target));
    }

    [Fact]
    public void A_newer_target_is_never_taken_from_so_a_downgrade_does_not_import_its_settings()
    {
        WriteSettings("net11.0", """{"TeamName":"ELEVEN"}""");
        var target = SettingsPathIn("net10.0");

        Assert.Null(SettingsService.AdoptFromPreviousTarget(target, NullLogger.Instance));
        Assert.False(File.Exists(target));
    }

    [Fact]
    public void A_published_build_outside_a_target_framework_folder_is_left_alone()
    {
        WriteSettings("net9.0", """{"TeamName":"NINE"}""");
        var publish = Path.Combine(_root, "bin", "Debug", "publish");
        Directory.CreateDirectory(publish);
        var target = Path.Combine(publish, FileName);

        Assert.Null(SettingsService.AdoptFromPreviousTarget(target, NullLogger.Instance));
        Assert.False(File.Exists(target));
    }

    [Fact]
    public void Folders_that_only_look_like_targets_are_ignored()
    {
        WriteSettings("netcoreapp3.1", """{"TeamName":"CORE"}""");
        WriteSettings("net48", """{"TeamName":"FRAMEWORK"}""");
        var target = SettingsPathIn("net10.0");

        Assert.Null(SettingsService.AdoptFromPreviousTarget(target, NullLogger.Instance));
        Assert.False(File.Exists(target));
    }

    [Fact]
    public void Nothing_to_carry_over_is_simply_nothing()
    {
        var target = SettingsPathIn("net10.0");

        Assert.Null(SettingsService.AdoptFromPreviousTarget(target, NullLogger.Instance));
    }
}
