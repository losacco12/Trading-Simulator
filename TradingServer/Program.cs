using TradingServer.Core;
using TradingServer.Protocol;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Globalization;
using System.IO;
using TradingServer.Core.Metrics;





Console.WriteLine("Starting TradingServer...");

int port = 5000;


// Listener
TcpListener listener = new TcpListener(IPAddress.Any, port);
listener.Start();
Console.WriteLine($"Server listening on port {port}.");

int clientNumber = 0;

// One shared exchange for all clients
string dbPath = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "trading.db"));
Console.WriteLine("DB path: " + dbPath);

TradeDatabase db = new TradeDatabase(dbPath);
var metrics = new MetricsCollector();
Exchange exchange = new Exchange(db, metrics, replayFromEvents: true);

// Server continuous loop
while (true)
{
    Console.WriteLine("Waiting for a client to connect...");

    TcpClient client = listener.AcceptTcpClient(); // Wait for connection
    clientNumber++;

    Console.WriteLine($"Client #{clientNumber} connected.");

    
 // Concurrently handle each client
     _ = Task.Run(() => HandleClient(client, clientNumber, exchange, metrics));
}


// Method ran for each client
static void HandleClient(TcpClient client, int clientId, Exchange exchange, MetricsCollector metrics)
{
    try
    {
        using NetworkStream stream = client.GetStream();
        using var reader = new StreamReader(stream, new UTF8Encoding(false));
        using var writer = new StreamWriter(stream, new UTF8Encoding(false)) { AutoFlush = true };
        string? sessionAccount = null;


        // Continue reading messages from this client until disconnected
        while (true)
        {

            
            // Read exactly one command line (null means client disconnected)
            string? raw = reader.ReadLine();
            if (raw == null)
            {
                Console.WriteLine($"Client #{clientId} disconnected.");
                break;
            }

            raw = raw.Trim();
            
            // One single wrapper owns: IncCommands + stopwatch + try/catch + WriteResponse
            HandleOneCommand(raw, exchange, metrics, ref sessionAccount, writer);

        }
    }
    catch (Exception ex)
    {
        Console.WriteLine($"Client #{clientId} error:\n{ex}");
    }
    finally
    {
        client.Close();
    }
}

static void HandleOneCommand(
    string raw,
    Exchange exchange,
    MetricsCollector metrics,
    ref string? sessionAccount,
    StreamWriter writer)
{
    metrics.IncCommands();
    var sw = System.Diagnostics.Stopwatch.StartNew();

    string response = "ERROR: Unknown";

    try
    {
        response = RouteCommand(raw, exchange, metrics, ref sessionAccount);
    }
    catch (Exception ex)
    {
        // Any unexpected server error = errors_total++
        metrics.IncErrors();
        response = $"ERROR: Server exception: {ex.GetType().Name}";
    }
    finally
    {
        sw.Stop();
        metrics.ObserveCommandRttMs(sw.Elapsed.TotalMilliseconds);

        // Guarantee END and RTT recording happens exactly once
        WriteResponse(writer, response);
    }
}

// Writes a multi-line response and guarantees it ends with END
static void WriteResponse(StreamWriter writer, string response)
{
    string normalized = response.Replace("\r\n", "\n").Replace("\r", "\n");
    foreach (var line in normalized.Split('\n'))
    {
        if (line.Length == 0) continue;
        writer.WriteLine(line);
    }
    writer.WriteLine("END");
}

