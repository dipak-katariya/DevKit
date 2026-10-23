using System.Net;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DevKit.Web.Models;
using DevKit.Web.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DevKit.Tests;

/// <summary>
/// Shared mode keeps each person's settings and PAT only in their own browser, sealed for their Windows account under
/// a key of their own. They come back on every later page and from every DevKit server running as the same account,
/// with nothing copied to it, so DevKit can move to another server with nobody entering anything again. The server
/// keeps nothing, nothing the browser keeps is readable, and a domain that does not answer never costs anyone their
/// settings.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class ClientSettingsTests : SharedSettingsScratch
{
    [Fact]
    public async Task Settings_come_back_on_every_later_page_in_the_same_browser()
    {
        var alice = await SignedInAsync(Alice);
        Assert.True(alice.SaveTfs(FixedUrl, AlicePat));
        alice.TeamName = "TEAM-A";

        var later = await SignedInAsync(Alice);

        Assert.Equal(AlicePat, later.Tfs.Pat);
        Assert.Equal("TEAM-A", later.TeamName);
        Assert.True(later.IsConfigured);
        Assert.Null(later.LoadProblem);
    }

    [Fact]
    public async Task Settings_come_back_from_another_DevKit_server_running_as_the_same_account_with_nothing_copied()
    {
        var alice = await SignedInAsync(Alice);
        alice.SaveTfs(FixedUrl, AlicePat);
        alice.TeamName = "TEAM-A";

        var onTheNewServer = await SignedInAsync(Alice, vault: OtherServer());

        Assert.Equal(AlicePat, onTheNewServer.Tfs.Pat);
        Assert.Equal("TEAM-A", onTheNewServer.TeamName);
        Assert.Null(onTheNewServer.LoadProblem);
    }

    [Fact]
    public async Task A_server_running_as_another_account_starts_on_defaults_and_keeps_them_until_a_save_here()
    {
        (await SignedInAsync(Alice)).SaveTfs(FixedUrl, AlicePat);
        var kept = Browser.Vault;
        var otherAccount = OtherServer(AnotherAccount);

        var alice = await SignedInAsync(Alice, vault: otherAccount);

        Assert.Equal("", alice.Tfs.Pat);
        Assert.Contains("another Windows account", alice.LoadProblem, StringComparison.Ordinal);
        Assert.Equal(kept, Browser.Vault);
        Assert.Equal(AlicePat, (await SignedInAsync(Alice)).Tfs.Pat);

        Assert.True(alice.SaveTfs(FixedUrl, BobPat));
        Assert.Equal(BobPat, (await SignedInAsync(Alice, vault: otherAccount)).Tfs.Pat);
    }

    [Fact]
    public async Task When_the_domain_does_not_answer_nothing_is_saved_over_the_settings()
    {
        var saved = await SignedInAsync(Alice);
        saved.SaveTfs(FixedUrl, AlicePat);
        saved.TeamName = "TEAM-A";
        var kept = Browser.Vault;
        KeyLock.Unavailable = true;

        var page = OpenPage(Alice);
        await page.Session.StartAsync();
        page.Settings.TeamName = "TEAM-DEFAULTS";

        Assert.Equal(page.Store.ReadFailedMessage, page.Settings.LoadProblem);
        Assert.Equal(page.Store.ReadFailedMessage, page.Settings.LastSaveError);
        Assert.Equal(kept, Browser.Vault);

        KeyLock.Unavailable = false;
        var later = await SignedInAsync(Alice);
        Assert.Equal(AlicePat, later.Tfs.Pat);
        Assert.Equal("TEAM-A", later.TeamName);
    }

    [Fact]
    public async Task A_first_save_while_the_domain_does_not_answer_is_reported_and_keeps_nothing()
    {
        var alice = await SignedInAsync(Alice);
        KeyLock.Unavailable = true;

        alice.TeamName = "TEAM-A";

        Assert.NotNull(alice.LastSaveError);
        Assert.Null(Browser.Vault);
    }

    [Fact]
    public async Task Each_person_has_a_key_of_their_own_kept_from_save_to_save()
    {
        var alice = await SignedInAsync(Alice);
        alice.TeamName = "TEAM-A";
        var aliceKey = LockedKey(Browser.Vault);
        alice.TeamName = "TEAM-B";
        (await SignedInAsync(Alice)).TeamName = "TEAM-C";
        var bobsBrowser = new FakeBrowserStorage();
        (await SignedInAsync(Bob, bobsBrowser)).TeamName = "TEAM-B";

        Assert.Equal(aliceKey, LockedKey(Browser.Vault));
        Assert.NotEqual(aliceKey, LockedKey(bobsBrowser.Vault));
    }

    [Fact]
    public async Task Another_accounts_settings_do_not_open_as_this_account()
    {
        (await SignedInAsync(Alice)).SaveTfs(FixedUrl, AlicePat);

        var bob = await SignedInAsync(Bob);

        Assert.Equal("", bob.Tfs.Pat);
        Assert.False(bob.IsConfigured);
        Assert.NotNull(bob.LoadProblem);
    }

    [Fact]
    public async Task Nothing_readable_is_kept_in_the_browser_and_nothing_on_the_server()
    {
        var alice = await SignedInAsync(Alice);
        alice.SaveTfs(FixedUrl, AlicePat);
        alice.TeamName = "TEAM-A";

        Assert.Matches("^v2\\.[0-9a-f]{8}\\.[A-Za-z0-9_-]+\\.[A-Za-z0-9_-]+$", Browser.Vault);
        foreach (var secret in new[] { AlicePat, "TEAM-A", "tfs.example.test", Alice.Name, Alice.Id })
        {
            Assert.DoesNotContain(secret, Browser.Vault, StringComparison.Ordinal);
        }

        Assert.False(Directory.Exists(UsersPath));
    }

    [Fact]
    public async Task The_PAT_is_sealed_with_the_settings_rather_than_tied_to_this_machine()
    {
        (await SignedInAsync(Alice)).SaveTfs(FixedUrl, AlicePat);

        var stored = JsonSerializer.Deserialize<UserSettings>(Vault.Open(Alice, Browser.Vault!).Text)!;

        Assert.Equal(AlicePat, stored.Tfs.Pat);
    }

    [Fact]
    public async Task Pages_of_the_same_account_saving_at_once_never_fail()
    {
        var first = await SignedInAsync(Alice);
        var second = await SignedInAsync(Alice);
        first.SaveTfs(FixedUrl, AlicePat);

        var saves = await Task.WhenAll(Enumerable.Range(0, 40).Select(i => Task.Run(() =>
        {
            var page = i % 2 == 0 ? first : second;
            page.TeamName = $"TEAM-{i}";
            return page.LastSaveError;
        })));

        Assert.All(saves, Assert.Null);
        var after = await SignedInAsync(Alice);
        Assert.Null(after.LoadProblem);
        Assert.StartsWith("TEAM-", after.TeamName, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_save_the_browser_does_not_keep_is_reported()
    {
        var page = OpenPage(Alice);
        await page.Session.StartAsync();
        Browser.RefusesVault = true;

        page.Settings.TeamName = "TEAM-A";

        Assert.False(await page.Store.LastWriteAsync());
        Assert.Equal(page.Store.SaveFailedMessage, page.Settings.LastSaveError);
        Assert.Null(Browser.Vault);
    }

    [Fact]
    public async Task Settings_too_large_for_a_browser_are_not_saved_and_say_so()
    {
        var alice = await SignedInAsync(Alice);
        alice.TeamName = "TEAM-A";
        var kept = Browser.Vault;

        // Random text barely compresses, so this seals to more than a browser keeps.
        alice.TeamName = Convert.ToBase64String(RandomNumberGenerator.GetBytes(ClientVault.MaxSealedChars));

        Assert.NotNull(alice.LastSaveError);
        Assert.Equal(kept, Browser.Vault);
    }

    [Fact]
    public async Task Shared_mode_always_works_against_the_configured_server()
    {
        var alice = await SignedInAsync(Alice);

        alice.SaveTfs("https://elsewhere.example.test/tfs", AlicePat);

        Assert.Equal(FixedUrl, alice.FixedTfsUrl);
        Assert.Equal(FixedUrl, alice.Tfs.Url);
        Assert.Equal(AlicePat, alice.Tfs.Pat);
    }

    [Fact]
    public async Task A_pat_saved_for_another_TFS_server_is_dropped_rather_than_sent_to_this_one()
    {
        const string OldServer = "https://old.example.test/tfs/Collection";
        (await SignedInAsync(Alice, fixedUrl: OldServer)).SaveTfs(OldServer, AlicePat);

        var after = await SignedInAsync(Alice);

        Assert.Equal(FixedUrl, after.Tfs.Url);
        Assert.Equal("", after.Tfs.Pat);
        Assert.False(after.IsConfigured);
    }

    [Fact]
    public async Task Windows_sign_in_to_TFS_is_refused_for_anyone_but_the_account_DevKit_runs_as()
    {
        var alice = await SignedInAsync(Alice);
        var owner = await SignedInAsync(Owner, new FakeBrowserStorage());

        Assert.False(alice.AllowsWindowsSignIn);
        Assert.False(alice.SaveTfs(FixedUrl, "", TfsAuthMode.Windows));
        Assert.NotNull(alice.LastSaveError);
        Assert.False(alice.IsConfigured);

        Assert.True(owner.AllowsWindowsSignIn);
        Assert.True(owner.SaveTfs(FixedUrl, "", TfsAuthMode.Windows));
        Assert.True(owner.IsConfigured);
    }

    [Fact]
    public async Task A_stored_Windows_sign_in_still_goes_out_with_the_pat_for_anyone_but_the_owner()
    {
        var alice = await SignedInAsync(Alice);
        alice.SaveTfs(FixedUrl, AlicePat);
        alice.Tfs.Auth = TfsAuthMode.Windows;
        using var tfs = new RecordingTfsHandler();
        tfs.Reply(HttpStatusCode.OK, """{"value":[]}""");
        using var http = new HttpClient(tfs, disposeHandler: false);

        await new TfsApiService(http, alice, NullLogger<TfsApiService>.Instance).GetProjectsAsync();

        var sent = Assert.Single(tfs.Sent);
        Assert.Equal("Basic " + Convert.ToBase64String(Encoding.ASCII.GetBytes(":" + AlicePat)), sent.Authorization);
    }

    /// <summary>The locked key the sealed text carries.</summary>
    private static string LockedKey(string? sealedText) => sealedText!.Split('.')[2];
}
