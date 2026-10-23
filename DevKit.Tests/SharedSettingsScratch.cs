using System.Diagnostics;
using System.Net;
using System.Security.Claims;
using DevKit.Web.Services;
using Microsoft.AspNetCore.Components.Server.ProtectedBrowserStorage;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DevKit.Tests;

/// <summary>
/// Shared mode's settings for the tests: one DevKit server, running as <see cref="Owner"/>, whose domain locks each
/// person's key to that account, a browser that keeps them, and a throwaway DevKit folder, with the server's keys on
/// disk, where an earlier DevKit kept everyone's settings files.
/// </summary>
public abstract class SharedSettingsScratch : IDisposable
{
    protected const string FixedUrl = "https://tfs.example.test/tfs/Collection";
    protected const string AlicePat = "alice-test-pat-1c4e";
    protected const string BobPat = "bob-test-pat-7d2a";
    protected const string Domain = "example.test";

    protected static readonly SignedInUser Alice = new("S-1-5-21-1-2-3-1001", @"MT\alice", IsOwner: false);
    protected static readonly SignedInUser Bob = new("S-1-5-21-1-2-3-1002", @"MT\bob", IsOwner: false);
    protected static readonly SignedInUser Owner = new("S-1-5-21-1-2-3-500", @"MT\owner", IsOwner: true);

    /// <summary>An account other than the one DevKit runs as, which another DevKit server might run as.</summary>
    protected const string AnotherAccount = "S-1-5-21-1-2-3-600";

    private static readonly OwnerAccount RunsDevKit = new(Owner.Id);

    protected SharedSettingsScratch()
    {
        Root = Path.Combine(AppContext.BaseDirectory, "shared-settings-" + Guid.NewGuid().ToString("N"));
        Keys = KeysOnDisk();
        Folder = new ServerSettingsFolder(UsersPath, Keys);
        Vault = new ClientVault(KeyLock, Domain);
    }

    protected string Root { get; }

    protected string UsersPath => Path.Combine(Root, ServerSettingsFolder.FolderName);

    protected IDataProtectionProvider Keys { get; }

    /// <summary>This test's browser; a page opens in it unless it names another.</summary>
    private protected FakeBrowserStorage Browser { get; } = new();

    private protected ServerSettingsFolder Folder { get; }

    /// <summary>The domain, as this DevKit server asks it to lock and open people's keys.</summary>
    private protected FakeKeyLock KeyLock { get; } = new(Owner.Id);

    /// <summary>This DevKit server's vault.</summary>
    private protected ClientVault Vault { get; }

    /// <summary>The server's keys as a newly started DevKit reads them back from disk.</summary>
    protected IDataProtectionProvider KeysOnDisk() =>
        DataProtectionProvider.Create(new DirectoryInfo(Path.Combine(Root, "keys")), setup => setup.SetApplicationName("DevKit"));

    /// <summary>
    /// A page signed in as <paramref name="user"/> (nobody when null), wired as shared DevKit wires it: in
    /// <paramref name="browser"/>, on the DevKit server with <paramref name="vault"/>, against <paramref name="fixedUrl"/>.
    /// </summary>
    private protected Page OpenPage(
        SignedInUser? user,
        FakeBrowserStorage? browser = null,
        ClientVault? vault = null,
        string fixedUrl = FixedUrl,
        ILogger<EarlierSettingsCopies>? logger = null)
    {
        var inBrowser = browser ?? Browser;
        var store = new ClientSettingsStore(inBrowser, vault ?? Vault, NullLogger<ClientSettingsStore>.Instance);
        var settings = new SettingsService(NullLogger<SettingsService>.Instance, store, fixedUrl);
        store.WriteFailed += settings.ReportSaveFailed;
        var earlier = new EarlierSettingsCopies(
            Folder, new ProtectedLocalStorage(inBrowser, Keys), logger ?? NullLogger<EarlierSettingsCopies>.Instance);
        var principal = user is null
            ? new ClaimsPrincipal(new ClaimsIdentity())
            : new SessionRegistry(new ManualClock()).Start(user, "windows", TimeSpan.FromDays(5), IPAddress.Loopback).Principal;
        return new Page(new SharedUserSession(new FixedAuthenticationState(principal), store, earlier, settings, RunsDevKit), settings, store);
    }

