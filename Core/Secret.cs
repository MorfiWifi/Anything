using System;
using System.Runtime.InteropServices;
using System.Text;

// DPAPI via crypt32 P/Invoke (System.Security.Cryptography.ProtectedData is a
// NuGet package; the project stays dependency-free). Machine scope so the
// LocalSystem service can decrypt at boot with no user profile loaded.
internal static class Secret
{
    private const int CryptProtectLocalMachine = 0x4;

    [StructLayout(LayoutKind.Sequential)]
    private struct DataBlob
    {
        public int cbData;
        public IntPtr pbData;
    }

    [DllImport("crypt32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CryptProtectData(ref DataBlob dataIn, string? description,
        IntPtr entropy, IntPtr reserved, IntPtr promptStruct, int flags, out DataBlob dataOut);

    [DllImport("crypt32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CryptUnprotectData(ref DataBlob dataIn, IntPtr description,
        IntPtr entropy, IntPtr reserved, IntPtr promptStruct, int flags, out DataBlob dataOut);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr hMem);

    public static string Protect(string plain)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(plain);
        var input = new DataBlob { cbData = bytes.Length, pbData = Marshal.AllocHGlobal(bytes.Length) };
        try
        {
            Marshal.Copy(bytes, 0, input.pbData, bytes.Length);
            if (!CryptProtectData(ref input, "WifiShare", IntPtr.Zero, IntPtr.Zero, IntPtr.Zero,
                    CryptProtectLocalMachine, out DataBlob output))
                throw new InvalidOperationException("CryptProtectData failed (Win32 " + Marshal.GetLastWin32Error() + ").");
            try
            {
                var protectedBytes = new byte[output.cbData];
                Marshal.Copy(output.pbData, protectedBytes, 0, output.cbData);
                return Convert.ToBase64String(protectedBytes);
            }
            finally { LocalFree(output.pbData); }
        }
        finally { Marshal.FreeHGlobal(input.pbData); }
    }

    public static string Unprotect(string base64)
    {
        byte[] bytes = Convert.FromBase64String(base64);
        var input = new DataBlob { cbData = bytes.Length, pbData = Marshal.AllocHGlobal(bytes.Length) };
        try
        {
            Marshal.Copy(bytes, 0, input.pbData, bytes.Length);
            if (!CryptUnprotectData(ref input, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero,
                    CryptProtectLocalMachine, out DataBlob output))
                throw new InvalidOperationException("CryptUnprotectData failed (Win32 " + Marshal.GetLastWin32Error() + ").");
            try
            {
                var plain = new byte[output.cbData];
                Marshal.Copy(output.pbData, plain, 0, output.cbData);
                return Encoding.UTF8.GetString(plain);
            }
            finally { LocalFree(output.pbData); }
        }
        finally { Marshal.FreeHGlobal(input.pbData); }
    }
}