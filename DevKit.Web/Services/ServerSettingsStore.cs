using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.DataProtection;

namespace DevKit.Web.Services;

/// <summary>
/// Shared mode's folder of everyone's settings on the DevKit server: one file per Windows account, named by a hash of
/// the account's identifier so no name appears on disk, in DevKit's own folder, which only the account DevKit runs as,
/// SYSTEM and administrators can open. It does not depend on the site's address, the browser or DevKit's build, so a
/// person's settings come back however and wherever they open DevKit, and an update leaves them in place.
/// </summary>
internal sealed class ServerSettingsFolder
{
    public const string FolderName = "users";

    private const string PurposePrefix = "DevKit.ServerUserSettings.v1|";
    private const string FileExtension = ".json";
    private const string BackupSuffix = ".bak";

    private readonly string _rootWithSeparator;
    private readonly IDataProtectionProvider _keys;
    private readonly ConcurrentDictionary<string, object> _writeLocks = new(StringComparer.Ordinal);

    public ServerSettingsFolder(string root, IDataProtectionProvider keys)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        Root = Path.GetFullPath(root);
        _rootWithSeparator = Path.EndsInDirectorySeparator(Root) ? Root : Root + Path.DirectorySeparatorChar;
        _keys = keys;
    }

    public string Root { get; }

    /// <summary>The file of <paramref name="user"/>: in the folder, whatever the account's identifier holds.</summary>
    public string PathFor(SignedInUser user)
    {
        ArgumentNullException.ThrowIfNull(user);
        var name = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(user.Id))).ToLowerInvariant() + FileExtension;
        var path = Path.GetFullPath(Path.Combine(Root, name));
        if (!path.StartsWith(_rootWithSeparator, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("A settings file would lie outside DevKit's settings folder.");
        }

        return path;
    }

    /// <summary>Encrypts for <paramref name="user"/> alone: text protected for one account does not decrypt as another's.</summary>
    public IDataProtector ProtectorFor(SignedInUser user) => _keys.CreateProtector(PurposePrefix + user.Id);

    /// <summary>One lock per file, shared by all of that person's open pages, so two saves never interleave.</summary>
    public object WriteLockFor(string path) => _writeLocks.GetOrAdd(path, _ => new object());

    /// <summary>
    /// Refuses the folder, the file or its backup when one is a symbolic link or junction: through one, a read or a
    /// save would reach a place outside DevKit's folder.
    /// </summary>
    public void EnsureNotLinked(string path)
    {
        if (IsLink(new DirectoryInfo(Root)) || IsLink(new FileInfo(path)) || IsLink(new FileInfo(path + BackupSuffix)))
        {
            throw new IOException("DevKit's settings folder or a settings file in it is a link, so it is not used.");
        }
    }

    private static bool IsLink(FileSystemInfo entry) => entry.Exists && entry.Attributes.HasFlag(FileAttributes.ReparsePoint);
}

/// <summary>
/// Shared mode: one person's settings, PAT included, in their own file in <see cref="ServerSettingsFolder"/>. The text
/// is encrypted with the server's Data Protection keys for a purpose bound to the person's Windows account, so a file
/// put in place of another's does not decrypt as theirs; the PAT inside is also encrypted for its TFS server, as in the
/// settings file. Nothing is read or written until <see cref="Open"/> names the person.
/// </summary>
internal sealed class ServerSettingsStore : ISettingsStore
{
    private readonly ServerSettingsFolder _folder;
    private PersonFile? _person;

    public ServerSettingsStore(ServerSettingsFolder folder)
    {
        _folder = folder;
    }

    public string Location { get; private set; } = "DevKit's folder on the server";

    /// <summary>Whether the person has settings on the server yet.</summary>
    public bool HasSettings => _person?.File.Exists == true;

    /// <summary>Points the store at <paramref name="user"/>'s file; who that is comes from their sign-in, never from the page.</summary>
    public void Open(SignedInUser user)
    {
        ArgumentNullException.ThrowIfNull(user);
        var path = _folder.PathFor(user);
        _person = new PersonFile(new SettingsFile(path), _folder.ProtectorFor(user), _folder.WriteLockFor(path));
        Location = $"DevKit's folder on the server, for {user.Name} only";
    }

    /// <summary>The settings text, or null when there is none; throws <see cref="CryptographicException"/> when it does not decrypt.</summary>
    public string? ReadText()
    {
        var person = Opened();
        return person.Unprotect(person.File.ReadText());
    }

    public string? ReadBackupText()
    {
        var person = Opened();
        return person.Unprotect(person.File.ReadBackupText());
    }

    public void Write(string text)
    {
        var person = Opened();
        lock (person.WriteLock)
        {
            person.File.Write(person.Protector.Protect(text));
        }
    }

    public void DeleteBackup()
    {
        var person = Opened();
        lock (person.WriteLock)
        {
            person.File.DeleteBackup();
        }
    }

    public void SetAsideCorrupt()
    {
        var person = Opened();
        lock (person.WriteLock)
        {
            person.File.SetAsideCorrupt();
        }
    }

    private PersonFile Opened()
    {
        var person = _person ?? throw new IOException("Settings cannot be used before DevKit knows who is signed in.");
        _folder.EnsureNotLinked(person.File.FullPath);
        return person;
    }

    /// <summary>The one person's file, the key that opens it, and the lock their pages share.</summary>
    private sealed record PersonFile(SettingsFile File, IDataProtector Protector, object WriteLock)
    {
        public string? Unprotect(string? stored) => stored is null ? null : Protector.Unprotect(stored);
    }
}
