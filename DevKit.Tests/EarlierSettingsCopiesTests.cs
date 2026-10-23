using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text.Json;
using DevKit.Web.Models;
using DevKit.Web.Services;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace DevKit.Tests;

/// <summary>
/// A shared-mode page opens once it knows who is signed in and has their settings from their own browser. A copy an
/// earlier DevKit kept on the server, or before that in the browser's local storage, moves into the browser once and is
/// then deleted, so the server keeps nothing; a copy that cannot move yet stays for the next visit.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class EarlierSettingsCopiesTests : SharedSettingsScratch
{
    [Fact]
    public async Task The_page_opens_only_once_it_knows_who_is_signed_in_and_has_their_settings()
    {
        (await SignedInAsync(Alice)).SaveTfs(FixedUrl, AlicePat);
        var page = OpenPage(Alice);

        Assert.False(page.Session.IsReady);
        Assert.True(await page.Session.StartAsync());

        Assert.True(page.Session.IsReady);
        Assert.Equal(Alice.Id, page.Session.User!.Id);
        Assert.Equal(AlicePat, page.Settings.Tfs.Pat);
        Assert.False(page.Settings.AllowsWindowsSignIn);
        Assert.Equal($"this browser, on this PC only, for {Alice.Name}", page.Settings.SettingsFilePath);
    }

    [Fact]
    public async Task A_page_whose_session_has_ended_is_not_opened()
    {
        var page = OpenPage(null);

        Assert.False(await page.Session.StartAsync());
        Assert.False(page.Session.IsReady);
        Assert.Equal(0, Browser.VaultSaves);
    }

    [Fact]
    public async Task Settings_kept_on_the_server_move_into_the_browser_once_and_leave_the_server()
    {
        PutServerCopy(Alice, "TEAM-A");
        var file = Folder.PathFor(Alice);
        await File.WriteAllTextAsync(file + ".bak", "an earlier save");
        await File.WriteAllTextAsync(file + ".corrupt-20261020-101010", "a damaged copy");

        var alice = await SignedInAsync(Alice);

        Assert.Equal(AlicePat, alice.Tfs.Pat);
        Assert.Equal("TEAM-A", alice.TeamName);
        Assert.Empty(Directory.GetFiles(UsersPath));
        // Sealed with the settings, not with this machine's DPAPI, so the next server reads it too.
        Assert.Equal(AlicePat, JsonSerializer.Deserialize<UserSettings>(Vault.Open(Alice, Browser.Vault!).Text)!.Tfs.Pat);
        var onTheNewServer = await SignedInAsync(Alice, vault: OtherServer());
        Assert.Equal(AlicePat, onTheNewServer.Tfs.Pat);
        Assert.Equal("TEAM-A", onTheNewServer.TeamName);
    }

    [Fact]
    public async Task Settings_kept_in_local_storage_move_into_the_browsers_vault_once()
    {
        await PutLocalStorageCopyAsync(Alice, "TEAM-A");

        var alice = await SignedInAsync(Alice);

        Assert.Equal(AlicePat, alice.Tfs.Pat);
        Assert.Equal("TEAM-A", alice.TeamName);
        Assert.Empty(Browser.Items);
        Assert.Equal(AlicePat, (await SignedInAsync(Alice)).Tfs.Pat);
    }

    [Fact]
    public async Task The_servers_copy_wins_over_an_older_local_storage_copy_and_both_go()
    {
        PutServerCopy(Alice, "TEAM-SERVER");
        await PutLocalStorageCopyAsync(Alice, "TEAM-OLD");

        var alice = await SignedInAsync(Alice);

        Assert.Equal("TEAM-SERVER", alice.TeamName);
        Assert.Empty(Browser.Items);
        Assert.Empty(Directory.GetFiles(UsersPath));
    }

    [Fact]
    public async Task A_browser_that_keeps_settings_uses_them_and_drops_what_the_server_still_holds()
    {
        (await SignedInAsync(Alice)).TeamName = "TEAM-BROWSER";
        PutServerCopy(Alice, "TEAM-OLD");

        var alice = await SignedInAsync(Alice);

        Assert.Equal("TEAM-BROWSER", alice.TeamName);
        Assert.Empty(Directory.GetFiles(UsersPath));
    }

    [Fact]
    public async Task The_servers_copy_stays_while_the_browsers_settings_cannot_be_read_here()
    {
        (await SignedInAsync(Alice, vault: OtherServer(AnotherAccount))).TeamName = "TEAM-ELSEWHERE";
        PutServerCopy(Alice, "TEAM-SERVER");

        var alice = await SignedInAsync(Alice);

        Assert.NotNull(alice.LoadProblem);
        Assert.True(File.Exists(Folder.PathFor(Alice)));
    }

    [Fact]
    public async Task A_move_the_browser_does_not_keep_leaves_the_earlier_copies_for_the_next_visit()
    {
        PutServerCopy(Alice, "TEAM-A");
        await PutLocalStorageCopyAsync(Alice, "TEAM-OLD");
        Browser.RefusesVault = true;

        var refused = await SignedInAsync(Alice);

        Assert.NotNull(refused.LastSaveError);
        Assert.True(File.Exists(Folder.PathFor(Alice)));
        Assert.Single(Browser.Items);

        Browser.RefusesVault = false;
        var later = await SignedInAsync(Alice);
        Assert.Equal("TEAM-A", later.TeamName);
        Assert.Empty(Directory.GetFiles(UsersPath));
        Assert.Empty(Browser.Items);
    }

    [Fact]
    public async Task A_server_copy_this_servers_keys_no_longer_open_is_not_moved_and_is_logged()
    {
        PutServerCopy(Alice, "TEAM-A", new EphemeralDataProtectionProvider());
        var logger = new RecordingLogger<EarlierSettingsCopies>();

        var page = OpenPage(Alice, logger: logger);
        Assert.True(await page.Session.StartAsync());

        Assert.Equal("", page.Settings.Tfs.Pat);
        Assert.Contains(logger.Entries, e => e.Level == LogLevel.Error && e.Exception is CryptographicException);
        Assert.Null(Browser.Vault);
    }

    [Fact]
    public async Task A_local_storage_copy_that_no_longer_decrypts_is_deleted_and_logged()
    {
        await PutLocalStorageCopyAsync(Alice, "TEAM-A", new EphemeralDataProtectionProvider());
        var logger = new RecordingLogger<EarlierSettingsCopies>();

        var page = OpenPage(Alice, logger: logger);
        Assert.True(await page.Session.StartAsync());

        Assert.Contains(logger.Entries, e => e.Level == LogLevel.Error && e.Exception is CryptographicException);
        Assert.Empty(Browser.Items);
        Assert.Equal("", page.Settings.Tfs.Pat);
    }

    [Fact]
    public async Task A_link_in_place_of_the_servers_settings_folder_is_not_followed()
    {
        var elsewhere = Path.Combine(Root, "elsewhere");
        Directory.CreateDirectory(Root);
        MakeJunction(UsersPath, elsewhere);
        var outside = Path.Combine(elsewhere, Path.GetFileName(Folder.PathFor(Alice)));
        await File.WriteAllTextAsync(outside, "not DevKit's to read or delete");
        var logger = new RecordingLogger<EarlierSettingsCopies>();

        var page = OpenPage(Alice, logger: logger);
        Assert.True(await page.Session.StartAsync());

        Assert.Contains(logger.Entries, e => e.Level == LogLevel.Error && e.Exception is IOException);
        Assert.Equal("not DevKit's to read or delete", await File.ReadAllTextAsync(outside));
    }
}
