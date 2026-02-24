using TradingServer.Core;
using Xunit;

public class IocFokTests
{
    [Fact]
    public void IocBuy_PartialFill_DoesNotRest()
    {
        var book = new OrderBook("AAPL");
        book.AddRestingOrder(new Order(10, OrderSide.Sell, "AAPL", 2, 100m) { Account = "Bob" });

        var iocBuy = new Order(99, OrderSide.Buy, "AAPL", 5, 100m) { Account = "Alice" };

        var result = book.MatchIoc(iocBuy);

        Assert.Single(result.Trades);
        Assert.Equal(3, iocBuy.RemainingQuantity); // remainder canceled
        Assert.DoesNotContain("BUY#99", book.GetDetailedBook());
        Assert.DoesNotContain("SELL#10", book.GetDetailedBook());
    }

    [Fact]
    public void FokBuy_NotEnoughLiquidity_NoTrades_NoBookChange()
    {
        var book = new OrderBook("AAPL");
        book.AddRestingOrder(new Order(1, OrderSide.Sell, "AAPL", 2, 100m) { Account = "Bob" });

        string before = book.GetDetailedBook();

        var fokBuy = new Order(2, OrderSide.Buy, "AAPL", 5, 100m) { Account = "Alice" };
        var result = book.MatchFok(fokBuy);

        Assert.Empty(result.Trades);
        Assert.Equal(5, fokBuy.RemainingQuantity);

        string after = book.GetDetailedBook();
        Assert.Equal(before, after); // no changes
    }

    [Fact]
    public void FokSell_EnoughLiquidity_FillsFully_DoesNotRest()
    {
        var book = new OrderBook("AAPL");
        book.AddRestingOrder(new Order(1, OrderSide.Buy, "AAPL", 3, 101m) { Account = "Alice" });
        book.AddRestingOrder(new Order(2, OrderSide.Buy, "AAPL", 4, 100m) { Account = "Alice" });

        var fokSell = new Order(9, OrderSide.Sell, "AAPL", 7, 100m) { Account = "Bob" };
        var result = book.MatchFok(fokSell);

        Assert.Equal(2, result.Trades.Count);
        Assert.Equal(0, fokSell.RemainingQuantity);
        Assert.DoesNotContain("SELL#9", book.GetDetailedBook());
        Assert.Equal(3, result.Trades[0].Quantity);
        Assert.Equal(101m, result.Trades[0].Price);
        Assert.Equal(4, result.Trades[1].Quantity);
        Assert.Equal(100m, result.Trades[1].Price);

    }
}
