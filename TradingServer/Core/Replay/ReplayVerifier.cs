namespace TradingServer.Core.Replay;

public static class ReplayVerifier
{
    public static ReplayVerifyResult Compare(ReplayState replay, ReplayState live)
    {
        var r = new ReplayVerifyResult
        {
            ReplayOpenCount = replay.OpenOrderIds.Count,
            LiveOpenCount = live.OpenOrderIds.Count,
            ReplayBooks = replay.BookRestingIds.Count,
            LiveBooks = live.BookRestingIds.Count
        };

        // Open-set diffs
        foreach (var id in replay.OpenOrderIds)
            if (!live.OpenOrderIds.Contains(id))
                r.MissingInLive.Add(id);

        foreach (var id in live.OpenOrderIds)
            if (!replay.OpenOrderIds.Contains(id))
                r.MissingInReplay.Add(id);

        // Qty mismatches for open orders
        foreach (var id in replay.OpenOrderIds)
        {
            if (replay.RemainingQtyByOrderId.TryGetValue(id, out var rq) &&
                live.RemainingQtyByOrderId.TryGetValue(id, out var lq))
            {
                if (rq != lq)
                    r.QtyMismatches.Add(new QtyMismatch(id, rq, lq));
            }
            else
            {
                // Useful to know if one side is missing qty info
                if (!replay.RemainingQtyByOrderId.ContainsKey(id))
                    r.Warnings.Add($"Replay missing RemainingQty for open order {id}.");
                if (!live.RemainingQtyByOrderId.ContainsKey(id))
                    r.Warnings.Add($"Live missing RemainingQty for open order {id}.");
            }
        }

        // Per-symbol book diffs 
        // Build union of symbols
        var allSymbols = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var sym in replay.BookRestingIds.Keys) allSymbols.Add(sym);
        foreach (var sym in live.BookRestingIds.Keys) allSymbols.Add(sym);

        foreach (var sym in allSymbols)
        {
            replay.BookRestingIds.TryGetValue(sym, out var replaySet);
            live.BookRestingIds.TryGetValue(sym, out var liveSet);

            replaySet ??= new HashSet<long>();
            liveSet ??= new HashSet<long>();

            // ids that replay says are resting in this symbol, but live does not
            var missingInLive = replaySet.Where(id => !liveSet.Contains(id)).ToList();
            if (missingInLive.Count > 0)
                r.MissingInLiveBySymbol[sym] = missingInLive;

            // ids that live says are resting in this symbol, but replay does not
            var missingInReplay = liveSet.Where(id => !replaySet.Contains(id)).ToList();
            if (missingInReplay.Count > 0)
                r.MissingInReplayBySymbol[sym] = missingInReplay;
        }

        // Cross invariants: "open" should equal union of book resting ids (LIMIT-only assumption) 
        // Replay side
        var replayBookUnion = UnionBookIds(replay);
        foreach (var id in replay.OpenOrderIds)
            if (!replayBookUnion.Contains(id))
                r.ReplayOpenNotInReplayBooks.Add(id);

        foreach (var id in replayBookUnion)
            if (!replay.OpenOrderIds.Contains(id))
                r.ReplayBooksNotInReplayOpen.Add(id);

        // Live side
        var liveBookUnion = UnionBookIds(live);
        foreach (var id in live.OpenOrderIds)
            if (!liveBookUnion.Contains(id))
                r.LiveOpenNotInLiveBooks.Add(id);

        foreach (var id in liveBookUnion)
            if (!live.OpenOrderIds.Contains(id))
                r.LiveBooksNotInLiveOpen.Add(id);

        return r;
    }

    private static HashSet<long> UnionBookIds(ReplayState state)
    {
        var set = new HashSet<long>();
        foreach (var kvp in state.BookRestingIds)
            foreach (var id in kvp.Value)
                set.Add(id);
        return set;
    }
}