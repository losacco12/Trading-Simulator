using Microsoft.Data.Sqlite;

Console.WriteLine("Reading trading.db...\n");

using var connection = new SqliteConnection("Data Source=trading.db");
connection.Open();

Console.WriteLine("=== Orders ===");
var orders = connection.CreateCommand();
orders.CommandText = @"
SELECT OrderId, Side, Symbol, OriginalQuantity, Price, CreatedUtc
FROM Orders
ORDER BY OrderId;
";

using (var reader = orders.ExecuteReader())
{
    while (reader.Read())
    {
        Console.WriteLine(
            $"OrderId={reader.GetInt64(0)}, " +
            $"{reader.GetString(1)} {reader.GetString(2)} " +
            $"qty={reader.GetInt32(3)} price={reader.GetDecimal(4)} @ {reader.GetString(5)}"
        );
    }
}

Console.WriteLine("\n=== Trades ===");
var trades = connection.CreateCommand();
trades.CommandText = @"
SELECT TradeId, Symbol, Quantity, Price, BuyOrderId, SellOrderId, CreatedUtc
FROM Trades
ORDER BY TradeId;
";

using (var reader = trades.ExecuteReader())
{
    while (reader.Read())
    {
        Console.WriteLine(
            $"TradeId={reader.GetInt64(0)}, " +
            $"{reader.GetString(1)} qty={reader.GetInt32(2)} " +
            $"price={reader.GetDecimal(3)} " +
            $"(BUY#{reader.GetInt64(4)} SELL#{reader.GetInt64(5)}) @ {reader.GetString(6)}"
        );
    }
}

