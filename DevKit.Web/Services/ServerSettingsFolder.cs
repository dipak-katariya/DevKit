using Microsoft.AspNetCore.DataProtection;

namespace DevKit.Web.Services;

/// <summary>
/// Where an earlier shared DevKit kept everyone's settings on the server: one file per Windows account, named by a hash
/// of the account's identifier so no name appears on disk, encrypted with the server's keys for that account alone, in
/// DevKit's own folder. DevKit no longer writes here. Each person's file is read once, to move it into their own browser
/// (<see cref="EarlierSettingsCopies"/>), and is then deleted with everything kept beside it.
/// </summary>
internal sealed class ServerSettingsFolder
{
    public const string FolderName = "users";

    private const string PurposePrefix = "DevKit.ServerUserSettings.v1|";
    private const string FileExtension = ".json";
    private const string BackupSuffix = ".bak";

    private readonly string _rootWithSeparator;
    private readonly IDataProtectionProvider _keys;

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
        var name = Convert.ToHexString(user.IdHash()).ToLowerInvariant() + FileExtension;
        var path = Path.GetFullPath(Path.Combine(Root, name));
        if (!path.StartsWith(_rootWithSeparator, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("A settings file would lie outside DevKit's settings folder.");
        }

        return path;
    }

    /// <summary>How the file was encrypted for <paramref name="user"/> alone: text protected for one account does not decrypt as another's.</summary>
    public IDataProtector ProtectorFor(SignedInUser user) => _keys.CreateProtector(PurposePrefix + user.Id);

    /// <summary>
    /// The settings text in <paramref name="user"/>'s file, or null when there is none. Throws
    /// <see cref="System.Security.Cryptography.CryptographicException"/> when the server's keys no longer open it.
    /// </summary>
    public string? ReadText(SignedInUser user)
    {
        var path = PathFor(user);
        EnsureNotLinked(path);
        return new SettingsFile(path).ReadText() is { } stored ? ProtectorFor(user).Unprotect(stored) : null;
    }

    /// <summary>
    /// Deletes <paramref name="user"/>'s file with its backup and any damaged copies set aside beside it, which all start
    /// with its name; returns how many files there were.
    /// </summary>
    public int Delete(SignedInUser user)
    {
        var path = PathFor(user);
        EnsureNotLinked(path);
        if (!Directory.Exists(Root))
        {
            return 0;
        }

        // Only this folder's own entries, never its subfolders: each one lies inside the folder.
        var copies = Directory.GetFiles(Root, Path.GetFileName(path) + "*", SearchOption.TopDirectoryOnly);
        foreach (var copy in copies)
        {
            File.Delete(copy);
        }

        return copies.Length;
    }

    /// <summary>
    /// Refuses the folder, the file or its backup when one is a symbolic link or junction: through one, a read or a
    /// delete would reach a place outside DevKit's folder.
    /// </summary>
    private void EnsureNotLinked(string path)
    {
        if (IsLink(new DirectoryInfo(Root)) || IsLink(new FileInfo(path)) || IsLink(new FileInfo(path + BackupSuffix)))
        {
            throw new IOException("DevKit's settings folder or a settings file in it is a link, so it is not used.");
        }
    }

    private static bool IsLink(FileSystemInfo entry) => entry.Exists && entry.Attributes.HasFlag(FileAttributes.ReparsePoint);
}
