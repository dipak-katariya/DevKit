using System.Net;
using System.Runtime.Versioning;
using System.Security.Claims;
using System.Security.Cryptography;
using DevKit.Web.Services;
using Microsoft.AspNetCore.Components.Server.ProtectedBrowserStorage;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DevKit.Tests;

/// <summary>
/// A shared-mode page opens with the signed-in person's settings from the server, whatever browser or address it is
/// opened in. A copy an earlier DevKit kept in the browser moves to the server once and leaves the browser.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class BrowserSettingsMigrationTests : ServerSettingsScratch
{
    private static readonly OwnerAccount SomeoneElseRunsDevKit = new("S-1-5-21-9");

    private readonly FakeBrowserStorage _browser = new();

    /// <summary>What an earlier DevKit wrote into this browser for <paramref name="user"/>, encrypted for them.</summary>
    private async Task PutEarlierBrowserCopyAsync(SignedInUser user, string team, IDataProtectionProvider? keys = null)
    {
        var memory = new MemorySettingsStore();
        var earlier = new SettingsService(NullLogger<SettingsService>.Instance, memory, FixedUrl);
        earlier.LoadFromStore(user.IsOwner);
        earlier.SaveTfs(FixedUrl, AlicePat);
        earlier.TeamName = team;
        await new ProtectedLocalStorage(_browser, keys ?? Keys)
            .SetAsync(BrowserSettingsMigration.PurposeFor(user), BrowserSettingsMigration.KeyFor(user), memory.Text!);
    }

    /// <summary>A page signed in as <paramref name="user"/>, in <paramref name="browser"/> (this test's browser by default).</summary>
    private (SharedUserSession Session, SettingsService Settings) Page(
        SignedInUser? user, FakeBrowserStorage? browser = null, ILogger<BrowserSettingsMigration>? logger = null)
    {
        var store = new ServerSettingsStore(Folder);
        var settings = new SettingsService(NullLogger<SettingsService>.Instance, store, FixedUrl);
        var migration = new BrowserSettingsMigration(
            new ProtectedLocalStorage(browser ?? _browser, Keys), logger ?? NullLogger<BrowserSettingsMigration>.Instance);
        var principal = user is null
            ? new ClaimsPrincipal(new ClaimsIdentity())
            : new SessionRegistry(new ManualClock()).Start(user, "windows", TimeSpan.FromDays(5), IPAddress.Loopback).Principal;
        return (new SharedUserSession(new FixedAuthenticationState(principal), store, migration, settings, SomeoneElseRunsDevKit), settings);
    }

    [Fact]
    public async Task The_page_opens_only_once_it_knows_who_is_signed_in_and_has_their_settings()
    {
        Open(Alice).Settings.SaveTfs(FixedUrl, AlicePat);
        var (session, settings) = Page(Alice);

        Assert.False(session.IsReady);
        Assert.True(await session.StartAsync());

        Assert.True(session.IsReady);
        Assert.Equal(Alice.Id, session.User!.Id);
        Assert.Equal(AlicePat, settings.Tfs.Pat);
        Assert.False(settings.AllowsWindowsSignIn);
        Assert.Equal($"DevKit's folder on the server, for {Alice.Name} only", settings.SettingsFilePath);
    }

    [Fact]
    public async Task Settings_come_back_in_another_browser_or_at_another_address()
    {
        var (first, settings) = Page(Alice);
        await first.StartAsync();
        settings.SaveTfs(FixedUrl, AlicePat);
        settings.TeamName = "TEAM-A";

        var (elsewhere, settingsElsewhere) = Page(Alice, new FakeBrowserStorage());
        await elsewhere.StartAsync();

        Assert.Equal(AlicePat, settingsElsewhere.Tfs.Pat);
        Assert.Equal("TEAM-A", settingsElsewhere.TeamName);
    }

    [Fact]
    public async Task A_page_whose_session_has_ended_is_not_opened()
    {
        var (session, _) = Page(null);

        Assert.False(await session.StartAsync());
        Assert.False(session.IsReady);
        Assert.False(Directory.Exists(UsersPath));
    }

    [Fact]
    public async Task Settings_an_earlier_DevKit_kept_in_this_browser_move_to_the_server_once()
    {
        await PutEarlierBrowserCopyAsync(Alice, "TEAM-A");
        var (session, settings) = Page(Alice);

        await session.StartAsync();

        Assert.Equal(AlicePat, settings.Tfs.Pat);
        Assert.Equal("TEAM-A", settings.TeamName);
        Assert.Empty(_browser.Items);
        Assert.True(File.Exists(Folder.PathFor(Alice)));
        var (later, settingsLater) = Page(Alice, new FakeBrowserStorage());
        await later.StartAsync();
        Assert.Equal(AlicePat, settingsLater.Tfs.Pat);
    }

    [Fact]
    public async Task A_browser_copy_is_dropped_when_the_server_already_has_settings()
    {
        Open(Alice).Settings.TeamName = "TEAM-SERVER";
        await PutEarlierBrowserCopyAsync(Alice, "TEAM-OLD");
        var (session, settings) = Page(Alice);

        await session.StartAsync();

        Assert.Equal("TEAM-SERVER", settings.TeamName);
        Assert.Empty(_browser.Items);
    }

    [Fact]
    public async Task A_browser_copy_that_no_longer_decrypts_is_deleted_and_logged()
    {
        await PutEarlierBrowserCopyAsync(Alice, "TEAM-A", new EphemeralDataProtectionProvider());
        var logger = new RecordingLogger<BrowserSettingsMigration>();
        var (session, settings) = Page(Alice, logger: logger);

        Assert.True(await session.StartAsync());

        Assert.Contains(logger.Entries, e => e.Level == LogLevel.Error && e.Exception is CryptographicException);
        Assert.Empty(_browser.Items);
        Assert.Equal("", settings.Tfs.Pat);
    }

    [Fact]
    public async Task A_browser_copy_the_server_cannot_take_stays_for_the_next_visit()
    {
        Directory.CreateDirectory(Root);
        MakeJunction(UsersPath, Path.Combine(Root, "elsewhere"));
        await PutEarlierBrowserCopyAsync(Alice, "TEAM-A");
        var logger = new RecordingLogger<BrowserSettingsMigration>();
        var (session, settings) = Page(Alice, logger: logger);

        Assert.True(await session.StartAsync());

        Assert.Single(_browser.Items);
        Assert.Contains(logger.Entries, e => e.Level == LogLevel.Error && e.Exception is IOException);
        Assert.NotNull(settings.LoadProblem);
    }
}
