using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Principal;

namespace DevKit.Web.Services;

/// <summary>
/// Whether an account or group administers this machine: a member of its Administrators group, directly or through a
/// domain group in it, as Domain Admins. An administrator may take any file over anyway, so a permission one holds on
/// DevKit's folders gives it nothing more (<see cref="FolderAccess"/>). An account Windows or the domain cannot answer
/// for is not taken for one.
/// </summary>
[SupportedOSPlatform("windows")]
internal static class LocalAdministrators
{
    private const int Success = 0;
    private const int MaxPreferredLength = -1;

    // LG_INCLUDE_INDIRECT: also the local groups a user is in through a domain group.
    private const int IncludeIndirect = 1;

    /// <summary>Whether <paramref name="account"/> is a member of this machine's Administrators group, directly or through a group.</summary>
    public static bool Includes(SecurityIdentifier account)
    {
        ArgumentNullException.ThrowIfNull(account);
        if (NameOf(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null)) is not { } administrators)
        {
            return false;
        }

        // Windows names the group in the machine's own language, as BUILTIN\Administratoren.
        var group = administrators[(administrators.IndexOf('\\') + 1)..];
        return DirectMembers(group).Contains(account)
               || (NameOf(account) is { } name && LocalGroupsOf(name).Contains(group, StringComparer.OrdinalIgnoreCase));
    }

    private static string? NameOf(SecurityIdentifier sid) =>
        new IdentityReferenceCollection { sid }.Translate(typeof(NTAccount), forceSuccess: false)[0] is NTAccount account
            ? account.Value
            : null;

    /// <summary>The accounts and groups <paramref name="group"/> lists itself; empty when Windows does not say.</summary>
    private static HashSet<SecurityIdentifier> DirectMembers(string group)
    {
        var status = NetLocalGroupGetMembers(null, group, 0, out var buffer, MaxPreferredLength, out var read, out _, IntPtr.Zero);
        try
        {
            // LOCALGROUP_MEMBERS_INFO_0: one pointer to a SID per member.
            return status == Success
                ? [.. Enumerable.Range(0, read).Select(index => new SecurityIdentifier(Marshal.ReadIntPtr(buffer, index * IntPtr.Size)))]
                : [];
        }
        finally
        {
            Free(buffer);
        }
    }

    /// <summary>The names of the local groups user <paramref name="name"/> is in, through domain groups too; empty when it is no user.</summary>
    private static string[] LocalGroupsOf(string name)
    {
        var status = NetUserGetLocalGroups(null, name, 0, IncludeIndirect, out var buffer, MaxPreferredLength, out var read, out _);
        try
        {
            // LOCALGROUP_USERS_INFO_0: one pointer to a group name per group.
            return status == Success
                ? [.. Enumerable.Range(0, read).Select(index => Marshal.PtrToStringUni(Marshal.ReadIntPtr(buffer, index * IntPtr.Size)) ?? "")]
                : [];
        }
        finally
        {
            Free(buffer);
        }
    }

    private static void Free(IntPtr buffer)
    {
        if (buffer != IntPtr.Zero)
        {
            NetApiBufferFree(buffer);
        }
    }

    [DllImport("netapi32.dll", CharSet = CharSet.Unicode)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern int NetLocalGroupGetMembers(string? serverName, string groupName, int level, out IntPtr buffer,
        int preferredMaximumLength, out int entriesRead, out int totalEntries, IntPtr resumeHandle);

    [DllImport("netapi32.dll", CharSet = CharSet.Unicode)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern int NetUserGetLocalGroups(string? serverName, string userName, int level, int flags, out IntPtr buffer,
        int preferredMaximumLength, out int entriesRead, out int totalEntries);

    [DllImport("netapi32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern int NetApiBufferFree(IntPtr buffer);
}
