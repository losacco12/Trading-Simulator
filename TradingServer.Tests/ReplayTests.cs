using Microsoft.Data.Sqlite;
using TradingServer.Core;
using TradingServer.Core.Events;
using Xunit;

public class ReplayTests
{
    [Fact]
    public void ReplayFromEvents_RebuildsOpenBook()
    {
        var cs = "Data Source=file:memreplay1?mode=memory&cache=shared";
        using var keeper = new SqliteConnection(cs);
        keeper.Open();

        var db = new TradeDatabase(cs);

        // OrderAccepted LIMIT BUY AAPL 10 @ 100 (orderId=1)
        db.InsertEvent(ExchangeEventType.OrderAccepted, "Alice", 1,
            new { kind = "LIMIT", Side = "Buy", Symbol = "AAPL", qty = 10, Price = 100m });

        // No trades, no cancels => should be resting
        var ex = new Exchange(db, replayFromEvents: true);

        string book = ex.GetBook("AAPL");

        Assert.Contains("BUY#1", book);
        Assert.Contains("qty=10/10", book);
    }

    [Fact]
    public void ReplayFromEvents_AppliesTradesAndRemovesFilled()
    {
        var cs = "Data Source=file:memreplay2?mode=memory&cache=shared";
        using var keeper = new SqliteConnection(cs);
        keeper.Open();

        var db = new TradeDatabase(cs);

        // Resting SELL#2
        db.InsertEvent(ExchangeEventType.OrderAccepted, "Bob", 2,
            new { kind = "LIMIT", Side = "Sell", Symbol = "AAPL", qty = 5, Price = 100m });

        // Incoming MARKET BUY#3 (never rests)
        db.InsertEvent(ExchangeEventType.OrderAccepted, "Alice", 3,
            new { kind = "MARKET", Side = "Buy", Symbol = "AAPL", qty = 5 });

        // Trade executes 5 shares between BUY#3 and SELL#2
        db.InsertEvent(ExchangeEventType.TradeExecuted, null, null,
            new { Symbol = "AAPL", Quantity = 5, Price = 100m, BuyOrderId = 3L, SellOrderId = 2L, BuyerAccount = "Alice", SellerAccount = "Bob" });

        var ex = new Exchange(db, replayFromEvents: true);

        // SELL#2 should be gone (filled)
        string book = ex.GetBook("AAPL");
        Assert.DoesNotContain("SELL#2", book);
    }
    
    [Fact]
    public void ReplayFromEvents_SetsNextOrderId_FromNonRestingOrders()
    {
        var cs = "Data Source=file:memreplay3?mode=memory&cache=shared";
        using var keeper = new SqliteConnection(cs);
        keeper.Open();

        var db = new TradeDatabase(cs);

        // A MARKET order never rests but should still advance max id
        db.InsertEvent(ExchangeEventType.OrderAccepted, "Alice", 50,
            new { kind = "MARKET", Side = "Buy", Symbol = "AAPL", qty = 1 });

        var ex = new Exchange(db, replayFromEvents: true);

        // Force an actual submit to see what ID it assigns next:
        var o = new Order(0, OrderSide.Buy, "AAPL", 1, 100m) { Account = "Alice" };
        var result = ex.Submit(o);

        Assert.Equal(51, o.OrderId);
    }
    
    [Fact]
    public void ReplayFromEvents_SetsNextOrderId_FromNonRestingMarketOrder()
    {
        var cs = "Data Source=file:memreplay_nextid?mode=memory&cache=shared";
        using var keeper = new SqliteConnection(cs);
        keeper.Open();

        var db = new TradeDatabase(cs);

        // MARKET orders never rest, but they must still advance next order id
        db.InsertEvent(
            ExchangeEventType.OrderAccepted,
            "Alice",
            50,
            new { kind = "MARKET", Side = "Buy", Symbol = "AAPL", qty = 1 }
        );

        var ex = new Exchange(db, replayFromEvents: true);

        // Submit a new limit order and ensure it gets OrderId=51
        var next = new Order(0, OrderSide.Buy, "AAPL", 1, 100m) { Account = "Alice" };
        ex.Submit(next);

        Assert.Equal(51, next.OrderId);
    }

}
