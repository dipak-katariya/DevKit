using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;

namespace DevKit.Web.Services;

/// <summary>
/// Shared mode: where every person's settings are kept, on the DevKit server, in one folder beside devkit.json, so a
/// redeploy, a package switch or another port never changes where they are. One file per person, named by a hash of
/// their Windows account so no name shows, holding their settings sealed by <see cref="ClientVault"/>: AES-256-GCM under
/// a key of their own, bound to their account, and that key locked by the domain to the account DevKit runs as. A copied
/// file opens nowhere else.
/// <para>
/// The folder is open to the account DevKit runs as alone: no other user of the server, and no administrator through
/// its group, is listed on it (<see cref="Secure"/>). Every read and write checks that first, and refuses a folder that
/// was opened up or replaced by a link. Each write replaces the file whole and keeps the one before as a backup, one
/// writer at a time per person, also across the two DevKit processes of a package switch.
/// </para>
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class PersonVault
{
    public const string FolderName = "vault";

    private const string Extension = ".sealed";
    private const string BackupExtension = ".bak";
    private const string PartialExtension = ".partial";
    private const string LockExtension = ".lock";
    private const int ExclusiveLock = 0x2;
    private const int FailImmediately = 0x1;
    private const int LockViolation = 33;
    private static readonly TimeSpan LockWait = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan LockRetry = TimeSpan.FromMilliseconds(25);

    private readonly string _rootWithSeparator;
    private readonly SecurityIdentifier _account;
    private readonly ILogger<PersonVault> _logger;

    /// <param name="root">The folder; made, open to <paramref name="account"/> alone, by <see cref="Secure"/>.</param>
    /// <param name="account">The Windows account DevKit runs as, the only one the folder is open to.</param>
    public PersonVault(string root, SecurityIdentifier account, ILogger<PersonVault> logger)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        Root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        _rootWithSeparator = Root + Path.DirectorySeparatorChar;
        _account = account;
        _logger = logger;
    }

    public string Root { get; }

    /// <summary>
    /// Makes the folder, or closes an existing one, so that only the account DevKit runs as is listed on it, as owner
    /// with full control, nothing inherited from above; and has each file in it take only that. Throws when that cannot
    /// be done, so shared DevKit never keeps settings in a folder others can open.
    /// </summary>
    public void Secure()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Root)!);
        var folder = new DirectoryInfo(Root);
        if (folder.Exists && IsLink(folder))
        {
            throw new UnauthorizedAccessException($"The settings folder {Root} is a link or junction, so DevKit does not use it.");
        }

        if (!folder.Exists)
        {
            folder.Create(OwnerOnly());
            _logger.LogInformation("Made the settings folder {Folder}, open to the account DevKit runs as alone", Root);
        }
        else if (!IsOwnerOnly(folder.GetAccessControl()))
        {
            folder.SetAccessControl(OwnerOnly());
            _logger.LogWarning("The settings folder {Folder} was open to others; it is closed again to the account DevKit runs as", Root);
        }

        foreach (var file in folder.EnumerateFiles())
        {
            CloseFile(file);
        }

        EnsureSecure();
    }

    /// <summary>The sealed settings kept for <paramref name="user"/>; null when none are.</summary>
    public string? Load(SignedInUser user) => ReadKept(PathFor(user));

    /// <summary>The sealed settings kept for <paramref name="user"/> before the last save; null when none are.</summary>
    public string? LoadBackup(SignedInUser user) => ReadKept(PathFor(user) + BackupExtension);

    /// <summary>
    /// Replaces <paramref name="user"/>'s settings with what <paramref name="next"/> makes of those kept now (null for
    /// none), while no other save for them runs: one person's tabs saving at once each build on the other's save.
    /// </summary>
    public void Update(SignedInUser user, Func<string?, string> next)
    {
        ArgumentNullException.ThrowIfNull(next);
        var path = PathFor(user);
        using var turn = TakeTurn(user);
        var sealedText = next(ReadKept(path));
        if (!ClientVault.HasSealedShape(sealedText))
        {
            throw new IOException("Only sealed settings are kept in the settings folder.");
        }

        var partial = path + PartialExtension;
        try
        {
            using (var stream = new FileStream(partial, FileMode.Create, FileAccess.Write, FileShare.None, 4_096, FileOptions.WriteThrough))
            using (var writer = new StreamWriter(stream))
            {
                writer.Write(sealedText);
            }

            Own(partial);
            if (File.Exists(path))
            {
                File.Replace(partial, path, path + BackupExtension, ignoreMetadataErrors: true);
            }
            else
            {
                File.Move(partial, path);
            }
        }
        finally
        {
            File.Delete(partial);
        }
    }

    /// <summary>Deletes the backup of <paramref name="user"/>'s settings, which may still hold a PAT they replaced.</summary>
    public void DeleteBackup(SignedInUser user)
    {
        var path = PathFor(user);
        using var turn = TakeTurn(user);
        File.Delete(path + BackupExtension);
    }

    /// <summary>The file of <paramref name="user"/>: inside the folder, whatever their account's identifier holds.</summary>
    internal string PathFor(SignedInUser user)
    {
        ArgumentNullException.ThrowIfNull(user);
        var path = Path.GetFullPath(Path.Combine(Root, Convert.ToHexString(user.IdHash()).ToLowerInvariant() + Extension));
        if (!path.StartsWith(_rootWithSeparator, StringComparison.Ordinal))
        {
            throw new UnauthorizedAccessException("A settings file would lie outside the settings folder.");
        }

        return path;
    }

    /// <summary>Refuses the folder when it is not the one <see cref="Secure"/> made: a link, or open to anyone else.</summary>
    internal void EnsureSecure()
    {
        var folder = new DirectoryInfo(Root);
        if (!folder.Exists)
        {
            throw new IOException($"The settings folder {Root} does not exist; DevKit makes it when it starts.");
        }

        if (IsLink(folder) || !IsOwnerOnly(folder.GetAccessControl()))
        {
            _logger.LogCritical("The settings folder {Folder} was replaced or opened to others, so no settings are read or saved until DevKit restarts and closes it", Root);
            throw new UnauthorizedAccessException($"The settings folder {Root} is open to others or is a link, so DevKit does not use it.");
        }
    }

    private string? ReadKept(string path)
    {
        EnsureSecure();
        var file = new FileInfo(path);
        if (!file.Exists)
        {
            return null;
        }

        if (IsLink(file))
        {
            throw new UnauthorizedAccessException("A settings file is a link, so DevKit does not read it.");
        }

        // Checked through the open file, so what is read is what was checked: owned by the account DevKit runs as, and open
        // to it alone, as Secure leaves every file.
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (!IsOwnerOnly(stream.GetAccessControl(), explicitRulesAllowed: false))
        {
            _logger.LogCritical("A file in the settings folder {Folder} was given to or opened to others, so no settings are read from it until DevKit restarts and closes it", Root);
            throw new UnauthorizedAccessException("A settings file is open to others, so DevKit does not read it.");
        }

        if (stream.Length > ClientVault.MaxSealedChars)
        {
            throw new IOException($"A settings file holds {stream.Length} bytes, more than sealed settings ever do.");
        }

        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    /// <summary>
    /// This DevKit's turn to change <paramref name="user"/>'s files, held until disposed: a lock on a file beside theirs.
    /// A named mutex would only be shared within one Windows session, and the DevKit a package switch replaces may run in
    /// another (started at boot, say). Windows lets the lock go when the handle closes, also when the process ends.
    /// </summary>
    private FileStream TakeTurn(SignedInUser user)
    {
        EnsureSecure();
        var turn = new FileStream(Path.ChangeExtension(PathFor(user), LockExtension), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.ReadWrite);
        var waited = Stopwatch.StartNew();
        var overlapped = default(NativeOverlapped);
        while (!LockFileEx(turn.SafeFileHandle, ExclusiveLock | FailImmediately, 0, 1, 0, ref overlapped))
        {
            var error = Marshal.GetLastPInvokeError();
            if (error != LockViolation || waited.Elapsed >= LockWait)
            {
                turn.Dispose();
                throw new IOException(error == LockViolation
                    ? "Another save of the same person's settings did not finish in time."
                    : $"A save could not take its turn (Windows error {error}).");
            }

            Thread.Sleep(LockRetry);
        }

        return turn;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool LockFileEx(SafeFileHandle file, int flags, int reserved, int lowBytes, int highBytes, ref NativeOverlapped overlapped);

    private DirectorySecurity OwnerOnly()
    {
        var security = new DirectorySecurity();
        security.SetOwner(_account);
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.AddAccessRule(new FileSystemAccessRule(_account, FileSystemRights.FullControl,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        return security;
    }

    /// <summary>
    /// Whether <paramref name="security"/> lists the account DevKit runs as alone, as owner and in every rule. The folder
    /// lists it by hand; a file (<paramref name="explicitRulesAllowed"/> false) only through what the folder passes on.
    /// </summary>
    private bool IsOwnerOnly(FileSystemSecurity security, bool explicitRulesAllowed = true)
    {
        var rules = security.GetAccessRules(includeExplicit: true, includeInherited: true, typeof(SecurityIdentifier))
            .Cast<FileSystemAccessRule>().ToList();
        return security.AreAccessRulesProtected == explicitRulesAllowed
            && _account.Equals(security.GetOwner(typeof(SecurityIdentifier)))
            && rules.Count > 0
            && rules.All(rule => rule.AccessControlType == AccessControlType.Allow && _account.Equals(rule.IdentityReference)
                && (explicitRulesAllowed || rule.IsInherited));
    }

    // A file is owned by the account DevKit runs as and takes only what the folder gives: anything listed on it by hand
    // is removed.
    private void CloseFile(FileInfo file)
    {
        var security = file.GetAccessControl();
        if (IsOwnerOnly(security, explicitRulesAllowed: false))
        {
            return;
        }

        var reset = new FileSecurity();
        reset.SetOwner(_account);
        reset.SetAccessRuleProtection(isProtected: false, preserveInheritance: false);
        file.SetAccessControl(reset);
        _logger.LogWarning("A file in the settings folder had another owner or its own permissions; it is owned by the account DevKit runs as again and takes the folder's permissions alone");
    }

    /// <summary>Makes the account DevKit runs as the owner of a new file, which an administrator's files otherwise are not.</summary>
    private void Own(string path)
    {
        var owner = new FileSecurity();
        owner.SetOwner(_account);
        new FileInfo(path).SetAccessControl(owner);
    }

    private static bool IsLink(FileSystemInfo entry) => entry.Attributes.HasFlag(FileAttributes.ReparsePoint);
}
