using System.Collections.Concurrent;
using System.Text;
using TradingServer.Core.Events;

namespace TradingServer.Core;

// Exchange holds order books for all symbols
public class Exchange
{
    private readonly ConcurrentDictionary<string, OrderBook> _books = new();
    private long _nextOrderId = 0;
    private readonly TradeDatabase _db;
    private readonly ConcurrentDictionary<long, Order> _openOrders = new();


    public Exchange(TradeDatabase db)
    {
        _db = db;
        
        // Avoid collisions
        _nextOrderId = _db.GetMaxOrderId();
        Console.WriteLine($"Exchange starting next OrderId from DB: {_nextOrderId}");

        //Rebuild in-memory books from db
        RebuildBooksFromDatabase();
    }

    private void RebuildBooksFromDatabase()
    {
        var orders = _db.GetAllOrders();
        var trades = _db.GetAllTrades();
        var canceled = _db.GetAllCanceledOrderIds();

        //filled[orderID] = total filled quantity for that order
        var filled= new Dictionary<long, int>();

        foreach (var t in trades)
        {
            //t.Quantity filled for both buy and sell order
            if(!filled.ContainsKey(t.BuyOrderId)) filled[t.BuyOrderId]=0;
            if(!filled.ContainsKey(t.SellOrderId)) filled[t.SellOrderId]=0;

            filled[t.BuyOrderId] += t.Quantity;
            filled[t.SellOrderId] += t.Quantity;
        }
        
        int loadedOpenOrders = 0;

        foreach (var o in orders)
        {
            int filledQty = filled.TryGetValue(o.OrderId, out int f) ? f : 0;
            int remaining = o.OriginalQuantity - filledQty;
            
            if (canceled.Contains(o.OrderId))
            continue;

            if (remaining <=0)
            continue; // fully filled, not resting

            // Parse side text from DB ("Buy"/"Sell")
            OrderSide side = o.Side.Equals("Buy", StringComparison.OrdinalIgnoreCase)
                ? OrderSide.Buy
                :OrderSide.Sell;


            var order = new Order(o.OrderId, side, o.Symbol, o.OriginalQuantity, o.Price);
            order.RemainingQuantity = remaining;
            order.Account = string.IsNullOrWhiteSpace(o.Account) ? "anonymous" : o.Account;

            var book = _books.GetOrAdd(order.Symbol, _ => new OrderBook(order.Symbol));
            book.AddRestingOrder(order);
            _openOrders[order.OrderId] = order;
            
            loadedOpenOrders++;
        }
        
        Console.WriteLine($"Rebuilt books from DB. Open (resting) orders loaded: {loadedOpenOrders}");
    }
    
    public string GetLatestOrders(int limit)
    {
        var lines = _db.GetLatestOrders(limit);
        if (lines.Count == 0) return "No orders in DB.\n";
        return string.Join('\n', lines) + "\n";
    }

    public string GetLatestTrades(int limit)
    {
        var lines = _db.GetLatestTrades(limit);
        if (lines.Count == 0) return "No trades in DB.\n";
        return string.Join('\n', lines) + "\n";
    }

    public string GetBook(string symbol)
    {
        // If a symbol has no in-memory book yet, say so.
        if (!_books.TryGetValue(symbol, out var book))
            return $"No in-memory book for {symbol}. (No open orders.)\n";

        return book.GetDetailedBook();
    }

    public MatchResult Submit(Order incoming)
    {
        incoming.OrderId = Interlocked.Increment(ref _nextOrderId);

        // Save the order as soon as it gets an ID
        _db.InsertOrder(incoming);

         _db.InsertEvent(
            ExchangeEventType.OrderAccepted,
            incoming.Account,
            incoming.OrderId,
            new { kind = "LIMIT", incoming.Side, incoming.Symbol, qty = incoming.OriginalQuantity, incoming.Price }
        );


        OrderBook book = _books.GetOrAdd(incoming.Symbol, _ => new OrderBook(incoming.Symbol));
        MatchResult result = book.Match(incoming);

        // Save any trades that happened
        PersistTradesAndEvents(result);
        

        // Incoming is open only if it still has remaining quantity
        if (incoming.RemainingQuantity > 0)
            _openOrders[incoming.OrderId] = incoming;
        else
            _openOrders.TryRemove(incoming.OrderId, out _);

        // Any resting orders fully filled should be removed from open tracking
        foreach (var filledId in result.FilledOrderIds)
            _openOrders.TryRemove(filledId, out _);

        return result;
    }
   