    /// <summary>The settings of a page signed in as <paramref name="user"/>, once it has started.</summary>
    private protected async Task<SettingsService> SignedInAsync(
        SignedInUser user, FakeBrowserStorage? browser = null, ClientVault? vault = null, string fixedUrl = FixedUrl)
    {
        var page = OpenPage(user, browser, vault, fixedUrl);
        Assert.True(await page.Session.StartAsync());
        return page.Settings;
    }

    /// <summary>Another DevKit server, with nothing copied from this one, running as <paramref name="runsAs"/> (the same account by default).</summary>
    private protected static ClientVault OtherServer(string? runsAs = null) => new(new FakeKeyLock(runsAs ?? Owner.Id), Domain);

    /// <summary>What an earlier DevKit kept on this server for <paramref name="user"/>, encrypted with <paramref name="keys"/> (this server's by default).</summary>
    private protected void PutServerCopy(SignedInUser user, string team, IDataProtectionProvider? keys = null)
    {
        var protector = new ServerSettingsFolder(UsersPath, keys ?? Keys).ProtectorFor(user);
        new SettingsFile(Folder.PathFor(user)).Write(protector.Protect(EarlierSettingsText(user, team)));
    }

    /// <summary>What a DevKit before the server's kept in this browser's local storage for <paramref name="user"/>.</summary>
    private protected Task PutLocalStorageCopyAsync(SignedInUser user, string team, IDataProtectionProvider? keys = null) =>
        new ProtectedLocalStorage(Browser, keys ?? Keys)
            .SetAsync(EarlierSettingsCopies.PurposeFor(user), EarlierSettingsCopies.KeyFor(user), EarlierSettingsText(user, team))
            .AsTask();

    /// <summary>A directory junction at <paramref name="link"/> to <paramref name="target"/>; no administrator rights needed.</summary>
    protected static void MakeJunction(string link, string target)
    {
        Directory.CreateDirectory(target);
        using var mklink = Process.Start(new ProcessStartInfo("cmd.exe", $"/c mklink /J \"{link}\" \"{target}\"")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true
        })!;
        mklink.WaitForExit();
        Assert.Equal(0, mklink.ExitCode);
    }

    public void Dispose()
    {
        Dispose(disposing: true);
        GC.SuppressFinalize(this);
    }

    protected virtual void Dispose(bool disposing)
    {
        if (!disposing || !Directory.Exists(Root))
        {
            return;
        }

        // A junction is removed on its own first, never followed, so the folder it points to is deleted only as itself.
        foreach (var link in new DirectoryInfo(Root).EnumerateDirectories().Where(d => d.Attributes.HasFlag(FileAttributes.ReparsePoint)))
        {
            link.Delete();
        }

        Directory.Delete(Root, recursive: true);
    }

    /// <summary>Settings an earlier DevKit wrote for <paramref name="user"/>: their PAT encrypted with DPAPI on this machine, for its server.</summary>
    private static string EarlierSettingsText(SignedInUser user, string team)
    {
        var memory = new MemorySettingsStore();
        var earlier = new SettingsService(NullLogger<SettingsService>.Instance, memory, FixedUrl);
        earlier.LoadFromStore(user.IsOwner);
        earlier.SaveTfs(FixedUrl, user == Bob ? BobPat : AlicePat);
        earlier.TeamName = team;
        return memory.Text!;
    }

    /// <summary>One open page: who it is for, their settings, and where those are kept.</summary>
    private protected sealed record Page(SharedUserSession Session, SettingsService Settings, ClientSettingsStore Store);
}
