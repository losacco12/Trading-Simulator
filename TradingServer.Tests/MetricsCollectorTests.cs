using TradingServer.Core.Metrics;
using Xunit;

public class MetricsCollectorTests
{
    [Fact]
    public void LatencyRing_ComputesPercentiles()
    {
        var ring = new LatencyRing(100);

        for (int i = 1; i <= 100; i++)
            ring.Add(i);

        var stats = ring.Stats();
        Assert.Equal(100, stats.Count);
        Assert.InRange(stats.P50Ms, 50, 51);
        Assert.InRange(stats.P95Ms, 95, 96);
        Assert.InRange(stats.P99Ms, 99, 100);
        Assert.Equal(100, stats.MaxMs);
    }

    [Fact]
    public void MetricsCollector_SnapshotReflectsCounters()
    {
        var m = new MetricsCollector();
        m.IncCommands();
        m.IncOrdersAccepted();
        m.IncTrades(2, 15);
        m.ObserveCommandRttMs(10);
        m.ObserveMatchLatencyMs(5);

        var s = m.Snapshot();

        Assert.Equal(1, s.CommandsTotal);
        Assert.Equal(1, s.OrdersAcceptedTotal);
        Assert.Equal(2, s.TradesTotal);
        Assert.Equal(15, s.TradeVolumeTotal);
        Assert.Equal(1, s.CommandRtt.Count);
        Assert.Equal(1, s.MatchLatency.Count);
    }
}