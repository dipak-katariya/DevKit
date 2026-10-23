using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace DevKit.Web.Services;

/// <summary>Locks the key of each person's settings (<see cref="ClientVault"/>) so that only DevKit can open it.</summary>
internal interface IKeyLock
{
    private const int AccountHashBytes = 4;

    /// <summary>Which account's keys this lock opens, safe to show and log: a hash, never the account.</summary>
    string Account { get; }

    /// <summary>How <see cref="Account"/> names the account with security identifier <paramref name="accountId"/>.</summary>
    static string AccountHash(string accountId) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(accountId)), 0, AccountHashBytes).ToLowerInvariant();

    /// <summary>The key, locked. Throws <see cref="IOException"/> when it cannot be locked now.</summary>
    byte[] Lock(byte[] key);

    /// <summary>
    /// The key <paramref name="locked"/> holds. Throws <see cref="CryptographicException"/> when it is not a locked key,
    /// and <see cref="IOException"/> when it cannot be opened now.
    /// </summary>
    byte[] Unlock(byte[] locked);
}

/// <summary>
/// Locks keys to the Windows account DevKit runs as through the domain (DPAPI-NG). Any machine in the domain where
/// DevKit runs as that account opens them, so nothing is copied between servers, and no other account can. Opening one
/// asks a domain controller, or uses the keys Windows already has from one.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class DomainKeyLock : IKeyLock
{
    private const int SilentFlag = 0x40;
    private const int CheckKeyBytes = 32;

    // A blob that does not even parse as a locked key; every other failure may be the domain not answering.
    private const int FirstMalformedError = unchecked((int)0x80093100);
    private const int LastMalformedError = unchecked((int)0x800931FF);

    private readonly string _descriptor;

    /// <param name="accountId">The security identifier of the account the keys are locked to.</param>
    public DomainKeyLock(string accountId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(accountId);
        _descriptor = "SID=" + accountId;
        Account = IKeyLock.AccountHash(accountId);
    }

    public string Account { get; }

    /// <summary>
    /// The lock for <paramref name="owner"/>, the account DevKit runs as, tried once so that the log says at start
    /// whether the domain locks keys for it. DevKit starts either way, since a domain controller that is down now may
    /// answer in a minute; until it does, nobody's settings are opened or saved, and none are overwritten.
    /// </summary>
    public static DomainKeyLock For(OwnerAccount owner, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(owner);
        ArgumentNullException.ThrowIfNull(logger);
        var keyLock = new DomainKeyLock(owner.Id);
        try
        {
            keyLock.Unlock(keyLock.Lock(RandomNumberGenerator.GetBytes(CheckKeyBytes)));
        }
        catch (Exception ex) when (ex is IOException or CryptographicException)
        {
            logger.LogError(ex,
                "The domain did not lock and open a key for the account DevKit runs as ({Account}), so nobody's settings can be opened or saved until it does. Shared DevKit runs as a domain account, on a machine in the domain",
                keyLock.Account);
        }

        return keyLock;
    }

    public byte[] Lock(byte[] key)
    {
        ArgumentNullException.ThrowIfNull(key);
        var status = NCryptCreateProtectionDescriptor(_descriptor, 0, out var descriptor);
        using (descriptor)
        {
            if (status == 0)
            {
                status = NCryptProtectSecret(descriptor, SilentFlag, key, key.Length, IntPtr.Zero, IntPtr.Zero, out var blob, out var length);
                if (status == 0)
                {
                    return TakeBytes(blob, length);
                }
            }
        }

        throw new IOException($"The domain did not lock a key for the account DevKit runs as (0x{status:X8}).");
    }

    public byte[] Unlock(byte[] locked)
    {
        ArgumentNullException.ThrowIfNull(locked);
        var status = NCryptUnprotectSecret(IntPtr.Zero, SilentFlag, locked, locked.Length, IntPtr.Zero, IntPtr.Zero, out var key, out var length);
        if (status == 0)
        {
            return TakeBytes(key, length);
        }

        if (status is >= FirstMalformedError and <= LastMalformedError)
        {
            throw new CryptographicException($"This is not a key DevKit locked (0x{status:X8}).");
        }

        throw new IOException($"The domain did not open the key (0x{status:X8}).");
    }

    /// <summary>Copies what Windows allocated, wipes it, since it may be a key, and frees it.</summary>
    private static byte[] TakeBytes(IntPtr memory, int length)
    {
        var bytes = new byte[length];
        Marshal.Copy(memory, bytes, 0, length);
        Marshal.Copy(new byte[length], 0, memory, length);
        LocalFree(memory);
        return bytes;
    }

    private sealed class DescriptorHandle : SafeHandleZeroOrMinusOneIsInvalid
    {
        public DescriptorHandle()
            : base(ownsHandle: true)
        {
        }

        protected override bool ReleaseHandle() => NCryptCloseProtectionDescriptor(handle) == 0;
    }

    [DllImport("ncrypt.dll", CharSet = CharSet.Unicode)]
    private static extern int NCryptCreateProtectionDescriptor(string descriptorString, int flags, out DescriptorHandle descriptor);

    [DllImport("ncrypt.dll")]
    private static extern int NCryptCloseProtectionDescriptor(IntPtr descriptor);

    [DllImport("ncrypt.dll")]
    private static extern int NCryptProtectSecret(DescriptorHandle descriptor, int flags, byte[] data, int dataLength,
        IntPtr memoryPara, IntPtr window, out IntPtr protectedBlob, out int protectedLength);

    [DllImport("ncrypt.dll")]
    private static extern int NCryptUnprotectSecret(IntPtr descriptor, int flags, byte[] protectedBlob, int protectedLength,
        IntPtr memoryPara, IntPtr window, out IntPtr data, out int dataLength);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr memory);
}