    public string CancelOrder(long orderId, string account)
    {
        // Only allow cancel if it's currently open in memory
        if (!_openOrders.TryRemove(orderId, out var order))
            return $"ERROR: Order {orderId} is not open (already filled, canceled, or unknown).\n";

        // Remove from the order book
        if (_books.TryGetValue(order.Symbol, out var book))
        {
            bool removed = book.RemoveOrder(orderId);
            if (!removed)
                return $"ERROR: Order {orderId} not found in book (unexpected).\n";
        }

        if (!order.Account.Equals(account, StringComparison.OrdinalIgnoreCase))
        {
            // Put it back because it wasn't theirs
            _openOrders[orderId] = order;
            return $"ERROR: Order {orderId} does not belong to {account}.\n";
        }


        // Persist the cancellation
        _db.InsertCancellation(orderId);

        _db.InsertEvent(
            ExchangeEventType.OrderCanceled,
            account,
            orderId,
            new { }
        );


        return $"OK: Canceled Order {orderId}\n";
    }
    
    public string GetPositions(string account)
    {
        var lines = _db.GetPositions(account);
        return "=== POSITIONS ===\n" + string.Join('\n', lines) + "\n";
    }

    public string GetPnl(string account)
    {
        // Average-cost method per symbol:
        // - Track position (shares)
        // - Track cost basis of open position
        // - Realized P&L when reducing/closing position

        var trades = _db.GetAllTradesForPnl();
        var lastPrices = _db.GetLastTradePrices();

        // state per symbol
        var pos = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var costBasis = new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase); // total cost of current open position
        var realized = new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase);

        foreach (var t in trades)
        {
            bool isBuyer = t.Buyer.Equals(account, StringComparison.OrdinalIgnoreCase);
            bool isSeller = t.Seller.Equals(account, StringComparison.OrdinalIgnoreCase);

            if (!isBuyer && !isSeller) continue;

            if (!pos.ContainsKey(t.Symbol))
            {
                pos[t.Symbol] = 0;
                costBasis[t.Symbol] = 0m;
                realized[t.Symbol] = 0m;
            }

            int p = pos[t.Symbol];
            decimal cb = costBasis[t.Symbol];

            // Helper: average cost of current position (if any)
            decimal AvgCost(int position, decimal basis) =>
                position == 0 ? 0m : Math.Abs(basis / position);

            if (isBuyer)
            {
                // Buying increases position.
                // If currently short (p < 0), this buy covers some/all of the short and realizes P&L.
                int buyQty = t.Quantity;

                if (p < 0)
                {
                    int coverQty = Math.Min(buyQty, -p);
                    decimal avgShort = AvgCost(p, cb); // cb for short will be negative basis (we keep sign via updates below)

                    // Realized P&L for covering short: (avgShort - buyPrice) * coverQty
                    realized[t.Symbol] += (avgShort - t.Price) * coverQty;

                    // Reduce short position
                    p += coverQty;

                    // Reduce cost basis accordingly (short basis is negative)
                    // avgShort = abs(cb/p_old). For short, cb is negative and p is negative.
                    // Remove the portion covered:
                    cb += avgShort * coverQty; // adds back toward zero (since cb is negative)

                    buyQty -= coverQty;
                }

                // Any remaining buyQty opens/increases a long position
                if (buyQty > 0)
                {
                    p += buyQty;
                    cb += t.Price * buyQty;
                }
            }
            else if (isSeller)
            {
                // Selling decreases position.
                // If currently long (p > 0), this sell closes some/all of the long and realizes P&L.
                int sellQty = t.Quantity;

                if (p > 0)
                {
                    int closeQty = Math.Min(sellQty, p);
                    decimal avgLong = AvgCost(p, cb);

                    // Realized P&L for selling long: (sellPrice - avgLong) * closeQty
                    realized[t.Symbol] += (t.Price - avgLong) * closeQty;

                    // Reduce long position
                    p -= closeQty;

                    // Reduce cost basis accordingly
                    cb -= avgLong * closeQty;

                    sellQty -= closeQty;
                }

                // Any remaining sellQty opens/increases a short position
                if (sellQty > 0)
                {
                    p -= sellQty;
                    cb -= t.Price * sellQty; // short basis stored as negative
                }
            }

            pos[t.Symbol] = p;
            costBasis[t.Symbol] = cb;
        }

        var sb = new StringBuilder();
        sb.AppendLine($"=== PNL {account} ===");

        decimal totalUnreal = 0m;
        decimal totalReal = 0m;

        foreach (var sym in pos.Keys.OrderBy(s => s))
        {
            int p = pos[sym];
            decimal cb = costBasis[sym];
            decimal real = realized[sym];
            totalReal += real;

            decimal avg = p == 0 ? 0m : Math.Abs(cb / p);
            decimal last = lastPrices.TryGetValue(sym, out var lp) ? lp : 0m;

            decimal unreal = 0m;
            if (p != 0 && last != 0m)
            {
                // Long: (last - avg) * qty
                // Short: (avg - last) * qtyAbs
                unreal = p > 0 ? (last - avg) * p : (avg - last) * Math.Abs(p);
            }

            totalUnreal += unreal;

            sb.AppendLine($"{sym} pos={p} avgCost={avg:0.00} last={last:0.00} unreal={unreal:0.00} realized={real:0.00}");
        }

        if (pos.Count == 0)
            sb.AppendLine("(no trades)");

        sb.AppendLine($"TOTAL unreal={totalUnreal:0.00} realized={totalReal:0.00}");
        return sb.ToString();
    }
    
    public MatchResult SubmitMarket(Order incoming)
    {
        incoming.OrderId = Interlocked.Increment(ref _nextOrderId);

        // Persist the incoming market order like any other order
        _db.InsertOrder(incoming);
        
        _db.InsertEvent(
            ExchangeEventType.OrderAccepted,
            incoming.Account,
            incoming.OrderId,
            new { kind = "MARKET", incoming.Side, incoming.Symbol, qty = incoming.OriginalQuantity }
        );

        OrderBook book = _books.GetOrAdd(incoming.Symbol, _ => new OrderBook(incoming.Symbol));
        MatchResult result = book.MatchMarket(incoming);

        PersistTradesAndEvents(result);

        // market orders never rest, so only track it as open if it somehow still has remaining (we will NOT keep it open)
        _openOrders.TryRemove(incoming.OrderId, out _);

        foreach (var filledId in result.FilledOrderIds)
            _openOrders.TryRemove(filledId, out _);

        return result;
    }

    public MatchResult SubmitIoc(Order incoming)
    {
        incoming.OrderId = Interlocked.Increment(ref _nextOrderId);
        _db.InsertOrder(incoming);

        _db.InsertEvent(
            ExchangeEventType.OrderAccepted,
            incoming.Account,
            incoming.OrderId,
            new { kind = "IOC", incoming.Side, incoming.Symbol, qty = incoming.OriginalQuantity, incoming.Price }
        );


        OrderBook book = _books.GetOrAdd(incoming.Symbol, _ => new OrderBook(incoming.Symbol));
        MatchResult result = book.MatchIoc(incoming);

        if (incoming.RemainingQuantity > 0)
        {
            _db.InsertEvent(
                ExchangeEventType.OrderPartialCanceled,
                incoming.Account,
                incoming.OrderId,
                new { unfilled = incoming.RemainingQuantity }
            );
        }


        PersistTradesAndEvents(result);


        // IOC never rests
        _openOrders.TryRemove(incoming.OrderId, out _);

        foreach (var filledId in result.FilledOrderIds)
            _openOrders.TryRemove(filledId, out _);

        return result;
    }

    public MatchResult SubmitFok(Order incoming)
    {
        incoming.OrderId = Interlocked.Increment(ref _nextOrderId);
        _db.InsertOrder(incoming);

        _db.InsertEvent(
            ExchangeEventType.OrderAccepted,
            incoming.Account,
            incoming.OrderId,
            new { kind = "FOK", incoming.Side, incoming.Symbol, qty = incoming.OriginalQuantity, incoming.Price }
        );


        OrderBook book = _books.GetOrAdd(incoming.Symbol, _ => new OrderBook(incoming.Symbol));
        MatchResult result = book.MatchFok(incoming);

        if (result.Trades.Count == 0)
        {
            _db.InsertEvent(
                ExchangeEventType.OrderKilled,
                incoming.Account,
                incoming.OrderId,
                new { reason = "Not enough immediate liquidity to fully fill" }
            );
        }

        // If not fully fillable, MatchFok returns 0 trades and changes nothing
        PersistTradesAndEvents(result);


        // FOK never rests
        _openOrders.TryRemove(incoming.OrderId, out _);

        foreach (var filledId in result.FilledOrderIds)
            _openOrders.TryRemove(filledId, out _);

        return result;
    }
    
    public string GetLatestEvents(int limit, string? account = null)
    {
        var lines = _db.GetLatestEvents(limit, account);
        if (lines.Count == 0) return "No events.\n";
        return string.Join('\n', lines) + "\n";
    }

    private void PersistTradesAndEvents(MatchResult result)
    {
        foreach (var trade in result.Trades)
        {
            _db.InsertTrade(trade);

            _db.InsertEvent(
                ExchangeEventType.TradeExecuted,
                null,
                null,
                new
                {
                    trade.Symbol,
                    trade.Quantity,
                    trade.Price,
                    trade.BuyOrderId,
                    trade.SellOrderId,
                    trade.BuyerAccount,
                    trade.SellerAccount
                }
            );
        }
}



}