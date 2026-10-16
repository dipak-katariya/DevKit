using System.Diagnostics;
using DevKit.Web.Services;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DevKit.Tests;

/// <summary>
/// A throwaway DevKit server folder for the shared-mode settings tests: keys kept on disk, as the real server keeps
/// them, so a second <see cref="ServerSettingsFolder"/> over them stands for DevKit after a restart or an update.
/// </summary>
public abstract class ServerSettingsScratch : IDisposable
{
    protected const string FixedUrl = "https://tfs.example.test/tfs/Collection";
    protected const string AlicePat = "alice-test-pat-1c4e";
    protected const string BobPat = "bob-test-pat-7d2a";

    protected static readonly SignedInUser Alice = new("S-1-5-21-1-2-3-1001", @"MT\alice", IsOwner: false);
    protected static readonly SignedInUser Bob = new("S-1-5-21-1-2-3-1002", @"MT\bob", IsOwner: false);
    protected static readonly SignedInUser Owner = new("S-1-5-21-1-2-3-500", @"MT\owner", IsOwner: true);

    protected ServerSettingsScratch()
    {
        Root = Path.Combine(AppContext.BaseDirectory, "server-settings-" + Guid.NewGuid().ToString("N"));
        Keys = KeysOnDisk();
        Folder = new ServerSettingsFolder(UsersPath, Keys);
    }

    protected string Root { get; }

    protected string UsersPath => Path.Combine(Root, ServerSettingsFolder.FolderName);

    protected IDataProtectionProvider Keys { get; }

    private protected ServerSettingsFolder Folder { get; }

    /// <summary>The server's keys as a newly started DevKit reads them back from disk.</summary>
    protected IDataProtectionProvider KeysOnDisk() =>
        DataProtectionProvider.Create(new DirectoryInfo(Path.Combine(Root, "keys")), setup => setup.SetApplicationName("DevKit"));

    /// <summary>One page's settings for <paramref name="user"/>, read from <paramref name="folder"/> (this server by default).</summary>
    private protected static (SettingsService Settings, ServerSettingsStore Store) Open(
        SignedInUser user, ServerSettingsFolder folder, string fixedUrl = FixedUrl)
    {
        var store = new ServerSettingsStore(folder);
        store.Open(user);
        var settings = new SettingsService(NullLogger<SettingsService>.Instance, store, fixedUrl);
        settings.LoadFromStore(user.IsOwner);
        return (settings, store);
    }

    private protected (SettingsService Settings, ServerSettingsStore Store) Open(SignedInUser user, string fixedUrl = FixedUrl) =>
        Open(user, Folder, fixedUrl);

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
}
