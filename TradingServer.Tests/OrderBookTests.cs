using Xunit;
using TradingServer.Core;


public class OrderBookTests
{
    [Fact]
    public void BuyThenSell_CrossingPrices_TradesAtRestingBuyPrice()
    {
        // Arrange
        var book = new OrderBook("AAPL");

        var restingBuy = new Order(orderId: 1, side: OrderSide.Buy, symbol: "AAPL", quantity: 10, price: 100m)
        {
            Account = "Alice"
        };

        book.AddRestingOrder(restingBuy);

        var incomingSell = new Order(orderId: 2, side: OrderSide.Sell, symbol: "AAPL", quantity: 5, price: 99m)
        {
            Account = "Bob"
        };

        // Act
        MatchResult result = book.Match(incomingSell);

        // Assert
        Assert.Single(result.Trades);
        var trade = result.Trades[0];

        Assert.Equal("AAPL", trade.Symbol);
        Assert.Equal(5, trade.Quantity);
        Assert.Equal(100m, trade.Price);              // trades at resting buy price
        Assert.Equal(1, trade.BuyOrderId);
        Assert.Equal(2, trade.SellOrderId);
        Assert.Equal("Alice", trade.BuyerAccount);
        Assert.Equal("Bob", trade.SellerAccount);

        // Remaining quantity should stay on the resting buy
        Assert.Equal(5, restingBuy.RemainingQuantity);
        Assert.Equal(0, incomingSell.RemainingQuantity);
    }

    [Fact]
    public void SellAboveBuyPrice_DoesNotTrade_LeavesOrdersResting()
    {
        // Arrange
        var book = new OrderBook("AAPL");

        var restingBuy = new Order(1, OrderSide.Buy, "AAPL", 10, 100m) { Account = "Alice" };
        book.AddRestingOrder(restingBuy);

        var incomingSell = new Order(2, OrderSide.Sell, "AAPL", 5, 101m) { Account = "Bob" };

        // Act
        MatchResult result = book.Match(incomingSell);

        // Assert
        Assert.Empty(result.Trades);

        // No fill occurred
        Assert.Equal(10, restingBuy.RemainingQuantity);
        Assert.Equal(5, incomingSell.RemainingQuantity);

        // Sell should now be resting in the book (since it didn't match)
        string detailed = book.GetDetailedBook();
        Assert.Contains("SELL#2", detailed);
    }

    [Fact]
    public void PartialFill_RemovesFullyFilledRestingOrderAndTracksFilledIds()
    {
        // Arrange
        var book = new OrderBook("AAPL");

        var restingSell = new Order(10, OrderSide.Sell, "AAPL", 3, 100m) { Account = "Bob" };
        book.AddRestingOrder(restingSell);

        var incomingBuy = new Order(11, OrderSide.Buy, "AAPL", 10, 150m) { Account = "Alice" };

        // Act
        MatchResult result = book.Match(incomingBuy);

        // Assert
        Assert.Single(result.Trades);
        Assert.Equal(3, result.Trades[0].Quantity);

        // Resting sell fully filled -> should be recorded
        Assert.Contains(10, result.FilledOrderIds);

        // Incoming buy not fully filled -> should NOT be recorded as filled
        Assert.DoesNotContain(11, result.FilledOrderIds);

        // Incoming buy should have remaining
        Assert.Equal(7, incomingBuy.RemainingQuantity);

        // Sell should be gone from the book
        string detailed = book.GetDetailedBook();
        Assert.DoesNotContain("SELL#10", detailed);
        Assert.Contains("BUY#11", detailed);
    }
}
