using Microsoft.Data.Sqlite;
using TradingServer.Core;
using TradingServer.Core.Events;
using Xunit;

public class EventJournalTests
{
    [Fact]
    public void InsertEvent_ThenQuery_ReturnsEvent()
    {
        // Shared in-memory DB (exists as long as at least one connection stays open)
        var cs = "Data Source=file:memdb1?mode=memory&cache=shared";

        using var keeper = new SqliteConnection(cs);
        keeper.Open();

        var db = new TradeDatabase(cs);

        db.InsertEvent(ExchangeEventType.OrderAccepted, "Alice", 123, new { kind = "LIMIT", side = "Buy" });

        var events = db.GetLatestEvents(10);

        Assert.NotEmpty(events);
        Assert.Contains("OrderAccepted", events[0]);
        Assert.Contains("Alice", events[0]);
        Assert.Contains("123", events[0]);
    }
}
