using System.Text;

namespace TradingServer.Core.Metrics;

public static class MetricsFormatter
{
    public static string Format(MetricsSnapshot s)
    {
        var sb = new StringBuilder();
        sb.AppendLine("=== METRICS ===");
        sb.AppendLine($"commands_total={s.CommandsTotal}");
        sb.AppendLine($"orders_accepted_total={s.OrdersAcceptedTotal}");
        sb.AppendLine($"orders_rejected_total={s.OrdersRejectedTotal}");
        sb.AppendLine($"trades_total={s.TradesTotal}");
        sb.AppendLine($"trade_volume_total={s.TradeVolumeTotal}");
        sb.AppendLine($"errors_total={s.ErrorsTotal}");
        sb.AppendLine();

        sb.AppendLine(FormatLatency("command_rtt_ms", s.CommandRtt));
        sb.AppendLine(FormatLatency("match_latency_ms", s.MatchLatency));

        return sb.ToString();
    }

    private static string FormatLatency(string name, LatencyStats stats)
    {
        return $"{name} " +
               $"count={stats.Count} " +
               $"avg={stats.AvgMs:0.00} " +
               $"p50={stats.P50Ms:0.00} " +
               $"p95={stats.P95Ms:0.00} " +
               $"p99={stats.P99Ms:0.00} " +
               $"max={stats.MaxMs:0.00}";
    }
}