namespace TradingServer.Core;


// record with mutable RemainingQuantity so we can partially fill
public class Order
{
    public long OrderId { get; set; }
    public string Account { get; set; } = "anonymous";
    public OrderSide Side { get; }
    public string Symbol { get; }
    public int OriginalQuantity { get; }
    public int RemainingQuantity { get; set; }
    public decimal Price { get; }

    public Order(long orderId, OrderSide side, string symbol, int quantity, decimal price)
    {
        OrderId = orderId;
        Side = side;
        Symbol = symbol;
        OriginalQuantity = quantity;
        RemainingQuantity = quantity;
        Price = price;
    }
}