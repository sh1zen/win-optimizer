using System.Net;
using WinOptimizer.Core.Features.Management;
using WinOptimizer.Infrastructure.Features.Management;
using Xunit;

namespace WinOptimizer.Infrastructure.Tests.Utilities;

/// <summary>Checks ETW attribution without opening a trace session or sending any network traffic.</summary>
public sealed class ProcessTrafficLedgerTests
{
    private static readonly DateTimeOffset At = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);
    private static readonly ProcessIdentity Identity = new(42, At.AddMinutes(-1).UtcTicks);
    private static readonly IPAddress Local = IPAddress.Parse("192.0.2.10");
    private static readonly IPAddress Remote = IPAddress.Parse("198.51.100.20");
    private static IReadOnlySet<string> Addresses(params IPAddress[] addresses) => addresses.Select(address => Convert.ToHexString(address.GetAddressBytes())).ToHashSet();

    [Fact]
    public void SendAndReceiveAccumulateByRemoteIpForEitherTupleOrientation()
    {
        var ledger = new ProcessTrafficLedger(Identity, At);
        ledger.Add(42, At, "TCP", true, Local, Remote, 100, Addresses(Local));
        ledger.Add(42, At.AddSeconds(1), "TCP", false, Remote, Local, 200, Addresses(Local));
        ledger.Add(42, At.AddSeconds(2), "TCP", false, Local, Remote, 300, Addresses(Local));
        var host = Assert.Single(ledger.Snapshot(At.AddSeconds(3), 0, "").Hosts);
        Assert.Equal(Remote.ToString(), host.RemoteAddress); Assert.Equal(100UL, host.SentBytes); Assert.Equal(500UL, host.ReceivedBytes); Assert.Equal(3, host.Events);
    }

    [Fact]
    public void ForeignPidsOldEventsAndInvalidLengthsAreExcluded()
    {
        var ledger = new ProcessTrafficLedger(Identity, At);
        ledger.Add(10, At, "TCP", true, Local, Remote, 100, Addresses(Local));
        ledger.Add(42, At.AddSeconds(-1), "TCP", true, Local, Remote, 100, Addresses(Local));
        ledger.Add(42, At, "TCP", true, Local, Remote, -1, Addresses(Local));
        Assert.Empty(ledger.Snapshot(At, 0, "").Hosts);
    }

    [Fact]
    public void IPv6AndUdpUseTheSameExplicitDirectionAccounting()
    {
        var local = IPAddress.Parse("2001:db8::1"); var remote = IPAddress.Parse("2001:db8::2");
        var ledger = new ProcessTrafficLedger(Identity, At);
        ledger.Add(42, At, "UDP6", false, remote, local, 2048, Addresses(local));
        var host = Assert.Single(ledger.Snapshot(At, 0, "").Hosts);
        Assert.Equal("UDP6", host.Protocol); Assert.Equal(remote.ToString(), host.RemoteAddress); Assert.Equal(2048UL, host.ReceivedBytes);
    }

    [Fact]
    public void AmbiguousAddressesAreReportedInsteadOfInventingARemotePeer()
    {
        var ledger = new ProcessTrafficLedger(Identity, At);
        ledger.Add(42, At, "TCP", true, Local, Remote, 100, Addresses());
        ledger.Add(42, At, "TCP", true, Local, Remote, 100, Addresses(Local, Remote));
        var snapshot = ledger.Snapshot(At, 0, ""); Assert.Empty(snapshot.Hosts); Assert.Equal(2, snapshot.UnattributedEvents);
    }

    [Fact]
    public void IdenticalLoopbackAddressesHaveAnUnambiguousIpGroup()
    {
        var ledger = new ProcessTrafficLedger(Identity, At);
        ledger.Add(42, At, "TCP", true, IPAddress.Loopback, IPAddress.Loopback, 512, Addresses());
        Assert.Equal("127.0.0.1", Assert.Single(ledger.Snapshot(At, 0, "").Hosts).RemoteAddress);
    }

    [Fact]
    public void CapacityLimitsAndEventLossRemainVisible()
    {
        var ledger = new ProcessTrafficLedger(Identity, At, capacity: 1);
        ledger.Add(42, At, "TCP", true, Local, Remote, 100, Addresses(Local));
        ledger.Add(42, At, "UDP", true, Local, Remote, 200, Addresses(Local));
        var snapshot = ledger.Snapshot(At, 7, "");
        Assert.Single(snapshot.Hosts); Assert.True(snapshot.CapacityReached); Assert.Equal(1, snapshot.UnattributedEvents); Assert.Equal(7, snapshot.LostEvents);
    }

    [Fact]
    public void AnUnavailableLossCounterIsNotReportedAsZero()
    {
        var snapshot = new ProcessTrafficLedger(Identity, At).Snapshot(At, null, "Session unavailable");
        Assert.Null(snapshot.LostEvents); Assert.Equal(ProcessNetworkState.Failed, snapshot.State); Assert.Equal("Session unavailable", snapshot.Diagnostic);
    }
}
