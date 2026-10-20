using System.ComponentModel;
using System.Net;
using System.Net.NetworkInformation;
using System.Security.Principal;
using Microsoft.Diagnostics.Tracing.Parsers;
using Microsoft.Diagnostics.Tracing.Session;
using WinOptimizer.Core.Features.Management;
using WinOptimizer.Core.Features.Utilities;

namespace WinOptimizer.Infrastructure.Features.Management;

/// <summary>Owns an explicitly requested, uniquely named real-time kernel network session with no ETL files.</summary>
internal sealed class WindowsProcessNetwork : IProcessNetworkSource
{
    public IProcessNetworkSession Start(ProcessIdentity identity)
    {
        using var current = WindowsIdentity.GetCurrent();
        if (!new WindowsPrincipal(current).IsInRole(WindowsBuiltInRole.Administrator))
            throw new UtilityOperationException("Run win-optimizer as administrator to observe per-process TCP/UDP traffic with Windows ETW.");
        try { return new Session(identity); }
        catch (Exception ex) when (ex is Win32Exception or UnauthorizedAccessException or InvalidOperationException or NetworkInformationException or ApplicationException or System.Runtime.InteropServices.COMException)
        { throw new UtilityOperationException("Windows could not start process network monitoring. " + ex.Message, ex); }
    }

    private sealed class Session : IProcessNetworkSession
    {
        private readonly TraceEventSession _session;
        private readonly ProcessTrafficLedger _ledger;
        private readonly Task _reader;
        private IReadOnlySet<string> _addresses;
        private string _error = "";
        private long _addressRefresh;

        public Session(ProcessIdentity identity)
        {
            _addresses = ReadAddresses();
            _ledger = new(identity, DateTimeOffset.UtcNow);
            _session = new TraceEventSession("win-optimizer-network-" + Guid.NewGuid().ToString("N")) { StopOnDispose = true };
            try
            {
                var kernel = _session.Source.Kernel;
                void Add(int pid, DateTime time, string protocol, bool sent, IPAddress source, IPAddress destination, int size) =>
                    _ledger.Add(pid, new DateTimeOffset(time.ToUniversalTime()), protocol, sent, source, destination, size, Volatile.Read(ref _addresses));
                // TraceEvent fixes ProcessID from the event payload. Header PIDs can belong to a worker thread.
                kernel.TcpIpSend += e => Add(e.ProcessID, e.TimeStamp, "TCP", true, e.saddr, e.daddr, e.size);
                kernel.TcpIpRecv += e => Add(e.ProcessID, e.TimeStamp, "TCP", false, e.saddr, e.daddr, e.size);
                kernel.TcpIpSendIPV6 += e => Add(e.ProcessID, e.TimeStamp, "TCP6", true, e.saddr, e.daddr, e.size);
                kernel.TcpIpRecvIPV6 += e => Add(e.ProcessID, e.TimeStamp, "TCP6", false, e.saddr, e.daddr, e.size);
                kernel.UdpIpSend += e => Add(e.ProcessID, e.TimeStamp, "UDP", true, e.saddr, e.daddr, e.size);
                kernel.UdpIpRecv += e => Add(e.ProcessID, e.TimeStamp, "UDP", false, e.saddr, e.daddr, e.size);
                kernel.UdpIpSendIPV6 += e => Add(e.ProcessID, e.TimeStamp, "UDP6", true, e.saddr, e.daddr, e.size);
                kernel.UdpIpRecvIPV6 += e => Add(e.ProcessID, e.TimeStamp, "UDP6", false, e.saddr, e.daddr, e.size);
                // Send/receive events are counted once. Retransmit/copy events are not added again.
                _session.EnableKernelProvider(KernelTraceEventParser.Keywords.NetworkTCPIP);
                _reader = Task.Factory.StartNew(() =>
                {
                    try { _session.Source.Process(); }
                    catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or IOException or OverflowException or ApplicationException or System.Runtime.InteropServices.COMException)
                    { Volatile.Write(ref _error, ex.Message); }
                }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
            }
            catch { _session.Dispose(); throw; }
        }

        public ProcessNetworkSnapshot Read()
        {
            if (Environment.TickCount64 - _addressRefresh > 5000)
            {
                // Network reconfiguration can introduce new VPN/DHCP addresses during a capture.
                try { Volatile.Write(ref _addresses, ReadAddresses()); _addressRefresh = Environment.TickCount64; }
                catch (NetworkInformationException ex) { Volatile.Write(ref _error, ex.Message); }
            }
            if (_reader.IsCompleted && Volatile.Read(ref _error).Length == 0)
                Volatile.Write(ref _error, "The Windows ETW stream ended before monitoring was stopped.");
            long? lost = null;
            try { lost = _session.EventsLost; }
            catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or ApplicationException)
            { Volatile.Write(ref _error, ex.Message); }
            return _ledger.Snapshot(DateTimeOffset.UtcNow, lost, Volatile.Read(ref _error));
        }

        public async ValueTask DisposeAsync()
        {
            await Task.Run(_session.Dispose).ConfigureAwait(false);
            await _reader.ConfigureAwait(false);
        }

        private static IReadOnlySet<string> ReadAddresses() => NetworkInterface.GetAllNetworkInterfaces()
            .SelectMany(adapter => adapter.GetIPProperties().UnicastAddresses)
            .Select(address => address.Address.IsIPv4MappedToIPv6 ? address.Address.MapToIPv4() : address.Address)
            .Select(address => Convert.ToHexString(address.GetAddressBytes())).ToHashSet(StringComparer.Ordinal);
    }
}
