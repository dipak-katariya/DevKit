using DevKit.Web.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DevKit.Tests;

/// <summary>
/// The pinned list and its cap have to agree at all times — a list longer than the cap leaks
/// extra repositories into every picker in the app. These run sequentially within the class,
/// which matters because SettingsService persists to a single file beside the test assembly.
/// </summary>
[Collection(SettingsFileCollection.Name)]
public class MostUsedRepoSettingsTests : IDisposable
{
    private static readonly string SettingsPath =
        Path.Combine(AppContext.BaseDirectory, "usersettings.json");

    public MostUsedRepoSettingsTests() => Reset();

    public void Dispose() => Reset();

    private static void Reset()
    {
        if (File.Exists(SettingsPath)) File.Delete(SettingsPath);
    }

    private static SettingsService NewService() => new(NullLogger<SettingsService>.Instance);

    [Fact]
    public void The_feature_is_off_until_a_count_is_chosen()
    {
        var settings = NewService();

        Assert.Equal(0, settings.MostUsedRepoCount);
        Assert.Empty(settings.MostUsedRepoIds);
    }

    [Fact]
    public void A_count_of_zero_keeps_nothing_at_all()
    {
        // Regression: the cap was checked after appending, so "off" still kept one repository.
        var settings = NewService();
        settings.MostUsedRepoCount = 0;

        settings.SetMostUsedRepoIds(new[] { "r1", "r2" });

        Assert.Empty(settings.MostUsedRepoIds);
    }

    [Fact]
    public void Only_the_first_n_are_kept_when_more_are_offered()
    {
        var settings = NewService();
        settings.MostUsedRepoCount = 3;

        settings.SetMostUsedRepoIds(new[] { "r1", "r2", "r3", "r4", "r5" });

        Assert.Equal(new[] { "r1", "r2", "r3" }, settings.MostUsedRepoIds);
    }

    [Fact]
    public void Lowering_the_count_trims_the_pins_already_stored()
    {
        var settings = NewService();
        settings.MostUsedRepoCount = 5;
        settings.SetMostUsedRepoIds(new[] { "r1", "r2", "r3", "r4", "r5" });

        settings.MostUsedRepoCount = 2;

        Assert.Equal(new[] { "r1", "r2" }, settings.MostUsedRepoIds);
    }

    [Fact]
    public void Blanks_and_duplicates_never_consume_a_slot()
    {
        var settings = NewService();
        settings.MostUsedRepoCount = 3;

        settings.SetMostUsedRepoIds(new[] { "r1", "  ", "r1", "", " r2 ", "r3" });

        Assert.Equal(new[] { "r1", "r2", "r3" }, settings.MostUsedRepoIds);
    }

    [Fact]
    public void The_count_is_clamped_to_the_supported_range()
    {
        var settings = NewService();

        settings.MostUsedRepoCount = 9999;
        Assert.Equal(SettingsService.MaxMostUsedRepoCount, settings.MostUsedRepoCount);

        settings.MostUsedRepoCount = -5;
        Assert.Equal(0, settings.MostUsedRepoCount);
    }

    [Fact]
    public void Both_the_count_and_the_pins_survive_a_reload()
    {
        // BuildPersistableCopy is an explicit allow-list; a field missed there is silently lost.
        var first = NewService();
        first.MostUsedRepoCount = 3;
        first.SetMostUsedRepoIds(new[] { "r1", "r2" });

        var reloaded = NewService();

        Assert.Equal(3, reloaded.MostUsedRepoCount);
        Assert.Equal(new[] { "r1", "r2" }, reloaded.MostUsedRepoIds);
    }
}
