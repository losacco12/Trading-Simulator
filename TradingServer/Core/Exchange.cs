using System.Collections.Concurrent;
using System.Text;
using TradingServer.Core.Events;
using System.Text.Json;
using TradingServer.Core.Replay;
using TradingServer.Core.Metrics;


namespace TradingServer.Core;

// Exchange holds order books for all symbols
public class Exchange
{
    private readonly ConcurrentDictionary<string, OrderBook> _books = new();
    private long _nextOrderId = 0;
    private readonly TradeDatabase _db;
    private readonly ConcurrentDictionary<long, Order> _openOrders = new();
    private long _maxSeenOrderId = 0;
    private readonly MetricsCollector _metrics;

    public Exchange(TradeDatabase db, MetricsCollector metrics, bool replayFromEvents = false)
    {
        _db = db;
        _metrics = metrics;

       if (replayFromEvents)
        {
            ReplayFromEvents();

            // SAFETY: Orders table might contain legacy data even if Events is empty or incomplete.
            long maxFromOrders = _db.GetMaxOrderId();

            _nextOrderId = Math.Max(maxFromOrders, _maxSeenOrderId);

            Console.WriteLine(
                $"Exchange replayed from Events. maxFromOrders={maxFromOrders} maxFromEvents={_maxSeenOrderId} NextOrderId={_nextOrderId}"
            );
        }


        else
        {
            _nextOrderId = _db.GetMaxOrderId();
            Console.WriteLine($"Exchange starting next OrderId from DB: {_nextOrderId}");
            RebuildBooksFromDatabase();
        }
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
        var sw = System.Diagnostics.Stopwatch.StartNew();
        
        incoming.OrderId = Interlocked.Increment(ref _nextOrderId);

        // Save the order as soon as it gets an ID
        _db.InsertOrder(incoming);

         _db.InsertEvent(
            ExchangeEventType.OrderAccepted,
            incoming.Account,
            incoming.OrderId,
            new { kind = "LIMIT", incoming.Side, incoming.Symbol, qty = incoming.OriginalQuantity, incoming.Price }
        );

         _metrics.IncOrdersAccepted();


        OrderBook book = _books.GetOrAdd(incoming.Symbol, _ => new OrderBook(incoming.Symbol));
        MatchResult result = book.Match(incoming);

        // Save any trades that happened
        PersistTradesAndEvents(result);
        
         if (result.Trades.Count > 0)
        {
            int volume = result.Trades.Sum(t => t.Quantity);
            _metrics.IncTrades(result.Trades.Count, volume);
        }


        // Incoming is open only if it still has remaining quantity
        if (incoming.RemainingQuantity > 0)
            _openOrders[incoming.OrderId] = incoming;
        else
            _openOrders.TryRemove(incoming.OrderId, out _);

        // Any resting orders fully filled should be removed from open tracking
        foreach (var filledId in result.FilledOrderIds)
            _openOrders.TryRemove(filledId, out _);

        sw.Stop();
        _metrics.ObserveMatchLatencyMs(sw.Elapsed.TotalMilliseconds);
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

        _metrics.IncOrdersAccepted();
        
        OrderBook book = _books.GetOrAdd(incoming.Symbol, _ => new OrderBook(incoming.Symbol));
        var sw = System.Diagnostics.Stopwatch.StartNew();
        MatchResult result = book.MatchMarket(incoming);
        sw.Stop();
        _metrics.ObserveMatchLatencyMs(sw.Elapsed.TotalMilliseconds);

        PersistTradesAndEvents(result);

         if (result.Trades.Count > 0)
            {
                int volume = result.Trades.Sum(t => t.Quantity);
                _metrics.IncTrades(result.Trades.Count, volume);
            }

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
        
        _metrics.IncOrdersAccepted();
        
        OrderBook book = _books.GetOrAdd(incoming.Symbol, _ => new OrderBook(incoming.Symbol));
        var sw = System.Diagnostics.Stopwatch.StartNew();
        MatchResult result = book.MatchIoc(incoming);
        sw.Stop();
        _metrics.ObserveMatchLatencyMs(sw.Elapsed.TotalMilliseconds);

       

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

        if (result.Trades.Count > 0)
        {
            int volume = result.Trades.Sum(t => t.Quantity);
            _metrics.IncTrades(result.Trades.Count, volume);
        }

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

        _metrics.IncOrdersAccepted();

        OrderBook book = _books.GetOrAdd(incoming.Symbol, _ => new OrderBook(incoming.Symbol));
        var sw = System.Diagnostics.Stopwatch.StartNew();
        MatchResult result = book.MatchFok(incoming);
        sw.Stop();
        _metrics.ObserveMatchLatencyMs(sw.Elapsed.TotalMilliseconds);

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
       
        if (result.Trades.Count > 0)
            {
                int volume = result.Trades.Sum(t => t.Quantity);
                _metrics.IncTrades(result.Trades.Count, volume);
            }

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

    private long GetMaxOrderIdFromOpenState()
    {
        // We want next OrderId to be >= any order id we've seen.
        // If open orders is empty, just return 0 and it will increment from 1.
        return _openOrders.Count == 0 ? 0 : _openOrders.Keys.Max();
    }  

    private void ReplayFromEvents()
    {
        var events = _db.GetAllEventsAsc();

        // Track all orders (including IOC/FOK/MARKET) so we can decrement quantities during trade replay.
        var ordersById = new Dictionary<long, Order>();

        foreach (var e in events)
        {
            string type = e.Type;

            if (type.Equals(nameof(ExchangeEventType.OrderAccepted), StringComparison.OrdinalIgnoreCase))
            {
                if (e.OrderId == null) continue;

                _maxSeenOrderId = Math.Max(_maxSeenOrderId, e.OrderId.Value);

                // DataJson contains { kind, Side, Symbol, qty, Price }
                using var doc = JsonDocument.Parse(e.DataJson);
                var root = doc.RootElement;

                string kind = root.TryGetProperty("kind", out var kindEl)
                ? GetAsString(kindEl, "LIMIT")
                : "LIMIT";


                string sideText = root.TryGetProperty("Side", out var sideEl)
                ? GetAsString(sideEl, "Buy")
                : "Buy";

                string symbol = root.TryGetProperty("Symbol", out var symEl)
                    ? GetAsString(symEl, "UNK")
                    : "UNK";

                int qty = root.GetProperty("qty").GetInt32();

                decimal price = 0m;
                if (root.TryGetProperty("Price", out var p))
                {
                    // your serializer writes decimal as JSON number
                    price = p.GetDecimal();
                }

                OrderSide side = sideText.Equals("Sell", StringComparison.OrdinalIgnoreCase) ? OrderSide.Sell : OrderSide.Buy;

                var order = new Order(e.OrderId.Value, side, symbol, qty, price)
                {
                    Account = string.IsNullOrWhiteSpace(e.Account) ? "anonymous" : e.Account,
                    RemainingQuantity = qty
                };

                ordersById[order.OrderId] = order;

                // Only LIMIT orders can rest. (MARKET/IOC/FOK never rest.)
                if (kind.Equals("LIMIT", StringComparison.OrdinalIgnoreCase))
                {
                    var book = _books.GetOrAdd(symbol, _ => new OrderBook(symbol));
                    book.AddRestingOrder(order);
                    _openOrders[order.OrderId] = order;
                }
            }
            else if (type.Equals(nameof(ExchangeEventType.TradeExecuted), StringComparison.OrdinalIgnoreCase))
            {
                using var doc = JsonDocument.Parse(e.DataJson);
                var root = doc.RootElement;

                long buyId = root.GetProperty("BuyOrderId").GetInt64();
                long sellId = root.GetProperty("SellOrderId").GetInt64();
                int q = root.GetProperty("Quantity").GetInt32();

                _maxSeenOrderId = Math.Max(_maxSeenOrderId, buyId);
                _maxSeenOrderId = Math.Max(_maxSeenOrderId, sellId);

                ApplyFill(ordersById, buyId, q);
                ApplyFill(ordersById, sellId, q);
            }
           else if (type.Equals(nameof(ExchangeEventType.OrderCanceled), StringComparison.OrdinalIgnoreCase))
            {
                if (e.OrderId != null)
                {
                    _maxSeenOrderId = Math.Max(_maxSeenOrderId, e.OrderId.Value);
                    RemoveOpenOrder(e.OrderId.Value);
                }
            }

            else if (type.Equals(nameof(ExchangeEventType.OrderPartialCanceled), StringComparison.OrdinalIgnoreCase))
            {
                if (e.OrderId != null)
                {
                    _maxSeenOrderId = Math.Max(_maxSeenOrderId, e.OrderId.Value);
                    RemoveOpenOrder(e.OrderId.Value);
                }
            }

            else if (type.Equals(nameof(ExchangeEventType.OrderKilled), StringComparison.OrdinalIgnoreCase))
            {
                // FOK killed: it never rests, nothing to remove
            }
        }
    }
    
    public string ReplayVerify(int limit = 20000)
    {
        // Build replay-local state from events
        var replayState = BuildReplayState(limit);

        // Build live state snapshot (no mutation)
        var liveState = BuildLiveStateSnapshot();

        // Compare + format
        return ReplayVerifier.Compare(replayState, liveState).ToPrettyString();
    }

    private ReplayState BuildReplayState(int limit)
    {
        var state = new ReplayState();

        var events = _db.GetEventsAsc(limit);

        // Track all orders so trades can decrement remaining
        var ordersById = new Dictionary<long, Order>();

        void EnsureBook(string sym)
        {
            if (!state.BookRestingIds.ContainsKey(sym))
                state.BookRestingIds[sym] = new HashSet<long>();
        }

        foreach (var e in events)
        {
            if (e.OrderId != null)
                state.MaxSeenOrderId = Math.Max(state.MaxSeenOrderId, e.OrderId.Value);

            if (e.Type.Equals(nameof(ExchangeEventType.OrderAccepted), StringComparison.OrdinalIgnoreCase))
            {
                if (e.OrderId == null) continue;

                using var doc = JsonDocument.Parse(e.DataJson);
                var root = doc.RootElement;

                string kind = root.TryGetProperty("kind", out var k) ? GetAsString(k, "LIMIT") : "LIMIT";
                string sideText = root.TryGetProperty("Side", out var s) ? GetAsString(s, "Buy") : "Buy";
                string symbol = root.TryGetProperty("Symbol", out var sym) ? GetAsString(sym, "UNK") : "UNK";
                int qty = root.TryGetProperty("qty", out var q) ? q.GetInt32() : 0;

                decimal price = 0m;
                if (root.TryGetProperty("Price", out var p) && p.ValueKind != JsonValueKind.Null)
                    price = p.GetDecimal();

                var side = sideText.Equals("Sell", StringComparison.OrdinalIgnoreCase) ? OrderSide.Sell : OrderSide.Buy;

                var o = new Order(e.OrderId.Value, side, symbol, qty, price)
                {
                    Account = string.IsNullOrWhiteSpace(e.Account) ? "anonymous" : e.Account,
                    RemainingQuantity = qty
                };

                ordersById[o.OrderId] = o;
                state.RemainingQtyByOrderId[o.OrderId] = o.RemainingQuantity;

                // Only LIMIT rests
                if (kind.Equals("LIMIT", StringComparison.OrdinalIgnoreCase))
                {
                    EnsureBook(symbol);
                    state.BookRestingIds[symbol].Add(o.OrderId);
                    state.OpenOrderIds.Add(o.OrderId);
                }
            }
            else if (e.Type.Equals(nameof(ExchangeEventType.TradeExecuted), StringComparison.OrdinalIgnoreCase))
            {
                using var doc = JsonDocument.Parse(e.DataJson);
                var root = doc.RootElement;

                long buyId = root.GetProperty("BuyOrderId").GetInt64();
                long sellId = root.GetProperty("SellOrderId").GetInt64();
                int fillQty = root.GetProperty("Quantity").GetInt32();

                state.MaxSeenOrderId = Math.Max(state.MaxSeenOrderId, buyId);
                state.MaxSeenOrderId = Math.Max(state.MaxSeenOrderId, sellId);

                ApplyFillReplay(state, ordersById, buyId, fillQty);
                ApplyFillReplay(state, ordersById, sellId, fillQty);
            }
            else if (
                e.Type.Equals(nameof(ExchangeEventType.OrderCanceled), StringComparison.OrdinalIgnoreCase) ||
                e.Type.Equals(nameof(ExchangeEventType.OrderPartialCanceled), StringComparison.OrdinalIgnoreCase))
            {
                if (e.OrderId == null) continue;

                if (ordersById.TryGetValue(e.OrderId.Value, out var ord))
                {
                    state.OpenOrderIds.Remove(ord.OrderId);
                    if (state.BookRestingIds.TryGetValue(ord.Symbol, out var set))
                        set.Remove(ord.OrderId);
                }
            }
        }

        return state;
    }

    private ReplayState BuildLiveStateSnapshot()
    {
        var live = new ReplayState();

        // open ids
        foreach (var id in _openOrders.Keys)
            live.OpenOrderIds.Add(id);

        // books
        foreach (var (sym, book) in _books)
        {
            live.BookRestingIds[sym] = new HashSet<long>(book.GetRestingOrderIds());
        }

        // remaining qty (optional but strong)
        foreach (var kvp in _openOrders)
            live.RemainingQtyByOrderId[kvp.Key] = kvp.Value.RemainingQuantity;

        return live;
    }

    private static void ApplyFillReplay(ReplayState state, Dictionary<long, Order> ordersById, long orderId, int fillQty)
    {
        if (!ordersById.TryGetValue(orderId, out var o))
            return;

        o.RemainingQuantity -= fillQty;
        if (o.RemainingQuantity < 0) o.RemainingQuantity = 0;

        state.RemainingQtyByOrderId[orderId] = o.RemainingQuantity;

        if (o.RemainingQuantity == 0)
        {
            // If it was a resting LIMIT, remove from open/books
            if (state.OpenOrderIds.Remove(orderId))
            {
                if (state.BookRestingIds.TryGetValue(o.Symbol, out var set))
                    set.Remove(orderId);
            }
        }
    }

    private void ApplyFill(Dictionary<long, Order> ordersById, long orderId, int fillQty)
    {
        if (!ordersById.TryGetValue(orderId, out var order))
            return;

        order.RemainingQuantity -= fillQty;
        if (order.RemainingQuantity < 0) order.RemainingQuantity = 0;

        if (order.RemainingQuantity == 0)
        {
            // If it was a resting limit order, remove it.
            RemoveOpenOrder(orderId);
        }
    }

    private void RemoveOpenOrder(long orderId)
    {
        if (_openOrders.TryRemove(orderId, out var order))
        {
            if (_books.TryGetValue(order.Symbol, out var book))
            {
                book.RemoveOrder(orderId);
            }
        }
    }
    
    public string ReplayCheckExpanded(int limit = 20000)
    {
        // Re-simulate state from events WITHOUT touching live in-memory state.
        var report = BuildReplayReport(limit);
        return report.ToPrettyString();
    }
    private ReplayReport BuildReplayReport(int limit)
    {
        var report = new ReplayReport();

        var events = _db.GetEventsAsc(limit);
        report.EventsProcessed = events.Count;

        // replay state (local, not your live state)
        var ordersById = new Dictionary<long, Order>();
        var open = new HashSet<long>(); // open LIMIT orders only
        var books = new Dictionary<string, HashSet<long>>(StringComparer.OrdinalIgnoreCase);

        void CountType(string t)
        {
            report.EventTypeCounts.TryGetValue(t, out int c);
            report.EventTypeCounts[t] = c + 1;
        }

        void EnsureBook(string symbol)
        {
            if (!books.ContainsKey(symbol))
                books[symbol] = new HashSet<long>();
        }

        foreach (var e in events)
        {
            CountType(e.Type);

            // Track max order id from event column
            if (e.OrderId != null)
                report.MaxSeenOrderId = Math.Max(report.MaxSeenOrderId, e.OrderId.Value);

            if (e.Type.Equals(nameof(ExchangeEventType.OrderAccepted), StringComparison.OrdinalIgnoreCase))
            {
                if (e.OrderId == null)
                {
                    report.Warnings.Add($"OrderAccepted missing OrderId (EventId={e.EventId}).");
                    continue;
                }

                using var doc = JsonDocument.Parse(e.DataJson);
                var root = doc.RootElement;

                string kind = root.TryGetProperty("kind", out var k) ? GetAsString(k, "LIMIT") : "LIMIT";
                report.AcceptedKindCounts.TryGetValue(kind, out int kc);
                report.AcceptedKindCounts[kind] = kc + 1;
                string sideText = root.TryGetProperty("Side", out var s) ? GetAsString(s, "Buy") : "Buy";
                string symbol   = root.TryGetProperty("Symbol", out var sym) ? GetAsString(sym, "UNK") : "UNK";
                int qty = root.TryGetProperty("qty", out var q) ? q.GetInt32() : 0;

                decimal price = 0m;
                if (root.TryGetProperty("Price", out var p) && p.ValueKind != JsonValueKind.Null)
                    price = p.GetDecimal();

                var side = sideText.Equals("Sell", StringComparison.OrdinalIgnoreCase) ? OrderSide.Sell : OrderSide.Buy;

                var order = new Order(e.OrderId.Value, side, symbol, qty, price)
                {
                    Account = string.IsNullOrWhiteSpace(e.Account) ? "anonymous" : e.Account,
                    RemainingQuantity = qty
                };
                ordersById[order.OrderId] = order;

                // Only LIMIT rests
                if (kind.Equals("LIMIT", StringComparison.OrdinalIgnoreCase))
                {
                    EnsureBook(symbol);
                    books[symbol].Add(order.OrderId);
                    open.Add(order.OrderId);
                }
            }
            else if (e.Type.Equals(nameof(ExchangeEventType.TradeExecuted), StringComparison.OrdinalIgnoreCase))
            {
                using var doc = JsonDocument.Parse(e.DataJson);
                var root = doc.RootElement;

                long buyId = root.GetProperty("BuyOrderId").GetInt64();
                long sellId = root.GetProperty("SellOrderId").GetInt64();
                int qty = root.GetProperty("Quantity").GetInt32();
                string symbol = root.TryGetProperty("Symbol", out var sym) ? GetAsString(sym, "UNK") : "UNK";


                report.MaxSeenOrderId = Math.Max(report.MaxSeenOrderId, buyId);
                report.MaxSeenOrderId = Math.Max(report.MaxSeenOrderId, sellId);

                report.Trades += 1;
                report.TradeVolume += qty;

                ApplyFillForReport(report, ordersById, open, books, buyId, qty, symbol, "BUY");
                ApplyFillForReport(report, ordersById, open, books, sellId, qty, symbol, "SELL");
            }
            else if (e.Type.Equals(nameof(ExchangeEventType.OrderCanceled), StringComparison.OrdinalIgnoreCase) ||
                    e.Type.Equals(nameof(ExchangeEventType.OrderPartialCanceled), StringComparison.OrdinalIgnoreCase))
            {
                if (e.OrderId == null)
                {
                    report.Warnings.Add($"{e.Type} missing OrderId (EventId={e.EventId}).");
                    continue;
                }

                // remove if it was a resting LIMIT
                if (ordersById.TryGetValue(e.OrderId.Value, out var ord))
                {
                    open.Remove(ord.OrderId);
                    if (books.TryGetValue(ord.Symbol, out var set))
                        set.Remove(ord.OrderId);
                }
            }
            else if (e.Type.Equals(nameof(ExchangeEventType.OrderKilled), StringComparison.OrdinalIgnoreCase))
            {
                // FOK killed never rests. Nothing required.
            }
        }

        // Consistency summary (based on replay-local state)
        report.BooksCount = books.Count;
        report.OpenOrdersCount = open.Count;
        report.OrdersInBooksCount = books.Values.Sum(s => s.Count);

        // Replay-local consistency checks:
        // Every order in any book should be in open
        foreach (var (sym, set) in books)
        {
            foreach (var id in set)
            {
                if (!open.Contains(id))
                {
                    report.InBooksNotOpen++;
                    report.Warnings.Add($"Book has order {id} for {sym} but open-set does not.");
                }
            }
        }

        // Every open order should be in its book
        foreach (var id in open)
        {
            if (!ordersById.TryGetValue(id, out var o))
            {
                report.UnknownOrderRefs++;
                report.Warnings.Add($"Open-set contains unknown order id {id}.");
                continue;
            }
            if (!books.TryGetValue(o.Symbol, out var set) || !set.Contains(id))
            {
                report.OpenNotInBooks++;
                report.Warnings.Add($"Open order {id} ({o.Symbol}) not present in books map.");
            }
        }

        // Live-memory consistency check too (nice bonus):
        // Compare current _openOrders vs actual live books you have
        var liveBookIds = new HashSet<long>();
        foreach (var b in _books.Values)
            foreach (var id in b.GetRestingOrderIds())
                liveBookIds.Add(id);

        foreach (var id in _openOrders.Keys)
            if (!liveBookIds.Contains(id))
                report.Warnings.Add($"LIVE mismatch: _openOrders has {id} but books do not.");

        foreach (var id in liveBookIds)
            if (!_openOrders.ContainsKey(id))
                report.Warnings.Add($"LIVE mismatch: books have {id} but _openOrders does not.");

        return report;
    }

    private static void ApplyFillForReport(
        ReplayReport report,
        Dictionary<long, Order> ordersById,
        HashSet<long> open,
        Dictionary<string, HashSet<long>> books,
        long orderId,
        int fillQty,
        string symbolFromTrade,
        string sideLabel)
    {
        if (!ordersById.TryGetValue(orderId, out var order))
        {
            report.UnknownOrderRefs++;
            report.Warnings.Add($"Trade references unknown {sideLabel} orderId={orderId} (symbol={symbolFromTrade}).");
            return;
        }

        order.RemainingQuantity -= fillQty;

        if (order.RemainingQuantity < 0)
        {
            report.NegativeRemainingDetected++;
            report.Warnings.Add($"Order {orderId} remaining went negative after fillQty={fillQty}. Clamping to 0.");
            order.RemainingQuantity = 0;
        }

        // If it was a resting LIMIT order and is now filled, remove from open/books
        if (order.RemainingQuantity == 0)
        {
            if (open.Remove(orderId))
            {
                if (books.TryGetValue(order.Symbol, out var set))
                    set.Remove(orderId);
            }
        }
    }

    private static string GetAsString(JsonElement element, string fallback = "")
    {
        return element.ValueKind switch
        {
            JsonValueKind.String => element.GetString() ?? fallback,
            JsonValueKind.Number => element.ToString(),   // safest, handles int/decimal
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            JsonValueKind.Null => fallback,
            JsonValueKind.Undefined => fallback,
            _ => element.ToString() ?? fallback
        };
    }



}