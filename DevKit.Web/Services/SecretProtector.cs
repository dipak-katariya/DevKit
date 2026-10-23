using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace DevKit.Web.Services;

/// <summary>
/// Encrypts small secrets (the TFS PAT) at rest using Windows DPAPI under the current
/// user account. Implemented via P/Invoke so the app keeps its zero-NuGet-dependency
/// footprint. On non-Windows hosts it degrades to a no-op (the app targets win-x64).
/// Values are tagged with a version marker so legacy plaintext settings still load.
/// </summary>
/// <remarks>
/// v2 values are bound to a context, such as the server the PAT belongs to: decrypting with any
/// other context fails, so a settings file edited to point somewhere else cannot hand the secret
/// over. v1 values, written before that, carry no context and are still read.
/// </remarks>
internal static class SecretProtector
{
    private const string Marker = "enc:v2:";
    private const string LegacyMarker = "enc:v1:";
    private const int CRYPTPROTECT_UI_FORBIDDEN = 0x1;

    private delegate bool DpapiTransform(ref DataBlob dataIn, IntPtr description, IntPtr entropy,
        IntPtr reserved, IntPtr prompt, int flags, ref DataBlob dataOut);

    public static bool IsProtected(string value) =>
        value.StartsWith(Marker, StringComparison.Ordinal) || value.StartsWith(LegacyMarker, StringComparison.Ordinal);

    /// <summary>True for a value already in the current, context-bound format.</summary>
    public static bool IsCurrent(string value) => value.StartsWith(Marker, StringComparison.Ordinal);

    public static string Protect(string plaintext, string context)
    {
        if (string.IsNullOrEmpty(plaintext) || IsProtected(plaintext) || !OperatingSystem.IsWindows())
        {
            return plaintext;
        }

        var cipher = Transform(CryptProtectData, Encoding.UTF8.GetBytes(plaintext), Encoding.UTF8.GetBytes(context));
        return Marker + Convert.ToBase64String(cipher);
    }

    /// <summary>
    /// The plaintext of a protected value; anything unprotected is returned as it is (legacy plaintext).
    /// Throws <see cref="CryptographicException"/> when the value belongs to another user, machine or
    /// context, and <see cref="FormatException"/> when it is damaged.
    /// </summary>
    public static string Unprotect(string stored, string context)
    {
        if (string.IsNullOrEmpty(stored) || !IsProtected(stored))
        {
            return stored;
        }

        if (!OperatingSystem.IsWindows())
        {
            return "";
        }

        var current = IsCurrent(stored);
        var cipher = Convert.FromBase64String(stored[(current ? Marker : LegacyMarker).Length..]);
        var entropy = current ? Encoding.UTF8.GetBytes(context) : null;
        return Encoding.UTF8.GetString(Transform(CryptUnprotectData, cipher, entropy));
    }

    private static byte[] Transform(DpapiTransform transform, byte[] input, byte[]? entropy)
    {
        var inBlob = default(DataBlob);
        var entropyBlob = default(DataBlob);
        var outBlob = default(DataBlob);
        var entropyPointer = IntPtr.Zero;
        try
        {
            inBlob = AllocBlob(input);
            if (entropy is not null)
            {
                entropyBlob = AllocBlob(entropy);
                entropyPointer = Marshal.AllocHGlobal(Marshal.SizeOf<DataBlob>());
                Marshal.StructureToPtr(entropyBlob, entropyPointer, fDeleteOld: false);
            }

            if (!transform(ref inBlob, IntPtr.Zero, entropyPointer, IntPtr.Zero, IntPtr.Zero, CRYPTPROTECT_UI_FORBIDDEN, ref outBlob))
            {
                throw new CryptographicException(Marshal.GetLastWin32Error());
            }

            return ReadBlob(outBlob);
        }
        finally
        {
            FreeHGlobalBlob(ref inBlob);
            FreeHGlobalBlob(ref entropyBlob);
            if (entropyPointer != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(entropyPointer);
            }

            LocalFreeBlob(ref outBlob);
        }
    }

    private static DataBlob AllocBlob(byte[] data)
    {
        var blob = new DataBlob { cbData = data.Length, pbData = Marshal.AllocHGlobal(data.Length) };
        Marshal.Copy(data, 0, blob.pbData, data.Length);
        return blob;
    }

    private static byte[] ReadBlob(DataBlob blob)
    {
        var bytes = new byte[blob.cbData];
        Marshal.Copy(blob.pbData, bytes, 0, blob.cbData);
        return bytes;
    }

    // Memory I allocated with AllocHGlobal must be released with FreeHGlobal.
    private static void FreeHGlobalBlob(ref DataBlob blob)
    {
        if (blob.pbData != IntPtr.Zero) { Marshal.FreeHGlobal(blob.pbData); blob.pbData = IntPtr.Zero; }
    }

    // Memory allocated by the DPAPI call must be released with LocalFree.
    private static void LocalFreeBlob(ref DataBlob blob)
    {
        if (blob.pbData != IntPtr.Zero) { LocalFree(blob.pbData); blob.pbData = IntPtr.Zero; }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DataBlob
    {
        public int cbData;
        public IntPtr pbData;
    }

    [DllImport("crypt32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptProtectData(ref DataBlob pDataIn, IntPtr szDataDescr, IntPtr pOptionalEntropy, IntPtr pvReserved, IntPtr pPromptStruct, int dwFlags, ref DataBlob pDataOut);

    [DllImport("crypt32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptUnprotectData(ref DataBlob pDataIn, IntPtr ppszDataDescr, IntPtr pOptionalEntropy, IntPtr pvReserved, IntPtr pPromptStruct, int dwFlags, ref DataBlob pDataOut);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr hMem);
}

/// <summary>How a settings store keeps the PAT inside the settings text it holds. Either way it is read with <see cref="SecretProtector.Unprotect"/>.</summary>
internal abstract class PatAtRest
{
    /// <summary>
    /// Encrypted on its own with DPAPI, for the Windows account and machine DevKit runs on and for its TFS server: the
    /// settings file, which is plain text to anyone who can open it.
    /// </summary>
    public static readonly PatAtRest Dpapi = new DpapiPat();

    /// <summary>
    /// As it is, inside text the store encrypts as a whole with a key of its own (<see cref="ClientVault"/>). DPAPI would
    /// tie it to this machine, and the settings in a person's browser are read by every DevKit server running as the
    /// same account.
    /// </summary>
    public static readonly PatAtRest InSealedText = new SealedTextPat();

    public abstract string Protect(string pat, string context);

    /// <summary>Whether a stored PAT is already in the form this store writes, so it needs no saving again.</summary>
    public abstract bool IsCurrent(string stored);

    private sealed class DpapiPat : PatAtRest
    {
        public override string Protect(string pat, string context) => SecretProtector.Protect(pat, context);

        public override bool IsCurrent(string stored) => SecretProtector.IsCurrent(stored);
    }

    private sealed class SealedTextPat : PatAtRest
    {
        public override string Protect(string pat, string context) => pat;

        // One still encrypted with DPAPI came from the server or an earlier browser copy, and only this machine reads it.
        public override bool IsCurrent(string stored) => !SecretProtector.IsProtected(stored);
    }
}
