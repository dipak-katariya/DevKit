using System.Net;
using System.Runtime.Versioning;
using System.Text;
using DevKit.Web.Models;
using DevKit.Web.Services;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DevKit.Tests;

/// <summary>
/// Shared mode keeps each person's settings and PAT in a file of their own on the DevKit server, encrypted for their
/// Windows account: they come back on every later page, after DevKit restarts or is updated, whatever browser or
/// address the person uses; nobody else's sign-in reads them, and nothing on disk is readable.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class ServerSettingsTests : ServerSettingsScratch
{
    [Fact]
    public void Settings_come_back_for_the_same_account_on_every_later_page()
    {
        var (alice, _) = Open(Alice);
        Assert.True(alice.SaveTfs(FixedUrl, AlicePat));
        alice.TeamName = "TEAM-A";

        var (later, _) = Open(Alice);

        Assert.Equal(AlicePat, later.Tfs.Pat);
        Assert.Equal("TEAM-A", later.TeamName);
        Assert.True(later.IsConfigured);
        Assert.Null(later.LoadProblem);
    }

    [Fact]
    public void Settings_survive_a_restart_or_an_update_of_DevKit()
    {
        var (alice, _) = Open(Alice);
        alice.SaveTfs(FixedUrl, AlicePat);
        alice.TeamName = "TEAM-A";

        var restarted = new ServerSettingsFolder(UsersPath, KeysOnDisk());
        var (after, _) = Open(Alice, restarted);

        Assert.Equal(AlicePat, after.Tfs.Pat);
        Assert.Equal("TEAM-A", after.TeamName);
    }

    [Fact]
    public void Each_account_finds_only_its_own_settings()
    {
        var (alice, _) = Open(Alice);
        alice.SaveTfs(FixedUrl, AlicePat);
        alice.TeamName = "TEAM-A";

        var (bob, _) = Open(Bob);

        Assert.Equal("", bob.Tfs.Pat);
        Assert.Equal("", bob.TeamName);
        Assert.False(bob.IsConfigured);
        bob.SaveTfs(FixedUrl, BobPat);
        Assert.Equal(2, Directory.GetFiles(UsersPath, "*.json").Length);
        Assert.Equal(AlicePat, Open(Alice).Settings.Tfs.Pat);
        Assert.Equal(BobPat, Open(Bob).Settings.Tfs.Pat);
    }

    [Fact]
    public void Nothing_readable_is_stored_on_the_server()
    {
        var (alice, _) = Open(Alice);
        alice.SaveTfs(FixedUrl, AlicePat);
        alice.TeamName = "TEAM-A";

        var file = Assert.Single(Directory.GetFiles(UsersPath, "*.json"));
        var name = Path.GetFileName(file);
        var text = File.ReadAllText(file);

        Assert.DoesNotContain(Alice.Id, name, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("alice", name, StringComparison.OrdinalIgnoreCase);
        // Only encrypted text: no braces, quotes or names of settings, whatever bytes the encryption produced.
        Assert.Matches("^[A-Za-z0-9_-]+$", text);
        foreach (var secret in new[] { AlicePat, "TEAM-A", "tfs.example.test", Alice.Name, Alice.Id })
        {
            Assert.DoesNotContain(secret, text, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Another_accounts_file_put_in_place_does_not_decrypt_as_this_account()
    {
        Open(Alice).Settings.SaveTfs(FixedUrl, AlicePat);
        Open(Bob).Settings.TeamName = "TEAM-B";
        File.Copy(Folder.PathFor(Alice), Folder.PathFor(Bob), overwrite: true);

        var (bob, _) = Open(Bob);

        Assert.Equal("", bob.Tfs.Pat);
        Assert.NotNull(bob.LoadProblem);
        Assert.Single(Directory.GetFiles(UsersPath, "*.corrupt-*"));
        Assert.True(bob.SaveTfs(FixedUrl, BobPat));
        Assert.Equal(BobPat, Open(Bob).Settings.Tfs.Pat);
    }

    [Fact]
    public void Settings_the_servers_keys_no_longer_open_are_set_aside_not_lost()
    {
        Open(Alice).Settings.SaveTfs(FixedUrl, AlicePat);
        var stored = File.ReadAllText(Folder.PathFor(Alice));

        var newKeys = new ServerSettingsFolder(UsersPath, new EphemeralDataProtectionProvider());
        var (alice, _) = Open(Alice, newKeys);

        Assert.Equal("", alice.Tfs.Pat);
        Assert.NotNull(alice.LoadProblem);
        var kept = Assert.Single(Directory.GetFiles(UsersPath, "*.corrupt-*"));
        Assert.Equal(stored, File.ReadAllText(kept));
    }

    [Fact]
    public async Task Pages_of_the_same_account_saving_at_once_never_fail()
    {
        var (first, _) = Open(Alice);
        var (second, _) = Open(Alice);
        first.SaveTfs(FixedUrl, AlicePat);

        var saves = await Task.WhenAll(Enumerable.Range(0, 40).Select(i => Task.Run(() =>
        {
            var page = i % 2 == 0 ? first : second;
            page.TeamName = $"TEAM-{i}";
            return page.LastSaveError;
        })));

        Assert.All(saves, Assert.Null);
        var (after, _) = Open(Alice);
        Assert.Null(after.LoadProblem);
        Assert.StartsWith("TEAM-", after.TeamName, StringComparison.Ordinal);
    }

    [Fact]
    public void A_link_in_place_of_the_settings_folder_is_refused()
    {
        var elsewhere = Path.Combine(Root, "elsewhere");
        Directory.CreateDirectory(Root);
        MakeJunction(UsersPath, elsewhere);

        var (alice, _) = Open(Alice);

        Assert.False(alice.SaveTfs(FixedUrl, AlicePat));
        Assert.NotNull(alice.LastSaveError);
        Assert.Empty(Directory.GetFileSystemEntries(elsewhere));
    }

    [Fact]
    public void Shared_mode_always_works_against_the_configured_server()
    {
        var (alice, _) = Open(Alice);

        alice.SaveTfs("https://elsewhere.example.test/tfs", AlicePat);

        Assert.Equal(FixedUrl, alice.FixedTfsUrl);
        Assert.Equal(FixedUrl, alice.Tfs.Url);
        Assert.Equal(AlicePat, alice.Tfs.Pat);
    }

    [Fact]
    public void A_pat_saved_for_another_server_is_dropped_rather_than_sent_to_this_one()
    {
        Open(Alice, "https://old.example.test/tfs/Collection").Settings.SaveTfs("https://old.example.test/tfs/Collection", AlicePat);

        var (after, _) = Open(Alice);

        Assert.Equal(FixedUrl, after.Tfs.Url);
        Assert.Equal("", after.Tfs.Pat);
        Assert.False(after.IsConfigured);
    }

    [Fact]
    public void Windows_sign_in_to_TFS_is_refused_for_anyone_but_the_account_DevKit_runs_as()
    {
        var (alice, _) = Open(Alice);
        var (owner, _) = Open(Owner);

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
        var (alice, _) = Open(Alice);
        alice.SaveTfs(FixedUrl, AlicePat);
        alice.Tfs.Auth = TfsAuthMode.Windows;
        using var tfs = new RecordingTfsHandler();
        tfs.Reply(HttpStatusCode.OK, """{"value":[]}""");
        using var http = new HttpClient(tfs, disposeHandler: false);

        await new TfsApiService(http, alice, NullLogger<TfsApiService>.Instance).GetProjectsAsync();

        var sent = Assert.Single(tfs.Sent);
        Assert.Equal("Basic " + Convert.ToBase64String(Encoding.ASCII.GetBytes(":" + AlicePat)), sent.Authorization);
    }
}
