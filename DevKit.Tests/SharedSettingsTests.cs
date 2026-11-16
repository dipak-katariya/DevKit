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
/// Shared mode keeps each person's settings and PAT on the DevKit server, in its settings folder, sealed for their
/// Windows account under a key of their own. They come back on every later page, in every browser, after every
/// redeploy, and on every DevKit server running as the same account that has the folder, with no key copied. Nothing
/// kept is readable, nothing is kept in the browser, and a domain that does not answer never costs anyone their
/// settings.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class SharedSettingsTests : SharedSettingsScratch
{
    [Fact]
    public async Task Settings_come_back_on_every_later_page_in_any_browser()
    {
        var alice = await SignedInAsync(Alice);
        Assert.True(alice.SaveTfs(FixedUrl, AlicePat));
        alice.TeamName = "TEAM-A";

        var later = await SignedInAsync(Alice, new FakeBrowserStorage());

        Assert.Equal(AlicePat, later.Tfs.Pat);
        Assert.Equal("TEAM-A", later.TeamName);
        Assert.True(later.IsConfigured);
        Assert.Null(later.LoadProblem);
    }

    [Fact]
    public async Task Settings_come_back_on_another_DevKit_server_running_as_the_same_account_with_no_key_copied()
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
        var kept = KeptFor(Alice);
        var otherAccount = OtherServer(AnotherAccount);

        var alice = await SignedInAsync(Alice, vault: otherAccount);

        Assert.Equal("", alice.Tfs.Pat);
        Assert.Contains("another Windows account", alice.LoadProblem, StringComparison.Ordinal);
        Assert.Equal(kept, KeptFor(Alice));
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
        var kept = KeptFor(Alice);
        KeyLock.Unavailable = true;

        var page = OpenPage(Alice);
        await page.Session.StartAsync();
        page.Settings.TeamName = "TEAM-DEFAULTS";

        Assert.Equal(page.Store.ReadFailedMessage, page.Settings.LoadProblem);
        Assert.Equal(page.Store.ReadFailedMessage, page.Settings.LastSaveError);
        Assert.Equal(kept, KeptFor(Alice));

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
        Assert.Null(KeptFor(Alice));
    }

    [Fact]
    public async Task Each_person_has_a_key_of_their_own_kept_from_save_to_save()
    {
        var alice = await SignedInAsync(Alice);
        alice.TeamName = "TEAM-A";
        var aliceKey = LockedKey(KeptFor(Alice));
        alice.TeamName = "TEAM-B";
        (await SignedInAsync(Alice)).TeamName = "TEAM-C";
        (await SignedInAsync(Bob)).TeamName = "TEAM-B";

        Assert.Equal(aliceKey, LockedKey(KeptFor(Alice)));
        Assert.NotEqual(aliceKey, LockedKey(KeptFor(Bob)));
    }

    [Fact]
    public async Task Another_accounts_settings_do_not_open_as_this_account()
    {
        (await SignedInAsync(Alice)).SaveTfs(FixedUrl, AlicePat);
        // Alice's file in Bob's place, as someone with access to the folder might put it.
        Keep(Bob, KeptFor(Alice)!);

        var bob = await SignedInAsync(Bob);

        Assert.Equal("", bob.Tfs.Pat);
        Assert.False(bob.IsConfigured);
        Assert.NotNull(bob.LoadProblem);
    }

    [Fact]
    public async Task Nothing_readable_is_kept_on_the_server_and_nothing_in_the_browser()
    {
        var alice = await SignedInAsync(Alice);
        alice.SaveTfs(FixedUrl, AlicePat);
        alice.TeamName = "TEAM-A";

        Assert.Matches("^v2\\.[0-9a-f]{8}\\.[A-Za-z0-9_-]+\\.[A-Za-z0-9_-]+$", KeptFor(Alice));
        foreach (var file in Directory.GetFiles(ServerPath))
        {
            var text = await File.ReadAllTextAsync(file);
            foreach (var secret in new[] { AlicePat, "TEAM-A", "tfs.example.test", Alice.Name, Alice.Id, "alice" })
            {
                Assert.DoesNotContain(secret, text, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain(secret, Path.GetFileName(file), StringComparison.OrdinalIgnoreCase);
            }
        }

        Assert.Null(Browser.Vault);
        Assert.Empty(Browser.Items);
        Assert.False(Directory.Exists(UsersPath));
    }

    [Fact]
    public async Task The_PAT_is_sealed_with_the_settings_rather_than_tied_to_this_machine()
    {
        (await SignedInAsync(Alice)).SaveTfs(FixedUrl, AlicePat);

        var stored = JsonSerializer.Deserialize<UserSettings>(Vault.Open(Alice, KeptFor(Alice)!).Text)!;

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
    public async Task A_save_to_a_settings_folder_opened_to_others_is_refused_and_reported()
    {
        var page = OpenPage(Alice);
        await page.Session.StartAsync();
        PersonVaultTests.OpenToEveryone(ServerPath);

        page.Settings.TeamName = "TEAM-A";

        Assert.False(await page.Settings.SaveAndConfirmAsync());
        Assert.Equal(page.Store.SaveFailedMessage, page.Settings.LastSaveError);
        Server.Secure();
        Assert.Null(KeptFor(Alice));
    }

    [Fact]
    public async Task Settings_larger_than_settings_ever_are_not_saved_and_say_so()
    {
        var alice = await SignedInAsync(Alice);
        alice.TeamName = "TEAM-A";
        var kept = KeptFor(Alice);

        // Random text barely compresses, so this seals to more than the limit.
        alice.TeamName = Convert.ToBase64String(RandomNumberGenerator.GetBytes(ClientVault.MaxSealedChars));

        Assert.NotNull(alice.LastSaveError);
        Assert.Equal(kept, KeptFor(Alice));
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

    [Fact]
    public async Task A_page_opened_earlier_never_undoes_what_another_page_saved_meanwhile()
    {
        var openedEarlier = await SignedInAsync(Alice);
        var other = await SignedInAsync(Alice);
        Assert.True(other.SaveTfs(FixedUrl, AlicePat));
        other.TeamName = "TEAM-A";

        openedEarlier.DashboardView = DashboardView.List;

        var later = await SignedInAsync(Alice);
        Assert.Equal("TEAM-A", later.TeamName);
        Assert.Equal(AlicePat, later.Tfs.Pat);
        Assert.Equal(DashboardView.List, later.DashboardView);
        Assert.Null(openedEarlier.LastSaveError);
    }

    [Fact]
    public async Task The_first_saves_of_two_pages_are_both_kept()
    {
        var first = await SignedInAsync(Alice);
        var second = await SignedInAsync(Alice);

        first.TeamName = "TEAM-A";
        second.DashboardView = DashboardView.List;

        var later = await SignedInAsync(Alice);
        Assert.Equal("TEAM-A", later.TeamName);
        Assert.Equal(DashboardView.List, later.DashboardView);
        Assert.Null(later.LoadProblem);
    }

    [Fact]
    public async Task A_choice_a_later_DevKit_saved_stays_through_saves_here()
    {
        (await SignedInAsync(Alice)).TeamName = "TEAM-A";
        var opened = Vault.Open(Alice, KeptFor(Alice)!);
        Keep(Alice, Vault.Seal(Alice, opened.Key,
            opened.Text.Replace("\"DashboardView\": \"Grid\"", "\"DashboardView\": \"Tiles\"", StringComparison.Ordinal)));

        var alice = await SignedInAsync(Alice);
        alice.TeamName = "TEAM-B";

        Assert.Null(alice.LoadProblem);
        Assert.Equal(DashboardView.Grid, alice.DashboardView);
        var kept = Vault.Open(Alice, KeptFor(Alice)!).Text;
        Assert.Contains("\"DashboardView\": \"Tiles\"", kept, StringComparison.Ordinal);
        Assert.Contains("TEAM-B", kept, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Settings_this_DevKit_cannot_read_are_kept_and_nothing_is_saved_over_them()
    {
        (await SignedInAsync(Alice)).TeamName = "TEAM-A";
        var key = Vault.Open(Alice, KeptFor(Alice)!).Key;
        Keep(Alice, Vault.Seal(Alice, key, """["settings in a later form"]"""));
        var kept = KeptFor(Alice);
        var page = OpenPage(Alice);
        await page.Session.StartAsync();

        page.Settings.TeamName = "TEAM-B";

        Assert.Equal(page.Store.UnparsableMessage, page.Settings.LoadProblem);
        Assert.Equal(page.Store.UnparsableMessage, page.Settings.LastSaveError);
        Assert.Equal(kept, KeptFor(Alice));
    }

    /// <summary>The locked key the sealed text carries.</summary>
    private static string LockedKey(string? sealedText) => sealedText!.Split('.')[2];
}
