using System;
using System.Collections.Generic;
using TradingServer.Core.Replay;
using Xunit;

public class ReplayVerifyTests
{
    [Fact]
    public void Compare_PerfectMatch_ReturnsOk()
    {
        var replay = State(
            open: new[] { 1L, 2L },
            books: new Dictionary<string, long[]>
            {
                ["AAPL"] = new[] { 1L },
                ["MSFT"] = new[] { 2L }
            },
            remaining: new Dictionary<long, int>
            {
                [1] = 10,
                [2] = 5
            });

        var live = State(
            open: new[] { 1L, 2L },
            books: new Dictionary<string, long[]>
            {
                ["AAPL"] = new[] { 1L },
                ["MSFT"] = new[] { 2L }
            },
            remaining: new Dictionary<long, int>
            {
                [1] = 10,
                [2] = 5
            });

        var result = ReplayVerifier.Compare(replay, live);

        Assert.Empty(result.MissingInLive);
        Assert.Empty(result.MissingInReplay);
        Assert.Empty(result.MissingInLiveBySymbol);
        Assert.Empty(result.MissingInReplayBySymbol);
        Assert.Empty(result.QtyMismatches);

        Assert.Empty(result.ReplayOpenNotInReplayBooks);
        Assert.Empty(result.ReplayBooksNotInReplayOpen);
        Assert.Empty(result.LiveOpenNotInLiveBooks);
        Assert.Empty(result.LiveBooksNotInLiveOpen);

        Assert.Empty(result.Warnings);

        string text = result.ToPrettyString();
        Assert.Contains("OK: Replay state matches live state.", text);
    }

    [Fact]
    public void Compare_OpenMissingInLive_PopulatesMissingInLive()
    {
        var replay = State(
            open: new[] { 10L, 11L },
            books: new Dictionary<string, long[]>
            {
                ["AAPL"] = new[] { 10L, 11L }
            },
            remaining: new Dictionary<long, int> { [10] = 1, [11] = 2 });

        var live = State(
            open: new[] { 10L }, // Missing 11
            books: new Dictionary<string, long[]>
            {
                ["AAPL"] = new[] { 10L } // Missing 11
            },
            remaining: new Dictionary<long, int> { [10] = 1 });

        var result = ReplayVerifier.Compare(replay, live);

        Assert.Contains(11L, result.MissingInLive);
        Assert.Empty(result.MissingInReplay);
        Assert.True(result.MissingInLiveBySymbol.ContainsKey("AAPL"));
        Assert.Contains(11L, result.MissingInLiveBySymbol["AAPL"]);
    }

    [Fact]
    public void Compare_OpenMissingInReplay_PopulatesMissingInReplay()
    {
        var replay = State(
            open: new[] { 20L },
            books: new Dictionary<string, long[]>
            {
                ["AAPL"] = new[] { 20L }
            },
            remaining: new Dictionary<long, int> { [20] = 3 });

        var live = State(
            open: new[] { 20L, 21L }, // extra
            books: new Dictionary<string, long[]>
            {
                ["AAPL"] = new[] { 20L, 21L }
            },
            remaining: new Dictionary<long, int> { [20] = 3, [21] = 9 });

        var result = ReplayVerifier.Compare(replay, live);

        Assert.Contains(21L, result.MissingInReplay);
        Assert.Empty(result.MissingInLive);
        Assert.True(result.MissingInReplayBySymbol.ContainsKey("AAPL"));
        Assert.Contains(21L, result.MissingInReplayBySymbol["AAPL"]);
    }

    [Fact]
    public void Compare_PerSymbolBookDiff_DetectedEvenIfOpenMatches()
    {
        // Open sets match, but book membership differs across symbols.
        var replay = State(
            open: new[] { 30L, 31L },
            books: new Dictionary<string, long[]>
            {
                ["AAPL"] = new[] { 30L },
                ["MSFT"] = new[] { 31L }
            },
            remaining: new Dictionary<long, int> { [30] = 1, [31] = 1 });

        var live = State(
            open: new[] { 30L, 31L },
            books: new Dictionary<string, long[]>
            {
                // swapped:
                ["AAPL"] = new[] { 31L },
                ["MSFT"] = new[] { 30L }
            },
            remaining: new Dictionary<long, int> { [30] = 1, [31] = 1 });

        var result = ReplayVerifier.Compare(replay, live);

        // For AAPL: replay has 30, live has 31
        Assert.True(result.MissingInLiveBySymbol.ContainsKey("AAPL"));
        Assert.Contains(30L, result.MissingInLiveBySymbol["AAPL"]);
        Assert.True(result.MissingInReplayBySymbol.ContainsKey("AAPL"));
        Assert.Contains(31L, result.MissingInReplayBySymbol["AAPL"]);
    }

