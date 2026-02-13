using TradingServer.Core;
using TradingServer.Protocol;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Globalization;




Console.WriteLine("Starting TradingServer...");

int port = 5000;


// Listener
TcpListener listener = new TcpListener(IPAddress.Any, port);
listener.Start();
Console.WriteLine($"Server listening on port {port}.");

int clientNumber = 0;

// One shared exchange for all clients
TradeDatabase db = new TradeDatabase("trading.db");
Exchange exchange = new Exchange(db);

// Server continuous loop
while (true)
{
    Console.WriteLine("Waiting for a client to connect...");

    TcpClient client = listener.AcceptTcpClient(); // Wait for connection
    clientNumber++;

    Console.WriteLine($"Client #{clientNumber} connected.");

    
 // Concurrently handle each client
     _ = Task.Run(() => HandleClient(client, clientNumber, exchange));
}


// Method ran for each client
static void HandleClient(TcpClient client, int clientId, Exchange exchange)
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

            if (raw.Length == 0)
            {
                // Respond with END so client doesn't hang waiting
                writer.WriteLine("ERROR: Empty command");
                writer.WriteLine("END");
                continue;
            }

            // Handle commands first
            if (CommandRouter.TryHandleCommand(raw, exchange, ref sessionAccount, out string commandResponse))
            {
                WriteResponse(writer, commandResponse);
                continue;
            }

            if (raw.StartsWith("MARKET", StringComparison.OrdinalIgnoreCase))
            {
                if (string.IsNullOrWhiteSpace(sessionAccount))
                {
                    WriteResponse(writer, "ERROR: You must LOGIN first.\n");
                    continue;
                }

                if (!MarketOrderParser.TryParse(raw, out var side, out var symbol, out var qty, out var parseError))
                {
                    WriteResponse(writer, $"ERROR: {parseError}\n");
                    continue;
                }

                // Market orders use price=0 (ignored). We still persist it.
                var marketOrder = new Order(0, side, symbol, qty, 0m)
                {
                    Account = sessionAccount
                };

                MatchResult result = exchange.SubmitMarket(marketOrder);

                var sb = new StringBuilder();
                sb.AppendLine($"OK: Accepted MARKET {side} {symbol} qty={qty}");

                if (result.Trades.Count == 0)
                {
                    sb.AppendLine("NO TRADES (book empty)");
                }
                else
                {
                    foreach (var t in result.Trades)
                    {
                        sb.AppendLine($"TRADE: {t.Symbol} qty={t.Quantity} price={t.Price} ({t.BuyerAccount} BUY#{t.BuyOrderId} vs {t.SellerAccount} SELL#{t.SellOrderId})");
                    }
                }

                if (marketOrder.RemainingQuantity > 0)
                    sb.AppendLine($"UNFILLED: {marketOrder.RemainingQuantity}");

                sb.AppendLine($"BOOK: {result.BookSummary}");

                WriteResponse(writer, sb.ToString());
                continue;
            }


            Console.WriteLine($"Client #{clientId} says: {raw}");

            if (OrderParser.TryParseOrder(raw, out Order? order, out string error))
            {
                if (string.IsNullOrWhiteSpace(sessionAccount))
                {
                    WriteResponse(writer, "ERROR: You must LOGIN first.\n");
                    continue;
                }

                order!.Account = sessionAccount;

                MatchResult result = exchange.Submit(order!);

                var sb = new StringBuilder();
                sb.AppendLine($"OK: Accepted {order!.Side} {order.Symbol} qty={order.OriginalQuantity} price={order.Price}");

                if (result.Trades.Count == 0)
                {
                    sb.AppendLine("NO TRADES");
                }
                else
                {
                    foreach (var t in result.Trades)
                    {
                        sb.AppendLine(
                            $"TRADE: {t.Symbol} qty={t.Quantity} price={t.Price} " +
                            $"({t.BuyerAccount} BUY#{t.BuyOrderId} vs {t.SellerAccount} SELL#{t.SellOrderId})"
                        );
                    }

                }

                sb.AppendLine($"BOOK: {result.BookSummary}");

                WriteResponse(writer, sb.ToString());
            }
            else
            {
                WriteResponse(writer, $"ERROR: {error}");
            }
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


// Writes a multi-line response and guarantees it ends with END
static void WriteResponse(StreamWriter writer, string response)
{
    // Normalize line endings and write line-by-line
    string normalized = response.Replace("\r\n", "\n").Replace("\r", "\n");
    string[] lines = normalized.Split('\n');

    foreach (var line in lines)
    {
        if (line.Length == 0) continue; // optional: skip blank lines
        writer.WriteLine(line);
    }

    writer.WriteLine("END");
}