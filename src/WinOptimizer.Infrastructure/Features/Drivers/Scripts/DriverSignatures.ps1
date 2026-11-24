# An owned worker releases the native catalog handles when it exits.
Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;

public static class DriverSignatureQuery
{
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct Signer
    {
        public uint Size;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string Catalog;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string Publisher;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string Version;
        public uint Score;
    }

    [DllImport("setupapi.dll", EntryPoint = "SetupVerifyInfFileW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool Verify(string path, IntPtr platform, ref Signer signer);

    public static object Inspect(string path)
    {
        var signer = new Signer { Size = (uint)Marshal.SizeOf(typeof(Signer)) };
        bool valid = Verify(path, IntPtr.Zero, ref signer);
        return new { Path = path, Catalog = signer.Catalog ?? "", Publisher = signer.Publisher ?? "", Score = signer.Score, Valid = valid };
    }
}
'@

$items = foreach ($path in (Get-Content -LiteralPath $request -Raw -Encoding UTF8 | ConvertFrom-Json)) {
    [DriverSignatureQuery]::Inspect([string]$path)
}
@{ Items = @($items) } | ConvertTo-Json -Depth 4 -Compress
