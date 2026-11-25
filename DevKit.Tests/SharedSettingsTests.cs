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
    public async Task The_tour_opens_once_per_person_even_when_it_was_never_finished()
    {
        var first = await SignedInAsync(Alice);
        Assert.True(first.TryStartGuideTour());

        var nextSignIn = await SignedInAsync(Alice, new FakeBrowserStorage());
        var bob = await SignedInAsync(Bob);

        Assert.False(nextSignIn.TryStartGuideTour());
        Assert.True(bob.TryStartGuideTour());
    }

    [Fact]
    public async Task The_tour_does_not_open_while_the_settings_could_not_be_read()
    {
        (await SignedInAsync(Alice)).TeamName = "TEAM-A";
        KeyLock.Unavailable = true;

        var page = OpenPage(Alice);
        await page.Session.StartAsync();

        Assert.NotNull(page.Settings.LoadProblem);
        Assert.False(page.Settings.TryStartGuideTour());
    }

    [Fact]
    public async Task A_save_from_the_settings_form_keeps_what_another_tab_saved_meanwhile()
    {
        (await SignedInAsync(Alice)).SaveTfs(FixedUrl, AlicePat);
        var formTab = await SignedInAsync(Alice);
        var form = formTab.CurrentPreferences();
        (await SignedInAsync(Alice)).TeamName = "TEAM-B";

        form.DefaultRepoId = "repo-9";
        Assert.True(formTab.SavePreferences(form.ChangesFrom(formTab.CurrentPreferences())));

        var later = await SignedInAsync(Alice);
        Assert.Equal("TEAM-B", later.TeamName);
        Assert.Equal("repo-9", later.DefaultRepoId);
        Assert.Equal(AlicePat, later.Tfs.Pat);
    }

    [Fact]
    public async Task Two_tabs_saving_different_update_status_settings_keep_both()
    {
        var valueTab = await SignedInAsync(Alice);
        var tagsTab = await SignedInAsync(Alice);

        Assert.True(valueTab.SavePreferences(new PortableSettings { StatusValue = "Done" }));
        Assert.True(tagsTab.SavePreferences(new PortableSettings { StatusTags = new List<string> { "QA Done" } }));

        var later = (await SignedInAsync(Alice)).StatusUpdate;
        Assert.Equal("Done", later.Value);
        Assert.Equal(new[] { "QA Done" }, later.Tags);
    }

    [Fact]
    public async Task A_page_closed_while_it_started_does_not_go_on_hearing_saves()
    {
        (await SignedInAsync(Alice)).TeamName = "TEAM-A";
        var closed = OpenPage(Alice);

        closed.Session.Dispose();

        Assert.False(await closed.Session.StartAsync());
        (await SignedInAsync(Alice)).TeamName = "TEAM-B";
        await Task.Delay(TimeSpan.FromMilliseconds(300));
        Assert.Equal("TEAM-A", closed.Settings.TeamName);
    }

    [Fact]
    public async Task Another_open_page_of_the_same_person_shows_what_one_page_saved()
    {
        var saving = OpenPage(Alice);
        var other = OpenPage(Alice);
        var bob = OpenPage(Bob);
        await saving.Session.StartAsync();
        await other.Session.StartAsync();
        await bob.Session.StartAsync();

        Assert.True(saving.Settings.SaveTfs(FixedUrl, AlicePat));
        saving.Settings.TeamName = "TEAM-A";

        await WaitUntilAsync(() => other.Settings.IsConfigured && other.Settings.TeamName == "TEAM-A");
        Assert.Equal(AlicePat, other.Settings.Tfs.Pat);
        Assert.False(bob.Settings.IsConfigured);
        Assert.Equal("", bob.Settings.TeamName);
    }

    [Fact]
    public async Task An_open_page_reads_another_pages_save_without_asking_the_domain_again()
    {
        (await SignedInAsync(Alice)).TeamName = "TEAM-A";
        var saving = OpenPage(Alice);
        var other = OpenPage(Alice);
        await saving.Session.StartAsync();
        await other.Session.StartAsync();
        KeyLock.Unavailable = true;

        saving.Settings.TeamName = "TEAM-B";

        await WaitUntilAsync(() => other.Settings.TeamName == "TEAM-B");
        Assert.Null(other.Settings.LoadProblem);
    }

    [Fact]
    public async Task A_closed_page_stops_hearing_the_persons_saves()
    {
        var saving = OpenPage(Alice);
        var closed = OpenPage(Alice);
        await saving.Session.StartAsync();
        await closed.Session.StartAsync();

        closed.Session.Dispose();
        saving.Settings.TeamName = "TEAM-A";
        await Task.Delay(TimeSpan.FromMilliseconds(300));

        Assert.Equal("", closed.Settings.TeamName);
    }

    [Fact]
    public async Task Saving_waits_while_the_settings_do_not_open_and_Try_again_opens_them()
    {
        var saved = await SignedInAsync(Alice);
        saved.SaveTfs(FixedUrl, AlicePat);
        saved.TeamName = "TEAM-A";
        KeyLock.Unavailable = true;
        var page = OpenPage(Alice);
        await page.Session.StartAsync();
        Assert.True(page.Settings.SavingBlocked);

        page.Settings.ReloadFromStore();
        Assert.True(page.Settings.SavingBlocked);
        KeyLock.Unavailable = false;
        page.Settings.ReloadFromStore();

        Assert.False(page.Settings.SavingBlocked);
        Assert.Null(page.Settings.LoadProblem);
        Assert.Equal("TEAM-A", page.Settings.TeamName);
        Assert.True(page.Settings.SavePreferences(new PortableSettings { DefaultRepoId = "repo-1" }));
        var later = await SignedInAsync(Alice);
        Assert.Equal(AlicePat, later.Tfs.Pat);
        Assert.Equal("repo-1", later.DefaultRepoId);
    }

    /// <summary>Waits for what another page does on its own thread, failing after a few seconds rather than hanging.</summary>
    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "The other page did not show the save in time.");
            await Task.Delay(TimeSpan.FromMilliseconds(20));
        }
    }

    [Fact]
    public void The_saved_token_leaves_only_when_asked_and_each_use_is_logged_without_it()
    {
        var logger = new MessageRecordingLogger<SettingsService>();
        var settings = new SettingsService(logger, new MemorySettingsStore(), FixedUrl);
        settings.LoadFromStore(allowsWindowsSignIn: false);
        Assert.Equal("", settings.RevealStoredPat(PatReveal.Shown));
        settings.SaveTfs(FixedUrl, AlicePat);

        Assert.Equal(AlicePat, settings.RevealStoredPat(PatReveal.Shown));
        Assert.Equal(AlicePat, settings.RevealStoredPat(PatReveal.Copied));

        Assert.True(settings.IsStoredPat($" {AlicePat} "));
        Assert.False(settings.IsStoredPat(BobPat));
        Assert.False(settings.IsStoredPat(""));
        Assert.Equal(2, logger.Entries.Count(e => e.Message.Contains("stored PAT was", StringComparison.Ordinal)));
        Assert.All(logger.Entries, e => Assert.DoesNotContain(AlicePat, e.AllText, StringComparison.Ordinal));
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

        // A failed save here used to come from another page's read holding the file open while this one replaced it.
        Assert.Null(first.LastSaveError);
        Assert.Null(second.LastSaveError);
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