    [Fact]
    public void Compare_QtyMismatchForOpenOrders_Reported()
    {
        var replay = State(
            open: new[] { 40L },
            books: new Dictionary<string, long[]>
            {
                ["AAPL"] = new[] { 40L }
            },
            remaining: new Dictionary<long, int> { [40] = 7 });

        var live = State(
            open: new[] { 40L },
            books: new Dictionary<string, long[]>
            {
                ["AAPL"] = new[] { 40L }
            },
            remaining: new Dictionary<long, int> { [40] = 6 });

        var result = ReplayVerifier.Compare(replay, live);

        Assert.Single(result.QtyMismatches);
        Assert.Equal(40L, result.QtyMismatches[0].OrderId);
        Assert.Equal(7, result.QtyMismatches[0].ReplayQty);
        Assert.Equal(6, result.QtyMismatches[0].LiveQty);

        string text = result.ToPrettyString();
        Assert.Contains("QtyMismatches", text);
        Assert.Contains("Order 40", text);
    }

    [Fact]
    public void Compare_ReplayInvariant_OpenNotInBooks_Detected()
    {
        // Replay says open includes 50 but books don't contain 50
        var replay = State(
            open: new[] { 50L },
            books: new Dictionary<string, long[]>
            {
                ["AAPL"] = Array.Empty<long>() // Empty
            },
            remaining: new Dictionary<long, int> { [50] = 1 });

        var live = State(
            open: new[] { 50L },
            books: new Dictionary<string, long[]>
            {
                ["AAPL"] = new[] { 50L }
            },
            remaining: new Dictionary<long, int> { [50] = 1 });

        var result = ReplayVerifier.Compare(replay, live);

        Assert.Contains(50L, result.ReplayOpenNotInReplayBooks);
        Assert.Empty(result.ReplayBooksNotInReplayOpen); // Because there are no book ids
    }

    [Fact]
    public void Compare_LiveInvariant_BooksNotInOpen_Detected()
    {
        // Live has a book id that isn't in live open
        var replay = State(
            open: new[] { 60L },
            books: new Dictionary<string, long[]>
            {
                ["AAPL"] = new[] { 60L }
            },
            remaining: new Dictionary<long, int> { [60] = 1 });

        var live = State(
            open: Array.Empty<long>(),
            books: new Dictionary<string, long[]>
            {
                ["AAPL"] = new[] { 60L }
            },
            remaining: new Dictionary<long, int> { [60] = 1 });

        var result = ReplayVerifier.Compare(replay, live);

        Assert.Contains(60L, result.LiveBooksNotInLiveOpen);
    }

    [Fact]
    public void Compare_MissingRemainingQty_AddsWarnings()
    {
        var replay = State(
            open: new[] { 70L },
            books: new Dictionary<string, long[]>
            {
                ["AAPL"] = new[] { 70L }
            },
            remaining: new Dictionary<long, int>() // missing qty
        );

        var live = State(
            open: new[] { 70L },
            books: new Dictionary<string, long[]>
            {
                ["AAPL"] = new[] { 70L }
            },
            remaining: new Dictionary<long, int> { [70] = 1 }
        );

        var result = ReplayVerifier.Compare(replay, live);

        Assert.NotEmpty(result.Warnings);
        Assert.Contains(result.Warnings, w => w.Contains("Replay missing RemainingQty"));
    }

    // Helpers 

    private static ReplayState State(
        IEnumerable<long> open,
        Dictionary<string, long[]> books,
        Dictionary<long, int> remaining)
    {
        var s = new ReplayState();

        foreach (var id in open)
            s.OpenOrderIds.Add(id);

        foreach (var (sym, ids) in books)
        {
            if (!s.BookRestingIds.TryGetValue(sym, out var set))
            {
                set = new HashSet<long>();
                s.BookRestingIds[sym] = set;
            }

            foreach (var id in ids)
                set.Add(id);
        }

        foreach (var kvp in remaining)
            s.RemainingQtyByOrderId[kvp.Key] = kvp.Value;

        return s;
    }
}