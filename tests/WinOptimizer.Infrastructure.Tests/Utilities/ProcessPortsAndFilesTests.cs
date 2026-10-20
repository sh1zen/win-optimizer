using System.Buffers.Binary;
using System.Net;
using WinOptimizer.Core.Features.Utilities;
using WinOptimizer.Infrastructure.Features.Management;
using Xunit;

namespace WinOptimizer.Infrastructure.Tests.Utilities;

/// <summary>Exercises native table layouts and Handle CSV with synthetic bytes and paths only.</summary>
public sealed class ProcessPortsAndFilesTests
{
    [Fact]
    public void TcpV4ReadsNetworkOrderPortsAndOwningPid()
    {
        var bytes = new byte[28]; Write(bytes, 0, 1); Write(bytes, 4, 5);
        IPAddress.Parse("192.0.2.10").GetAddressBytes().CopyTo(bytes, 8);
        BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(12), 55000);
        IPAddress.Parse("198.51.100.20").GetAddressBytes().CopyTo(bytes, 16);
        BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(20), 443); Write(bytes, 24, 42);
        var port = Assert.Single(WindowsProcessPorts.Parse(bytes, false, true, 42));
        Assert.Equal("192.0.2.10", port.LocalAddress); Assert.Equal(55000, port.LocalPort);
        Assert.Equal("198.51.100.20", port.RemoteAddress); Assert.Equal(443, port.RemotePort); Assert.Equal("Established", port.State);
        Assert.Empty(WindowsProcessPorts.Parse(bytes, false, true, 99));
    }

    [Fact]
    public void TcpV6KeepsScopesAndRemotePorts()
    {
        var bytes = new byte[60]; Write(bytes, 0, 1);
        IPAddress.Parse("fe80::1").GetAddressBytes().CopyTo(bytes, 4); Write(bytes, 20, 7);
        BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(24), 1234);
        IPAddress.Parse("fe80::2").GetAddressBytes().CopyTo(bytes, 28); Write(bytes, 44, 7);
        BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(48), 443); Write(bytes, 52, 5); Write(bytes, 56, 42);
        var port = Assert.Single(WindowsProcessPorts.Parse(bytes, true, true, 42));
        Assert.Equal("fe80::1%7", port.LocalAddress); Assert.Equal("fe80::2%7", port.RemoteAddress); Assert.Equal(443, port.RemotePort);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void UdpBindingsNeverInventARemoteEndpoint(bool ipv6)
    {
        var bytes = new byte[ipv6 ? 32 : 16]; Write(bytes, 0, 1);
        BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(ipv6 ? 24 : 8), 5353);
        Write(bytes, ipv6 ? 28 : 12, 42);
        var port = Assert.Single(WindowsProcessPorts.Parse(bytes, ipv6, false, 42));
        Assert.Equal(5353, port.LocalPort); Assert.Empty(port.RemoteAddress); Assert.Null(port.RemotePort); Assert.Equal("Bound", port.State);
    }

    [Fact]
    public void TruncatedNativeTablesFailBeforeReadingAnInvalidRow()
    {
        Assert.Throws<InvalidDataException>(() => WindowsProcessPorts.Parse([1, 0], false, true, 42));
        Assert.Throws<InvalidDataException>(() => WindowsProcessPorts.Parse([255, 255, 255, 255], true, true, 42));
    }

    [Fact]
    public void HandleCsvPreservesQuotedPathsAndFiltersForeignPidsAndNonFiles()
    {
        const string csv = "Process,PID,Type,Handle,Name\nExample.exe,42,File,1A,\"C:\\example, folder\\file.txt\"\nOther.exe,99,File,20,C:\\other.txt\nExample.exe,42,Key,22,RegistryKey";
        var file = Assert.Single(WindowsProcessFiles.Parse(csv, 42));
        Assert.Equal("C:\\example, folder\\file.txt", file.Path); Assert.Equal("1A", file.Handle);
    }

    [Theory]
    [InlineData("")] [InlineData("Access is denied")] [InlineData("Process,PID,Type,Handle,Name\nx,invalid,File,10,C:\\x")]
    [InlineData("Process,PID,Type,Handle,Name\nx,42,File")]
    public void MalformedHandleOutputDoesNotPretendTheProcessHasNoFiles(string csv) =>
        Assert.Throws<UtilityOperationException>(() => WindowsProcessFiles.Parse(csv, 42));

    private static void Write(byte[] bytes, int offset, uint value) => BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(offset), value);
}
