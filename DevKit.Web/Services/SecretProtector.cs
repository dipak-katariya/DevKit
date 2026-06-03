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
internal static class SecretProtector
{
    private const string Marker = "enc:v1:";
    private const int CRYPTPROTECT_UI_FORBIDDEN = 0x1;

    public static bool IsProtected(string value) => value.StartsWith(Marker, StringComparison.Ordinal);

    public static string Protect(string plaintext)
    {
        if (string.IsNullOrEmpty(plaintext) || IsProtected(plaintext)) return plaintext;
        if (!OperatingSystem.IsWindows()) return plaintext;

        var inBlob = default(DATA_BLOB);
        var outBlob = default(DATA_BLOB);
        try
        {
            inBlob = AllocBlob(Encoding.UTF8.GetBytes(plaintext));
            if (!CryptProtectData(ref inBlob, null, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, CRYPTPROTECT_UI_FORBIDDEN, ref outBlob))
                throw new CryptographicException(Marshal.GetLastWin32Error());
            return Marker + Convert.ToBase64String(ReadBlob(outBlob));
        }
        finally
        {
            FreeHGlobalBlob(ref inBlob);
            LocalFreeBlob(ref outBlob);
        }
    }

    public static string Unprotect(string stored)
    {
        if (string.IsNullOrEmpty(stored) || !IsProtected(stored)) return stored; // legacy plaintext
        if (!OperatingSystem.IsWindows()) return "";

        var cipher = Convert.FromBase64String(stored[Marker.Length..]);
        var inBlob = default(DATA_BLOB);
        var outBlob = default(DATA_BLOB);
        try
        {
            inBlob = AllocBlob(cipher);
            if (!CryptUnprotectData(ref inBlob, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, CRYPTPROTECT_UI_FORBIDDEN, ref outBlob))
                throw new CryptographicException(Marshal.GetLastWin32Error());
            return Encoding.UTF8.GetString(ReadBlob(outBlob));
        }
        finally
        {
            FreeHGlobalBlob(ref inBlob);
            LocalFreeBlob(ref outBlob);
        }
    }

    private static DATA_BLOB AllocBlob(byte[] data)
    {
        var blob = new DATA_BLOB { cbData = data.Length, pbData = Marshal.AllocHGlobal(data.Length) };
        Marshal.Copy(data, 0, blob.pbData, data.Length);
        return blob;
    }

    private static byte[] ReadBlob(DATA_BLOB blob)
    {
        var bytes = new byte[blob.cbData];
        Marshal.Copy(blob.pbData, bytes, 0, blob.cbData);
        return bytes;
    }

    // Memory I allocated with AllocHGlobal must be released with FreeHGlobal.
    private static void FreeHGlobalBlob(ref DATA_BLOB blob)
    {
        if (blob.pbData != IntPtr.Zero) { Marshal.FreeHGlobal(blob.pbData); blob.pbData = IntPtr.Zero; }
    }

    // Memory allocated by the DPAPI call must be released with LocalFree.
    private static void LocalFreeBlob(ref DATA_BLOB blob)
    {
        if (blob.pbData != IntPtr.Zero) { LocalFree(blob.pbData); blob.pbData = IntPtr.Zero; }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DATA_BLOB
    {
        public int cbData;
        public IntPtr pbData;
    }

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptProtectData(ref DATA_BLOB pDataIn, string? szDataDescr, IntPtr pOptionalEntropy, IntPtr pvReserved, IntPtr pPromptStruct, int dwFlags, ref DATA_BLOB pDataOut);

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptUnprotectData(ref DATA_BLOB pDataIn, IntPtr ppszDataDescr, IntPtr pOptionalEntropy, IntPtr pvReserved, IntPtr pPromptStruct, int dwFlags, ref DATA_BLOB pDataOut);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr hMem);
}
