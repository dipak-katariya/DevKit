using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;

namespace DevKit.Web.Services;

/// <summary>
/// Who besides this account, SYSTEM and administrators may change a folder. DevKit runs its packages as this account,
/// so anyone else who may change a package, or the Release folder they are in, could run their own code as it. The
/// switch (<see cref="DevKitSwitch"/>) refuses such a folder; tools\devkit-lock.bat leaves only those three in it.
/// Administrators count one by one too, as an account of theirs a permission names (<see cref="LocalAdministrators"/>):
/// they may take any file over anyway.
/// </summary>
[SupportedOSPlatform("windows")]
internal static class FolderAccess
{
    // Any of these lets an account change a file or folder, or the permissions that guard it.
    private const FileSystemRights ChangeRights =
        FileSystemRights.WriteData | FileSystemRights.AppendData | FileSystemRights.WriteExtendedAttributes
        | FileSystemRights.WriteAttributes | FileSystemRights.Delete | FileSystemRights.DeleteSubdirectoriesAndFiles
        | FileSystemRights.ChangePermissions | FileSystemRights.TakeOwnership;

    // GENERIC_ALL and GENERIC_WRITE, which the rules a folder passes on to new files can hold.
    private const int GenericChangeRights = 0x10000000 | 0x40000000;

    // Windows' own installer account, which owns and may change system files.
    private const string TrustedInstaller = "S-1-5-80-956008885-3418522649-1831038044-1856292015-2392452016";

    /// <summary>
    /// Another account that may change <paramref name="folder"/> or, with <paramref name="scope"/>
    /// <see cref="AccessScope.FolderAndContents"/>, anything in it, said with what it may change; null when none may.
    /// The rules a folder only passes on to what is made in it later count too.
    /// </summary>
    public static string? OtherWriter(string folder, AccessScope scope) => OtherWriter(folder, scope, LocalAdministrators.Includes);

    /// <summary><see cref="OtherWriter(string, AccessScope)"/>, with <paramref name="administers"/> saying who administers this machine.</summary>
    internal static string? OtherWriter(string folder, AccessScope scope, Func<SecurityIdentifier, bool> administers)
    {
        var trusted = new TrustedAccounts(administers);
        var directory = new DirectoryInfo(folder);
        var entries = scope == AccessScope.FolderAndContents
            ? directory.EnumerateFileSystemInfos("*", new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = 0 })
            : [];
        return new FileSystemInfo[] { directory }.Concat(entries)
            .Select(entry => OtherWriterOf(entry, trusted))
            .FirstOrDefault(writer => writer is not null);
    }

    private static string? OtherWriterOf(FileSystemInfo entry, TrustedAccounts trusted)
    {
        FileSystemSecurity security = entry is DirectoryInfo directory ? directory.GetAccessControl() : ((FileInfo)entry).GetAccessControl();
        if (security.GetOwner(typeof(SecurityIdentifier)) is SecurityIdentifier owner && !trusted.Contains(owner))
        {
            return $"{Name(owner)} owns {entry.FullName}";
        }

        return security.GetAccessRules(includeExplicit: true, includeInherited: true, typeof(SecurityIdentifier))
            .Cast<FileSystemAccessRule>()
            .Where(rule => rule.AccessControlType == AccessControlType.Allow && MayChange(rule)
                           && rule.IdentityReference is SecurityIdentifier sid && !trusted.Contains(sid))
            .Select(rule => $"{Name((SecurityIdentifier)rule.IdentityReference)} may change {entry.FullName}")
            .FirstOrDefault();
    }

    private static bool MayChange(FileSystemAccessRule rule) =>
        (rule.FileSystemRights & ChangeRights) != 0 || ((int)rule.FileSystemRights & GenericChangeRights) != 0;

    // An account the domain no longer knows stays a SID, which names it just as exactly.
    private static string Name(SecurityIdentifier sid) =>
        new IdentityReferenceCollection { sid }.Translate(typeof(NTAccount), forceSuccess: false)[0].Value;

    /// <summary>
    /// This account, SYSTEM, administrators, Windows' installer, and each account that administers this machine. A folder
    /// passes the same permissions on to all it holds, so every other account is looked up once only.
    /// </summary>
    private sealed class TrustedAccounts
    {
        private readonly Func<SecurityIdentifier, bool> _administers;
        private readonly Dictionary<SecurityIdentifier, bool> _known;

        public TrustedAccounts(Func<SecurityIdentifier, bool> administers)
        {
            _administers = administers;
            _known = new Dictionary<SecurityIdentifier, bool>
            {
                [new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null)] = true,
                [new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null)] = true,
                // Stands for whoever makes a file there, which only the accounts here may.
                [new SecurityIdentifier(WellKnownSidType.CreatorOwnerSid, null)] = true,
                [new SecurityIdentifier(TrustedInstaller)] = true
            };
            using var current = WindowsIdentity.GetCurrent();
            if (current.User is { } user)
            {
                _known[user] = true;
            }
        }

        public bool Contains(SecurityIdentifier account)
        {
            if (!_known.TryGetValue(account, out var trusted))
            {
                trusted = _administers(account);
                _known[account] = trusted;
            }

            return trusted;
        }
    }
}

/// <summary>What <see cref="FolderAccess.OtherWriter"/> looks at.</summary>
internal enum AccessScope
{
    /// <summary>The folder itself, with what it passes on to files made in it later.</summary>
    Folder,

    /// <summary>The folder and every file and folder in it.</summary>
    FolderAndContents
}
