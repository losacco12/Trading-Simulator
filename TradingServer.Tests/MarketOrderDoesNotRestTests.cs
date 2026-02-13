using TradingServer.Core;
using Xunit;

public class MarketOrderDoesNotRestTests
{
    [Fact]
    public void MarketBuy_PartiallyUnfilled_DoesNotRestInBook()
    {
        // Arrange: only 2 shares available to buy from sells
        var book = new OrderBook("AAPL");
        book.AddRestingOrder(new Order(10, OrderSide.Sell, "AAPL", 2, 101m) { Account = "Bob" });

        // Market buy wants 5, but only 2 available
        var marketBuy = new Order(99, OrderSide.Buy, "AAPL", 5, 0m) { Account = "Alice" };

        // Act
        var result = book.MatchMarket(marketBuy);

        // Assert: it should fill 2 and leave 3 unfilled
        Assert.Single(result.Trades);
        Assert.Equal(3, marketBuy.RemainingQuantity);

        // Market order should NOT appear in the book
        string detailed = book.GetDetailedBook();
        Assert.DoesNotContain("BUY#99", detailed);
        Assert.DoesNotContain("SELL#99", detailed);

        // The sell should be gone because it was fully filled
        Assert.DoesNotContain("SELL#10", detailed);
    }

    [Fact]
    public void MarketSell_PartiallyUnfilled_DoesNotRestInBook()
    {
        // Arrange: only 3 shares available to sell into buys
        var book = new OrderBook("AAPL");
        book.AddRestingOrder(new Order(20, OrderSide.Buy, "AAPL", 3, 100m) { Account = "Alice" });

        // Market sell wants 10, but only 3 available
        var marketSell = new Order(199, OrderSide.Sell, "AAPL", 10, 0m) { Account = "Bob" };

        // Act
        var result = book.MatchMarket(marketSell);

        // Assert: it should fill 3 and leave 7 unfilled
        Assert.Single(result.Trades);
        Assert.Equal(7, marketSell.RemainingQuantity);

        // Market order should NOT appear in the book
        string detailed = book.GetDetailedBook();
        Assert.DoesNotContain("SELL#199", detailed);
        Assert.DoesNotContain("BUY#199", detailed);

        // The buy should be gone because it was fully filled
        Assert.DoesNotContain("BUY#20", detailed);
    }
}
