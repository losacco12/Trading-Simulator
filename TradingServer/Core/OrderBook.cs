using System.Text;

namespace TradingServer.Core;



// OrderBook stores resting orders and matches incoming orders
public class OrderBook
{
    private readonly string _symbol;

    
    // Thread-safety     
    private readonly object _lock = new object();


    //Resting orders (in-memory)
    private readonly List<Order> _buys = new List<Order>();
    private readonly List<Order> _sells = new List<Order>();

    public OrderBook(string symbol)
    {
        _symbol = symbol;
    }
   
    public void AddRestingOrder(Order order)
    {
        lock (_lock)
        {
            if (order.Side == OrderSide.Buy)
                _buys.Add(order);
            else
                _sells.Add(order);
        }
    }

    
    
    
    public string GetDetailedBook()
    {
        lock (_lock)
        {
            var sb = new StringBuilder();
            sb.AppendLine($"=== BOOK {_symbol} ===");

            sb.AppendLine("-- BUYS (highest price first) --");
            foreach (var b in _buys.OrderByDescending(o => o.Price))
                sb.AppendLine($"BUY#{b.OrderId} qty={b.RemainingQuantity}/{b.OriginalQuantity} price={b.Price}");

            sb.AppendLine("-- SELLS (lowest price first) --");
            foreach (var s in _sells.OrderBy(o => o.Price))
                sb.AppendLine($"SELL#{s.OrderId} qty={s.RemainingQuantity}/{s.OriginalQuantity} price={s.Price}");

            if (_buys.Count == 0 && _sells.Count == 0)
                sb.AppendLine("(empty)");

            return sb.ToString();
        }
    }

    
    
    public MatchResult Match(Order incoming)
    {
        var result = new MatchResult();

        lock (_lock)
        {
            
            ///////BUYS////////
            if (incoming.Side == OrderSide.Buy)
            {
                // Try match against best sells (lowest price first)
                _sells.Sort((a, b) => a.Price.CompareTo(b.Price));

                int i = 0;
                
                while (incoming.RemainingQuantity > 0 && i < _sells.Count)
                {
                    Order sell = _sells[i];

                    // Can we trade?
                    if (sell.Price > incoming.Price)
                        break;
                    
                    int tradeQty = Math.Min(incoming.RemainingQuantity, sell.RemainingQuantity);

                    // Trade at resting order price (sell's price)
                    result.Trades.Add(new Trade(_symbol, tradeQty, sell.Price, incoming.OrderId, sell.OrderId, incoming.Account, sell.Account));

                    incoming.RemainingQuantity -= tradeQty;
                    sell.RemainingQuantity -= tradeQty;

                    // Remove fully filled sell
                    if (sell.RemainingQuantity == 0)
                    {
                        result.FilledOrderIds.Add(sell.OrderId);
                        _sells.RemoveAt(i);
                        continue; // don't increment i because list shifted
                    }
                    
                    i++;
                }
                
                // If not fully filled, add remaining to buys
                if (incoming.RemainingQuantity > 0)
                {
                    _buys.Add(incoming);
                }

                if (incoming.RemainingQuantity == 0)
                    result.FilledOrderIds.Add(incoming.OrderId);

            }
            
            ///////////////SELLS///////////
            else 
            {
                // Try match against best buys (highest price first)
                _buys.Sort((a, b) => b.Price.CompareTo(a.Price));

                int i = 0;
                
                while (incoming.RemainingQuantity > 0 && i < _buys.Count)
                {
                    Order buy = _buys[i];

                    if (buy.Price < incoming.Price)
                        break;

                    int tradeQty = Math.Min(incoming.RemainingQuantity, buy.RemainingQuantity);
                    
                    // Trade at resting order price (buy's price)
                    result.Trades.Add(new Trade(_symbol, tradeQty, buy.Price, buy.OrderId, incoming.OrderId, buy.Account, incoming.Account));


                    incoming.RemainingQuantity -= tradeQty;
                    buy.RemainingQuantity -= tradeQty;

                   if (buy.RemainingQuantity == 0)
                    {
                        result.FilledOrderIds.Add(buy.OrderId);
                        _buys.RemoveAt(i);
                        continue;
                    }

                    i++;
                }
                
                if (incoming.RemainingQuantity > 0)
                {
                    _sells.Add(incoming);
                }
                
                
                if (incoming.RemainingQuantity == 0)
                    result.FilledOrderIds.Add(incoming.OrderId);

            }

            result.BookSummary = BuildSummary();
            return result;
        }
    }
    private string BuildSummary()
    {
        int buyCount = _buys.Count;
        int sellCount = _sells.Count;

        decimal bestBuy = _buys.Count == 0 ? 0 : _buys.Max(o => o.Price);
        decimal bestSell = _sells.Count == 0 ? 0 : _sells.Min(o => o.Price);

        return $"{_symbol} buys={buyCount} sells={sellCount} bestBuy={bestBuy} bestSell={bestSell}";
    }
    
    public bool RemoveOrder(long orderId)
    {
        lock (_lock)
        {
            int before = _buys.Count + _sells.Count;

            _buys.RemoveAll(o => o.OrderId == orderId);
            _sells.RemoveAll(o => o.OrderId == orderId);

            int after = _buys.Count + _sells.Count;
            return after != before;
        }
    }

   public MatchResult MatchMarket(Order incoming)
    {
        var result = new MatchResult();

        lock (_lock)
        {
            if (incoming.Side == OrderSide.Buy)
            {
                // best sells (lowest first)
                _sells.Sort((a, b) => a.Price.CompareTo(b.Price));

                int i = 0;
                while (incoming.RemainingQuantity > 0 && i < _sells.Count)
                {
                    Order sell = _sells[i];

                    int tradeQty = Math.Min(incoming.RemainingQuantity, sell.RemainingQuantity);

                    result.Trades.Add(new Trade(
                        _symbol, tradeQty, sell.Price,
                        incoming.OrderId, sell.OrderId,
                        incoming.Account, sell.Account
                    ));

                    incoming.RemainingQuantity -= tradeQty;
                    sell.RemainingQuantity -= tradeQty;

                    if (sell.RemainingQuantity == 0)
                    {
                        result.FilledOrderIds.Add(sell.OrderId);
                        _sells.RemoveAt(i);
                        continue;
                    }

                    i++;
                }

                if (incoming.RemainingQuantity == 0)
                    result.FilledOrderIds.Add(incoming.OrderId);
            }
            else
            {
                // best buys (highest first)
                _buys.Sort((a, b) => b.Price.CompareTo(a.Price));

                int i = 0;
                while (incoming.RemainingQuantity > 0 && i < _buys.Count)
                {
                    Order buy = _buys[i];

                    int tradeQty = Math.Min(incoming.RemainingQuantity, buy.RemainingQuantity);

                    result.Trades.Add(new Trade(
                        _symbol, tradeQty, buy.Price,
                        buy.OrderId, incoming.OrderId,
                        buy.Account, incoming.Account
                    ));

                    incoming.RemainingQuantity -= tradeQty;
                    buy.RemainingQuantity -= tradeQty;

                    if (buy.RemainingQuantity == 0)
                    {
                        result.FilledOrderIds.Add(buy.OrderId);
                        _buys.RemoveAt(i);
                        continue;
                    }

                    i++;
                }

                if (incoming.RemainingQuantity == 0)
                    result.FilledOrderIds.Add(incoming.OrderId);
            }

            result.BookSummary = BuildSummary();
            return result;
        }
    }

}

