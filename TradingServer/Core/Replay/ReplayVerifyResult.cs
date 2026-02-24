namespace TradingServer.Core.Replay;

public sealed class ReplayVerifyResult
{
    public int ReplayOpenCount { get; set; }
    public int LiveOpenCount { get; set; }

    public int ReplayBooks { get; set; }
    public int LiveBooks { get; set; }

    public List<long> MissingInLive { get; } = new();   // in replay open but not live open
    public List<long> MissingInReplay { get; } = new(); // in live open but not replay open

    // Per-symbol resting-book diffs
    public Dictionary<string, List<long>> MissingInLiveBySymbol { get; } =
        new(StringComparer.OrdinalIgnoreCase);

    public Dictionary<string, List<long>> MissingInReplayBySymbol { get; } =
        new(StringComparer.OrdinalIgnoreCase);

    // Stronger invariants (within each side)
    public List<long> ReplayOpenNotInReplayBooks { get; } = new();
    public List<long> ReplayBooksNotInReplayOpen { get; } = new();

    public List<long> LiveOpenNotInLiveBooks { get; } = new();
    public List<long> LiveBooksNotInLiveOpen { get; } = new();

    // Structured qty mismatches
    public List<QtyMismatch> QtyMismatches { get; } = new();

    public List<string> Warnings { get; } = new();

    public string ToPrettyString()
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("=== REPLAYVERIFY ===");
        sb.AppendLine($"ReplayOpen={ReplayOpenCount} LiveOpen={LiveOpenCount}");
        sb.AppendLine($"ReplayBooks={ReplayBooks} LiveBooks={LiveBooks}");

        if (MissingInLive.Count > 0)
        {
            sb.AppendLine("-- MissingInLive (replay open, live missing) --");
            foreach (var id in MissingInLive.Take(50)) sb.AppendLine(id.ToString());
            if (MissingInLive.Count > 50) sb.AppendLine($"... +{MissingInLive.Count - 50} more");
        }

        if (MissingInReplay.Count > 0)
        {
            sb.AppendLine("-- MissingInReplay (live open, replay missing) --");
            foreach (var id in MissingInReplay.Take(50)) sb.AppendLine(id.ToString());
            if (MissingInReplay.Count > 50) sb.AppendLine($"... +{MissingInReplay.Count - 50} more");
        }

        if (MissingInLiveBySymbol.Count > 0)
        {
            sb.AppendLine("-- BookDiff MissingInLiveBySymbol (replay resting, live missing) --");
            foreach (var (sym, ids) in MissingInLiveBySymbol.OrderBy(k => k.Key).Take(20))
            {
                sb.AppendLine($"{sym}: {string.Join(", ", ids.Take(25))}{(ids.Count > 25 ? $" ... +{ids.Count - 25} more" : "")}");
            }
            if (MissingInLiveBySymbol.Count > 20) sb.AppendLine($"... +{MissingInLiveBySymbol.Count - 20} more symbols");
        }

        if (MissingInReplayBySymbol.Count > 0)
        {
            sb.AppendLine("-- BookDiff MissingInReplayBySymbol (live resting, replay missing) --");
            foreach (var (sym, ids) in MissingInReplayBySymbol.OrderBy(k => k.Key).Take(20))
            {
                sb.AppendLine($"{sym}: {string.Join(", ", ids.Take(25))}{(ids.Count > 25 ? $" ... +{ids.Count - 25} more" : "")}");
            }
            if (MissingInReplayBySymbol.Count > 20) sb.AppendLine($"... +{MissingInReplayBySymbol.Count - 20} more symbols");
        }

        if (QtyMismatches.Count > 0)
        {
            sb.AppendLine("-- QtyMismatches (open orders) --");
            foreach (var m in QtyMismatches.Take(50))
                sb.AppendLine($"Order {m.OrderId}: replay={m.ReplayQty} live={m.LiveQty}");
            if (QtyMismatches.Count > 50) sb.AppendLine($"... +{QtyMismatches.Count - 50} more");
        }

        if (ReplayOpenNotInReplayBooks.Count > 0)
        {
            sb.AppendLine("-- ReplayInvariant: OpenNotInBooks --");
            foreach (var id in ReplayOpenNotInReplayBooks.Take(50)) sb.AppendLine(id.ToString());
        }

        if (ReplayBooksNotInReplayOpen.Count > 0)
        {
            sb.AppendLine("-- ReplayInvariant: BooksNotInOpen --");
            foreach (var id in ReplayBooksNotInReplayOpen.Take(50)) sb.AppendLine(id.ToString());
        }

        if (LiveOpenNotInLiveBooks.Count > 0)
        {
            sb.AppendLine("-- LiveInvariant: OpenNotInBooks --");
            foreach (var id in LiveOpenNotInLiveBooks.Take(50)) sb.AppendLine(id.ToString());
        }

        if (LiveBooksNotInLiveOpen.Count > 0)
        {
            sb.AppendLine("-- LiveInvariant: BooksNotInOpen --");
            foreach (var id in LiveBooksNotInLiveOpen.Take(50)) sb.AppendLine(id.ToString());
        }

        if (Warnings.Count > 0)
        {
            sb.AppendLine("-- Warnings --");
            foreach (var w in Warnings.Take(50)) sb.AppendLine(w);
            if (Warnings.Count > 50) sb.AppendLine($"... +{Warnings.Count - 50} more");
        }

        if (IsOk())
            sb.AppendLine("OK: Replay state matches live state.");

        return sb.ToString();
    }

    private bool IsOk()
    {
        return MissingInLive.Count == 0 &&
               MissingInReplay.Count == 0 &&
               MissingInLiveBySymbol.Count == 0 &&
               MissingInReplayBySymbol.Count == 0 &&
               QtyMismatches.Count == 0 &&
               ReplayOpenNotInReplayBooks.Count == 0 &&
               ReplayBooksNotInReplayOpen.Count == 0 &&
               LiveOpenNotInLiveBooks.Count == 0 &&
               LiveBooksNotInLiveOpen.Count == 0 &&
               Warnings.Count == 0;
    }
}

public readonly record struct QtyMismatch(long OrderId, int ReplayQty, int LiveQty);