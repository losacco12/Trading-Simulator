using System.Text;

namespace TradingServer.Core.Replay;

public sealed class ReplayReport
{
    public int EventsProcessed { get; set; }
    public long MaxSeenOrderId { get; set; }

    public Dictionary<string, int> EventTypeCounts { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, int> AcceptedKindCounts { get; } = new(StringComparer.OrdinalIgnoreCase);

    public int Trades { get; set; }
    public long TradeVolume { get; set; }

    public int UnknownOrderRefs { get; set; }
    public int NegativeRemainingDetected { get; set; }

    public List<string> Warnings { get; } = new();

    public int BooksCount { get; set; }
    public int OpenOrdersCount { get; set; }
    public int OrdersInBooksCount { get; set; }
    public int OpenNotInBooks { get; set; }
    public int InBooksNotOpen { get; set; }

    public string ToPrettyString(int maxWarnings = 50)
    {
        var sb = new StringBuilder();
        sb.AppendLine("=== REPLAYCHECK (Expanded) ===");

        sb.AppendLine($"EventsProcessed={EventsProcessed}");
        sb.AppendLine($"MaxSeenOrderId={MaxSeenOrderId}");

        sb.AppendLine("-- Event Types --");
        foreach (var kv in EventTypeCounts.OrderByDescending(k => k.Value))
            sb.AppendLine($"{kv.Key}: {kv.Value}");

        sb.AppendLine("-- Accepted Kinds --");
        foreach (var kv in AcceptedKindCounts.OrderByDescending(k => k.Value))
            sb.AppendLine($"{kv.Key}: {kv.Value}");

        sb.AppendLine("-- Trades --");
        sb.AppendLine($"Trades={Trades} Volume={TradeVolume}");

        sb.AppendLine("-- Integrity --");
        sb.AppendLine($"UnknownOrderRefs={UnknownOrderRefs}");
        sb.AppendLine($"NegativeRemainingDetected={NegativeRemainingDetected}");
        sb.AppendLine($"Books={BooksCount} OpenOrders={OpenOrdersCount} OrdersInBooks={OrdersInBooksCount}");
        sb.AppendLine($"OpenNotInBooks={OpenNotInBooks} InBooksNotOpen={InBooksNotOpen}");

        if (Warnings.Count == 0)
        {
            sb.AppendLine("OK: No warnings detected.");
        }
        else
        {
            sb.AppendLine($"WARNINGS ({Math.Min(Warnings.Count, maxWarnings)} of {Warnings.Count})");
            foreach (var w in Warnings.Take(maxWarnings))
                sb.AppendLine($"WARN: {w}");
        }

        return sb.ToString();
    }
}
