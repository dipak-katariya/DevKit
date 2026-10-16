using DevKit.Web.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DevKit.Tests;

/// <summary>
/// Exporting settings to a file and importing them again: every preference travels, the TFS connection never does,
/// and a file that is not DevKit settings changes nothing.
/// </summary>
public sealed class SettingsTransferTests
{
    private const string FixedUrl = "https://tfs.example.test/tfs/Collection";
    private const string TestPat = "transfer-test-pat-5b1e";

    private static SettingsService NewSettings(ILogger<SettingsService>? logger = null)
    {
        var settings = new SettingsService(logger ?? NullLogger<SettingsService>.Instance, new MemorySettingsStore(), FixedUrl);
        settings.LoadFromStore(allowsWindowsSignIn: false);
        return settings;
    }

    private static SettingsService Configured()
    {
        var settings = NewSettings();
        settings.SaveTfs(FixedUrl, TestPat);
        settings.TeamName = "TEAM-A";
        settings.DefaultAreaPath = "Area|||Project\\Area|||Project";
        settings.DefaultSprint = "Project\\PI-5_1";
        settings.DefaultSprintIsCurrent = false;
        settings.DefaultRepoId = "repo-1";
        settings.MostUsedRepoCount = 3;
        settings.SetMostUsedRepoIds(new[] { "repo-1", "repo-2" });
        settings.SetRepoQaBranch("repo-1", "develop");
        settings.SetRepoMergingBranch("repo-1", "release/cp-26.5.2");
        return settings;
    }

    [Fact]
    public void An_export_holds_no_TFS_connection_or_token()
    {
        var file = Configured().ExportSettings();

        Assert.DoesNotContain(TestPat, file, StringComparison.Ordinal);
        Assert.DoesNotContain("tfs.example.test", file, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("\"Tfs\"", file, StringComparison.Ordinal);
        Assert.DoesNotContain("\"Pat\"", file, StringComparison.Ordinal);
        Assert.Contains(PortableSettings.FormatName, file, StringComparison.Ordinal);
    }

    [Fact]
    public void Every_preference_comes_back_on_import()
    {
        var file = Configured().ExportSettings();
        var other = NewSettings();

        var result = other.ImportSettings(file);

        Assert.True(result.Imported, result.Message);
        Assert.Equal("TEAM-A", other.TeamName);
        Assert.Equal("Project\\PI-5_1", other.DefaultSprint);
        Assert.False(other.DefaultSprintIsCurrent);
        Assert.Equal("repo-1", other.DefaultRepoId);
        Assert.Equal(3, other.MostUsedRepoCount);
        Assert.Equal(new[] { "repo-1", "repo-2" }, other.MostUsedRepoIds);
        Assert.Equal("develop", other.GetRepoBranches("repo-1").QaBranch);
        Assert.Equal("release/cp-26.5.2", other.GetRepoBranches("repo-1").MergingBranch);
    }

    [Fact]
    public void An_import_never_changes_the_connection_even_from_a_file_that_names_one()
    {
        var settings = NewSettings();
        settings.SaveTfs(FixedUrl, TestPat);
        var file = NewSettings().ExportSettings()
            .Replace("\"TeamName\"", "\"Tfs\": { \"Url\": \"https://evil.example.test/tfs\", \"Pat\": \"stolen\" },\n  \"TeamName\"", StringComparison.Ordinal);

        Assert.True(settings.ImportSettings(file).Imported);

        Assert.Equal(FixedUrl, settings.Tfs.Url);
        Assert.Equal(TestPat, settings.Tfs.Pat);
    }

    [Theory]
    [InlineData("not json at all")]
    [InlineData("""{ "Format": "Something else", "Version": 1 }""")]
    [InlineData("""{ "Version": 1, "TeamName": "X" }""")]
    public void A_file_that_is_not_DevKit_settings_changes_nothing(string file)
    {
        var settings = Configured();

        var result = settings.ImportSettings(file);

        Assert.False(result.Imported);
        Assert.Equal("TEAM-A", settings.TeamName);
    }

    [Fact]
    public void A_file_from_a_newer_DevKit_is_refused()
    {
        var settings = Configured();

        var result = settings.ImportSettings($$"""{ "Format": "{{PortableSettings.FormatName}}", "Version": 99, "TeamName": "X" }""");

        Assert.False(result.Imported);
        Assert.Contains("newer DevKit", result.Message, StringComparison.Ordinal);
        Assert.Equal("TEAM-A", settings.TeamName);
    }

    [Fact]
    public void A_file_larger_than_any_settings_file_is_refused_unread()
    {
        var result = NewSettings().ImportSettings(new string(' ', SettingsService.MaxImportBytes + 1));

        Assert.False(result.Imported);
    }

    [Fact]
    public void Imported_lists_are_cleaned_and_capped_as_Settings_keeps_them()
    {
        var settings = NewSettings();
        var file = $$"""
            { "Format": "{{PortableSettings.FormatName}}", "Version": 1, "MostUsedRepoCount": 99,
              "MostUsedRepoIds": [ " a ", "a", "", "b" ],
              "RepoBranches": { "r1": { "QaBranch": "  main ", "MergingBranch": "" }, "r2": { "QaBranch": "", "MergingBranch": " " } } }
            """;

        Assert.True(settings.ImportSettings(file).Imported);

        Assert.Equal(SettingsService.MaxMostUsedRepoCount, settings.MostUsedRepoCount);
        Assert.Equal(new[] { "a", "b" }, settings.MostUsedRepoIds);
        Assert.Equal("main", settings.GetRepoBranches("r1").QaBranch);
        Assert.True(settings.GetRepoBranches("r2").IsEmpty);
    }

    [Fact]
    public void A_file_that_cannot_be_read_is_logged()
    {
        var logger = new RecordingLogger<SettingsService>();

        NewSettings(logger).ImportSettings("{ broken");

        Assert.Contains(logger.Entries, e => e.Level == LogLevel.Error && e.Exception is System.Text.Json.JsonException);
    }
}
