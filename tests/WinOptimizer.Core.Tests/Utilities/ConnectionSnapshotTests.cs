using WinOptimizer.Core.Features.Utilities;
using Xunit;

namespace WinOptimizer.Core.Tests.Utilities;

public sealed class ConnectionSnapshotTests
{
    [Fact]
    public void TotalsIncludeDisconnectedAndVirtualAdaptersAndKeepMissingCountersExplicit()
    {
        ConnectionAdapter Adapter(string id, ConnectionCounters? counters) => new(id, id, "", "Tunnel", "Down", [], counters, "");
        var snapshot = new ConnectionSnapshot(DateTimeOffset.Now,
            [Adapter("ethernet", new(100, 300, 2, 4)), Adapter("vpn", new(50, 20, 1, 2)), Adapter("unavailable", null)], [], "");
        Assert.Equal(2, snapshot.MeasuredAdapters);
        Assert.Equal(150, snapshot.BytesSent); Assert.Equal(320, snapshot.BytesReceived);
        Assert.Equal(3, snapshot.PacketsSent); Assert.Equal(6, snapshot.PacketsReceived);
        Assert.Null(snapshot.Adapters[2].Counters);
    }

    [Fact]
    public void AggregationDoesNotOverflowSignedAdapterCounters()
    {
        var counters = new ConnectionCounters(long.MaxValue, long.MaxValue, long.MaxValue, long.MaxValue);
        var adapter = new ConnectionAdapter("a", "a", "", "Ethernet", "Up", [], counters, "");
        var snapshot = new ConnectionSnapshot(DateTimeOffset.Now, [adapter, adapter with { Id = "b" }], [], "");
        Assert.Equal((decimal)long.MaxValue * 2, counters.TotalBytes);
        Assert.Equal((decimal)long.MaxValue * 2, snapshot.PacketsSent);
        Assert.Equal((decimal)long.MaxValue * 2, snapshot.BytesReceived);
    }

    [Fact]
    public void EmptySnapshotHasNoMeasuredAdapters()
    {
        var snapshot = new ConnectionSnapshot(DateTimeOffset.Now, [], [], "");
        Assert.Equal(0, snapshot.MeasuredAdapters); Assert.Equal(0, snapshot.BytesSent);
    }
}
