namespace TradingServer.Core.Metrics;

public sealed class MetricsSnapshot
{
    public long CommandsTotal { get; init; }
    public long OrdersAcceptedTotal { get; init; }
    public long OrdersRejectedTotal { get; init; }
    public long TradesTotal { get; init; }
    public long TradeVolumeTotal { get; init; }
    public long ErrorsTotal { get; init; }

    public LatencyStats CommandRtt { get; init; } = new();
    public LatencyStats MatchLatency { get; init; } = new();
}

public sealed class LatencyStats
{
    public int Count { get; init; }
    public double AvgMs { get; init; }
    public double P50Ms { get; init; }
    public double P95Ms { get; init; }
    public double P99Ms { get; init; }
    public double MaxMs { get; init; }
}