static string RouteCommand(
    string raw,
    Exchange exchange,
    MetricsCollector metrics,
    ref string? sessionAccount)
{
    if (string.IsNullOrWhiteSpace(raw))
    {
        metrics.IncErrors();
        return "ERROR: Empty command";
    }

    
    
    
    // METRICS first (so router can't swallow it)
    if (raw.Equals("METRICS", StringComparison.OrdinalIgnoreCase))
    {
        var snap = metrics.Snapshot();
        return MetricsFormatter.Format(snap);
    }

    // Then: protocol-level commands (LOGIN, BOOK, ORDERS, EVENTS, REPLAYVERIFY, etc)
    if (CommandRouter.TryHandleCommand(raw, exchange, ref sessionAccount, out string commandResponse, metrics))
    {
        return commandResponse;
    }

    // Enforce login for any order-entry commands
    if (string.IsNullOrWhiteSpace(sessionAccount))
    {
        metrics.IncErrors();
        metrics.IncOrdersRejected();
        return "ERROR: You must LOGIN first.";
    }

    // MARKET
    if (raw.StartsWith("MARKET", StringComparison.OrdinalIgnoreCase))
    {
        if (!MarketOrderParser.TryParse(raw, out var side, out var symbol, out var qty, out var parseError))
        {
            metrics.IncErrors();
            metrics.IncOrdersRejected();
            return "ERROR: " + parseError;
        }

        var marketOrder = new Order(0, side, symbol, qty, 0m) { Account = sessionAccount };
        var result = exchange.SubmitMarket(marketOrder);

        var sb = new StringBuilder();
        sb.AppendLine($"OK: Accepted MARKET {side} {symbol} qty={qty}");

        if (result.Trades.Count == 0) sb.AppendLine("NO TRADES (book empty)");
        else
            foreach (var t in result.Trades)
                sb.AppendLine($"TRADE: {t.Symbol} qty={t.Quantity} price={t.Price} ({t.BuyerAccount} BUY#{t.BuyOrderId} vs {t.SellerAccount} SELL#{t.SellOrderId})");

        if (marketOrder.RemainingQuantity > 0)
            sb.AppendLine($"UNFILLED: {marketOrder.RemainingQuantity}");

        sb.AppendLine($"BOOK: {result.BookSummary}");
        return sb.ToString();
    }

    // IOC / FOK
    if (raw.StartsWith("IOC", StringComparison.OrdinalIgnoreCase) ||
        raw.StartsWith("FOK", StringComparison.OrdinalIgnoreCase))
    {
        if (!TifOrderParser.TryParse(raw, out var tif, out var orderText, out var tifError))
        {
            metrics.IncErrors();
            metrics.IncOrdersRejected();
            return "ERROR: " + tifError;
        }

        if (!OrderParser.TryParseOrder(orderText, out Order? tifOrder, out string parseError))
        {
            metrics.IncErrors();
            metrics.IncOrdersRejected();
            return "ERROR: " + parseError;
        }

        tifOrder!.Account = sessionAccount;

        MatchResult result = tif == TimeInForce.IOC
            ? exchange.SubmitIoc(tifOrder)
            : exchange.SubmitFok(tifOrder);

        var sb = new StringBuilder();
        sb.AppendLine($"OK: Accepted {tif} {tifOrder.Side} {tifOrder.Symbol} qty={tifOrder.OriginalQuantity} price={tifOrder.Price}");

        if (result.Trades.Count == 0)
        {
            if (tif == TimeInForce.IOC && tifOrder.RemainingQuantity > 0)
                sb.AppendLine($"CANCELED: Unfilled remainder qty={tifOrder.RemainingQuantity}");
            else
                sb.AppendLine("NO TRADES");
        }
        else
        {
            foreach (var t in result.Trades)
                sb.AppendLine($"TRADE: {t.Symbol} qty={t.Quantity} price={t.Price} ({t.BuyerAccount} BUY#{t.BuyOrderId} vs {t.SellerAccount} SELL#{t.SellOrderId})");
        }

        if (tif == TimeInForce.IOC && tifOrder.RemainingQuantity > 0)
            sb.AppendLine($"CANCELED: Unfilled remainder qty={tifOrder.RemainingQuantity}");

        sb.AppendLine($"BOOK: {result.BookSummary}");
        return sb.ToString();
    }

    // Regular LIMIT orders (BUY/SELL)
    if (OrderParser.TryParseOrder(raw, out Order? order, out string error))
    {
        order!.Account = sessionAccount;

        MatchResult result = exchange.Submit(order);

        var sb = new StringBuilder();
        sb.AppendLine($"OK: Accepted {order.Side} {order.Symbol} qty={order.OriginalQuantity} price={order.Price}");

        if (result.Trades.Count == 0) sb.AppendLine("NO TRADES");
        else
            foreach (var t in result.Trades)
                sb.AppendLine($"TRADE: {t.Symbol} qty={t.Quantity} price={t.Price} ({t.BuyerAccount} BUY#{t.BuyOrderId} vs {t.SellerAccount} SELL#{t.SellOrderId})");

        sb.AppendLine($"BOOK: {result.BookSummary}");
        return sb.ToString();
    }

    // Unknown command
    metrics.IncErrors();
    return "ERROR: Unknown command";
}

