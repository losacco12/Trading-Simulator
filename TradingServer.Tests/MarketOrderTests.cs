using TradingServer.Core;
using Xunit;

public class MarketOrderTests
{
    [Fact]
    public void MarketBuy_ConsumesBestSellsUntilFilled()
    {
        var book = new OrderBook("AAPL");

        book.AddRestingOrder(new Order(1, OrderSide.Sell, "AAPL", 3, 101m) { Account = "Bob" });
        book.AddRestingOrder(new Order(2, OrderSide.Sell, "AAPL", 4, 102m) { Account = "Bob" });

        var mktBuy = new Order(99, OrderSide.Buy, "AAPL", 5, 0m) { Account = "Alice" };

        var result = book.MatchMarket(mktBuy);

        Assert.Equal(2, result.Trades.Count);
        Assert.Equal(0, mktBuy.RemainingQuantity);

        Assert.Equal(3, result.Trades[0].Quantity);
        Assert.Equal(101m, result.Trades[0].Price);

        Assert.Equal(2, result.Trades[1].Quantity);
        Assert.Equal(102m, result.Trades[1].Price);
    }

    [Fact]
    public void MarketSell_WhenBookEmpty_RemainsUnfilled()
    {
        var book = new OrderBook("AAPL");
        var mktSell = new Order(1, OrderSide.Sell, "AAPL", 5, 0m) { Account = "Bob" };

        var result = book.MatchMarket(mktSell);

        Assert.Empty(result.Trades);
        Assert.Equal(5, mktSell.RemainingQuantity);
    }
}
