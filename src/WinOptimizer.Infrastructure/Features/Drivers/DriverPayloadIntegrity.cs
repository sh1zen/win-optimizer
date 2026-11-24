using System.Runtime.InteropServices;
using System.Security.Cryptography;
using WinOptimizer.Core.Features.Drivers.Models;

namespace WinOptimizer.Infrastructure.Features.Drivers;

/// <summary>Checks each published digest and verifies executable signatures without launching files.</summary>
internal static class DriverPayloadIntegrity
{
    private static readonly Guid Authenticode = new("00AAC56B-CD44-11d0-8CC2-00C04FC295EE");

    internal static void Check(byte[] bytes, DriverHash[] hashes)
    {
        using var stream = new MemoryStream(bytes, writable: false);
        CheckAsync(stream, hashes, CancellationToken.None).GetAwaiter().GetResult();
    }

    internal static async Task<string> CheckAsync(Stream stream, DriverHash[] expected, CancellationToken ct)
    {
        var algorithms = expected.Select(hash => Name(hash.Algorithm)).Append(HashAlgorithmName.SHA256).Distinct().ToArray();
        var digests = algorithms.ToDictionary(name => name, IncrementalHash.CreateHash);
        try
        {
            var buffer = new byte[128 * 1024];
            int read;
            while ((read = await stream.ReadAsync(buffer, ct).ConfigureAwait(false)) != 0)
                foreach (var digest in digests.Values) digest.AppendData(buffer, 0, read);
            var values = digests.ToDictionary(pair => pair.Key, pair => pair.Value.GetHashAndReset());
            foreach (var hash in expected)
            {
                var calculated = values[Name(hash.Algorithm)];
                byte[] declared;
                try
                {
                    var value = hash.Value.Trim();
                    declared = value.Length == calculated.Length * 2 && value.All(Uri.IsHexDigit)
                        ? Convert.FromHexString(value) : Convert.FromBase64String(value);
                }
                catch (FormatException ex) { throw new DriverScanException($"The source published an invalid {hash.Algorithm} digest.", ex); }
                if (!CryptographicOperations.FixedTimeEquals(calculated, declared))
                    throw new DriverScanException($"The package does not match the published {hash.Algorithm} digest.");
            }
            return Convert.ToHexString(values[HashAlgorithmName.SHA256]);
        }
        finally { foreach (var digest in digests.Values) digest.Dispose(); }
    }

    private static HashAlgorithmName Name(string value) => value.Replace("-", "").ToUpperInvariant() switch
    {
        "SHA256" => HashAlgorithmName.SHA256, "SHA512" => HashAlgorithmName.SHA512,
        "SHA384" => HashAlgorithmName.SHA384, "SHA1" => HashAlgorithmName.SHA1, "MD5" => HashAlgorithmName.MD5,
        _ => throw new DriverScanException($"Unsupported published driver hash algorithm: {value}.")
    };

    internal static bool VerifyExecutableSignature(string path)
    {
        var file = new TrustFile { Size = (uint)Marshal.SizeOf<TrustFile>(), Path = path };
        var pointer = Marshal.AllocHGlobal(Marshal.SizeOf<TrustFile>());
        Marshal.StructureToPtr(file, pointer, false);
        var data = new TrustData
        {
            Size = (uint)Marshal.SizeOf<TrustData>(), UiChoice = 2, UnionChoice = 1, File = pointer,
            StateAction = 1, ProviderFlags = 0x1000 // Use cached trust data; the source HTTP boundary owns downloads.
        };
        try
        {
            var action = Authenticode;
            return WinVerifyTrust(new IntPtr(-1), ref action, ref data) == 0;
        }
        finally
        {
            var action = Authenticode;
            data.StateAction = 2;
            WinVerifyTrust(new IntPtr(-1), ref action, ref data);
            Marshal.DestroyStructure<TrustFile>(pointer);
            Marshal.FreeHGlobal(pointer);
        }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct TrustFile
    {
        public uint Size;
        [MarshalAs(UnmanagedType.LPWStr)] public string Path;
        public IntPtr Handle;
        public IntPtr KnownSubject;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct TrustData
    {
        public uint Size;
        public IntPtr PolicyCallback;
        public IntPtr SipClient;
        public uint UiChoice;
        public uint RevocationChecks;
        public uint UnionChoice;
        public IntPtr File;
        public uint StateAction;
        public IntPtr StateData;
        public IntPtr UrlReference;
        public uint ProviderFlags;
        public uint UiContext;
        public IntPtr SignatureSettings;
    }

    [DllImport("wintrust.dll", ExactSpelling = true)]
    private static extern int WinVerifyTrust(IntPtr window, ref Guid action, ref TrustData data);
}
