using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;

namespace DevKit.Web.Services;

/// <summary>
/// What a Windows access token says of its account: who it is, and the groups it counts as a member of. Read from the
/// token alone; nothing runs as the account.
/// </summary>
[SupportedOSPlatform("windows")]
internal static class WindowsTokens
{
    private const int TokenUser = 1;
    private const int TokenGroups = 2;

    // A group counts as Windows counts it for access: enabled, and neither the logon session nor one used to deny only.
    private const uint Enabled = 0x4;
    private const uint UseForDenyOnly = 0x10;
    private const uint LogonId = 0xC0000000;

    /// <summary>The account <paramref name="token"/> belongs to; null when Windows does not say.</summary>
    public static SecurityIdentifier? User(SafeAccessTokenHandle token) =>
        Read(token, TokenUser, buffer => new SecurityIdentifier(Marshal.ReadIntPtr(buffer)));

    /// <summary>The groups <paramref name="token"/>'s account is a member of; empty when Windows does not say.</summary>
    public static IReadOnlyList<SecurityIdentifier> Groups(SafeAccessTokenHandle token) =>
        Read(token, TokenGroups, ReadGroups) ?? [];

    private static List<SecurityIdentifier> ReadGroups(IntPtr buffer)
    {
        var count = Marshal.ReadInt32(buffer);
        var first = Marshal.OffsetOf<GroupsHeader>(nameof(GroupsHeader.First));
        var size = Marshal.SizeOf<SidAndAttributes>();
        var groups = new List<SecurityIdentifier>(count);
        for (var index = 0; index < count; index++)
        {
            var group = Marshal.PtrToStructure<SidAndAttributes>(buffer + first + (index * size));
            if ((group.Attributes & (Enabled | UseForDenyOnly | LogonId)) == Enabled)
            {
                groups.Add(new SecurityIdentifier(group.Sid));
            }
        }

        return groups;
    }

    /// <summary>Asks for one kind of information about <paramref name="token"/> and reads it while its buffer lasts.</summary>
    private static T? Read<T>(SafeAccessTokenHandle token, int kind, Func<IntPtr, T> read)
        where T : class
    {
        GetTokenInformation(token, kind, IntPtr.Zero, 0, out var needed);
        if (needed <= 0)
        {
            return null;
        }

        var buffer = Marshal.AllocHGlobal(needed);
        try
        {
            return GetTokenInformation(token, kind, buffer, needed, out _) ? read(buffer) : null;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SidAndAttributes
    {
        public IntPtr Sid;
        public uint Attributes;
    }

    // TOKEN_GROUPS: a count, then the groups, aligned as Windows aligns them.
    [StructLayout(LayoutKind.Sequential)]
    private struct GroupsHeader
    {
        public uint GroupCount;
        public SidAndAttributes First;
    }

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetTokenInformation(SafeAccessTokenHandle token, int informationClass, IntPtr information, int length, out int returnLength);
}
