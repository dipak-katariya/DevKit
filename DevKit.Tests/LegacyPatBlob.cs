using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace DevKit.Tests;

/// <summary>
/// A PAT protected the way builds before the server-bound format stored it: DPAPI for the current
/// user with no entropy, marked "enc:v1:". DPAPI output is per user and machine, so it cannot be a fixture.
/// </summary>
internal static class LegacyPatBlob
{
    private const string Marker = "enc:v1:";
    private const int UiForbidden = 0x1;

    public static string Protect(string plaintext)
    {
        var input = Encoding.UTF8.GetBytes(plaintext);
        var inBlob = new DataBlob { Size = input.Length, Data = Marshal.AllocHGlobal(input.Length) };
        var outBlob = default(DataBlob);
        try
        {
            Marshal.Copy(input, 0, inBlob.Data, input.Length);
            if (!CryptProtectData(ref inBlob, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, UiForbidden, ref outBlob))
            {
                throw new CryptographicException(Marshal.GetLastWin32Error());
            }

            var cipher = new byte[outBlob.Size];
            Marshal.Copy(outBlob.Data, cipher, 0, outBlob.Size);
            return Marker + Convert.ToBase64String(cipher);
        }
        finally
        {
            Marshal.FreeHGlobal(inBlob.Data);
            if (outBlob.Data != IntPtr.Zero)
            {
                LocalFree(outBlob.Data);
            }
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DataBlob
    {
        public int Size;
        public IntPtr Data;
    }

    [DllImport("crypt32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptProtectData(ref DataBlob dataIn, IntPtr description, IntPtr entropy, IntPtr reserved, IntPtr prompt, int flags, ref DataBlob dataOut);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr memory);
}
