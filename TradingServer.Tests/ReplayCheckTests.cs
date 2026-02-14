using Microsoft.Data.Sqlite;
using TradingServer.Core;
using TradingServer.Core.Events;
using Xunit;

public class ReplayCheckTests
{
    [Fact]
    public void ReplayCheckExpanded_FlagsUnknownOrderRefs()
    {
        var cs = "Data Source=file:memreplaycheck1?mode=memory&cache=shared";
        using var keeper = new SqliteConnection(cs);
        keeper.Open();

        var db = new TradeDatabase(cs);

        // Trade references orders that were never accepted
        db.InsertEvent(
            ExchangeEventType.TradeExecuted,
            null,
            null,
            new { Symbol="AAPL", Quantity=5, Price=100m, BuyOrderId=10L, SellOrderId=11L, BuyerAccount="A", SellerAccount="B" }
        );

        var ex = new Exchange(db, replayFromEvents: true);

        string report = ex.ReplayCheckExpanded(1000);

        Assert.Contains("UnknownOrderRefs=", report);
        Assert.Contains("WARN:", report);
    }
}
