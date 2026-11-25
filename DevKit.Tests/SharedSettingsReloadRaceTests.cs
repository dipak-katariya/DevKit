using System.Runtime.Versioning;
using DevKit.Web.Services;
using Xunit;

namespace DevKit.Tests;

/// <summary>
/// A page reads the stored settings again, after another page saved, without holding its settings. Its own save made
/// while that read is open must still be kept: the vault replaces the file in place, which fails while it is open.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class SharedSettingsReloadRaceTests : SharedSettingsScratch
{
    private static readonly TimeSpan SaveGetsThrough = TimeSpan.FromMilliseconds(500);

    [Fact]
    public async Task A_save_made_while_the_page_reads_the_stored_settings_again_is_kept()
    {
        (await SignedInAsync(Alice)).TeamName = "TEAM-A";
        var page = OpenPage(Alice);
        Assert.True(await page.Session.StartAsync());
        Task<bool>? save = null;
        page.Store.ReadingStored = () =>
        {
            page.Store.ReadingStored = null;
            // The file held open as a read of it holds it, while this page saves from another thread.
            using var reading = new FileStream(Server.PathFor(Alice), FileMode.Open, FileAccess.Read, FileShare.Read);
            save = Task.Run(() =>
            {
                page.Settings.DashboardView = DashboardView.List;
                return page.Settings.LastSaveError is null;
            });
            save.Wait(SaveGetsThrough);
        };

        page.Settings.ReloadFromStore();

        Assert.NotNull(save);
        Assert.True(await save);
        var later = await SignedInAsync(Alice);
        Assert.Equal("TEAM-A", later.TeamName);
        Assert.Equal(DashboardView.List, later.DashboardView);
    }
}
