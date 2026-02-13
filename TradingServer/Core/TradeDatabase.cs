using Microsoft.Data.Sqlite;

namespace TradingServer.Core;

public class TradeDatabase
{
    private readonly string _connectionString;

    public TradeDatabase(string dbFilePath)
    {
        _connectionString = $"Data Source={dbFilePath}";
        Initialize();
    }
    
    private void EnsureColumn(SqliteConnection connection, string table, string column, string columnType)
    {
        var check = connection.CreateCommand();
        check.CommandText = $"PRAGMA table_info({table});";

        using var reader = check.ExecuteReader();
        while (reader.Read())
        {
            string existing = reader.GetString(1);
            if (existing.Equals(column, StringComparison.OrdinalIgnoreCase))
                return;
        }

        var alter = connection.CreateCommand();
        alter.CommandText = $"ALTER TABLE {table} ADD COLUMN {column} {columnType};";
        alter.ExecuteNonQuery();
    }
    
    public long GetMaxOrderId()
    {
        using var connection = new SqliteConnection(_connectionString);
        connection.Open();

        var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT IFNULL(MAX(OrderId), 0) FROM Orders;";

        object? result = cmd.ExecuteScalar();
        return Convert.ToInt64(result);
    }

    private void Initialize()
    {
        using var connection = new SqliteConnection(_connectionString);
        connection.Open();

        var createOrders = connection.CreateCommand();
        createOrders.CommandText =
        @"
        CREATE TABLE IF NOT EXISTS Orders (
            OrderId INTEGER PRIMARY KEY,
            Side TEXT NOT NULL,
            Symbol TEXT NOT NULL,
            OriginalQuantity INTEGER NOT NULL,
            Price REAL NOT NULL,
            CreatedUtc TEXT NOT NULL
        );
        ";
        createOrders.ExecuteNonQuery();

        var createTrades = connection.CreateCommand();
        createTrades.CommandText =
        @"
        CREATE TABLE IF NOT EXISTS Trades (
            TradeId INTEGER PRIMARY KEY AUTOINCREMENT,
            Symbol TEXT NOT NULL,
            Quantity INTEGER NOT NULL,
            Price REAL NOT NULL,
            BuyOrderId INTEGER NOT NULL,
            SellOrderId INTEGER NOT NULL,
            CreatedUtc TEXT NOT NULL
        );
        ";
        createTrades.ExecuteNonQuery();

        var createCancels = connection.CreateCommand();
        createCancels.CommandText =
        @"
        CREATE TABLE IF NOT EXISTS Cancellations (
            OrderId INTEGER PRIMARY KEY,
            CanceledUtc TEXT NOT NULL
        );
        ";
        createCancels.ExecuteNonQuery();

        EnsureColumn(connection, "Orders", "Account", "TEXT");
        EnsureColumn(connection, "Trades", "BuyerAccount", "TEXT");
        EnsureColumn(connection, "Trades", "SellerAccount", "TEXT");

    }

    public void InsertOrder(Order order)
    {
        using var connection = new SqliteConnection(_connectionString);
        connection.Open();

        var cmd = connection.CreateCommand();
        cmd.CommandText =
        @"
        INSERT INTO Orders (OrderId, Side, Symbol, OriginalQuantity, Price, CreatedUtc, Account)
        VALUES ($id, $side, $symbol, $qty, $price, $utc, $acct);
        ";

        cmd.Parameters.AddWithValue("$id", order.OrderId);
        cmd.Parameters.AddWithValue("$acct", order.Account);
        cmd.Parameters.AddWithValue("$side", order.Side.ToString());
        cmd.Parameters.AddWithValue("$symbol", order.Symbol);
        cmd.Parameters.AddWithValue("$qty", order.OriginalQuantity);
        cmd.Parameters.AddWithValue("$price", order.Price);
        cmd.Parameters.AddWithValue("$utc", DateTime.UtcNow.ToString("o"));

        cmd.ExecuteNonQuery();
    }

    public void InsertTrade(Trade trade)
    {
        using var connection = new SqliteConnection(_connectionString);
        connection.Open();

        var cmd = connection.CreateCommand();
        cmd.CommandText =
        @"
        INSERT INTO Trades (Symbol, Quantity, Price, BuyOrderId, SellOrderId, CreatedUtc, BuyerAccount, SellerAccount)
        VALUES ($symbol, $qty, $price, $buyId, $sellId, $utc, $buyer, $seller);
        ";

        cmd.Parameters.AddWithValue("$symbol", trade.Symbol);
        cmd.Parameters.AddWithValue("$buyer", trade.BuyerAccount);
        cmd.Parameters.AddWithValue("$seller", trade.SellerAccount);
        cmd.Parameters.AddWithValue("$qty", trade.Quantity);
        cmd.Parameters.AddWithValue("$price", trade.Price);
        cmd.Parameters.AddWithValue("$buyId", trade.BuyOrderId);
        cmd.Parameters.AddWithValue("$sellId", trade.SellOrderId);
        cmd.Parameters.AddWithValue("$utc", DateTime.UtcNow.ToString("o"));

        cmd.ExecuteNonQuery();
    }
    
    public List<string> GetLatestOrders(int limit)
    {
        using var connection = new SqliteConnection(_connectionString);
        connection.Open();

        var cmd = connection.CreateCommand();
        cmd.CommandText = @"
            SELECT OrderId, Side, Symbol, OriginalQuantity, Price, CreatedUtc
            FROM Orders
            ORDER BY OrderId DESC
            LIMIT $limit;
            ";
        cmd.Parameters.AddWithValue("$limit", limit);

        var lines = new List<string>();

        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            long id = reader.GetInt64(0);
            string side = reader.GetString(1);
            string symbol = reader.GetString(2);
            int qty = reader.GetInt32(3);
            decimal price = reader.GetDecimal(4);
            string utc = reader.GetString(5);

            lines.Add($"OrderId={id} {side} {symbol} qty={qty} price={price} @ {utc}");
        }

        return lines;
    }

    public List<string> GetLatestTrades(int limit)
    {
        using var connection = new SqliteConnection(_connectionString);
        connection.Open();

        var cmd = connection.CreateCommand();
        cmd.CommandText = @"
            SELECT TradeId, Symbol, Quantity, Price, BuyOrderId, SellOrderId, CreatedUtc, BuyerAccount, SellerAccount
            ORDER BY TradeId DESC
            LIMIT $limit;
            ";
        cmd.Parameters.AddWithValue("$limit", limit);

        var lines = new List<string>();

        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            long tradeId = reader.GetInt64(0);
            string symbol = reader.GetString(1);
            int qty = reader.GetInt32(2);
            decimal price = reader.GetDecimal(3);
            long buyId = reader.GetInt64(4);
            long sellId = reader.GetInt64(5);
            string utc = reader.GetString(6);

            string buyer = reader.IsDBNull(7) ? "?" : reader.GetString(7);
            string seller = reader.IsDBNull(8) ? "?" : reader.GetString(8);

            lines.Add($"TradeId={tradeId} {symbol} qty={qty} price={price} ({buyer} BUY#{buyId} vs {seller} SELL#{sellId}) @ {utc}");

        }

        return lines;
    }
    
    public List<(long OrderId, string Side, string Symbol, int OriginalQuantity, decimal Price, string? Account)> GetAllOrders()
    {
        using var connection = new SqliteConnection(_connectionString);
        connection.Open();

        var cmd = connection.CreateCommand();
        cmd.CommandText = @"
            SELECT OrderId, Side, Symbol, OriginalQuantity, Price, Account
            FROM Orders
            ORDER BY OrderId ASC;
            ";

        var rows = new List<(long, string, string, int, decimal, string?)>();

        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            long id = reader.GetInt64(0);
            string side = reader.GetString(1);
            string symbol = reader.GetString(2);
            int qty = reader.GetInt32(3);
            decimal price = reader.GetDecimal(4);
            string? acct = reader.IsDBNull(5) ? null : reader.GetString(5);

            rows.Add((id, side, symbol, qty, price, acct));
        }

        return rows;
    }

    public List<(long TradeId, int Quantity, long BuyOrderId, long SellOrderId)> GetAllTrades()
    {
        using var connection = new SqliteConnection(_connectionString);
        connection.Open();

        var cmd = connection.CreateCommand();
        cmd.CommandText = @"
            SELECT TradeId, Quantity, BuyOrderId, SellOrderId
            FROM Trades
            ORDER BY TradeId ASC;
            ";

        var rows = new List<(long, int, long, long)>();

        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            long tradeId = reader.GetInt64(0);
            int qty = reader.GetInt32(1);
            long buyId = reader.GetInt64(2);
            long sellId = reader.GetInt64(3);

            rows.Add((tradeId, qty, buyId, sellId));
        }

        return rows;
    }

    public void InsertCancellation(long orderId)
    {
        using var connection = new SqliteConnection(_connectionString);
        connection.Open();

        var cmd = connection.CreateCommand();
        cmd.CommandText =
            @"
            INSERT OR IGNORE INTO Cancellations (OrderId, CanceledUtc)
            VALUES ($id, $utc);
            ";
        cmd.Parameters.AddWithValue("$id", orderId);
        cmd.Parameters.AddWithValue("$utc", DateTime.UtcNow.ToString("o"));

        cmd.ExecuteNonQuery();
    }

    public HashSet<long> GetAllCanceledOrderIds()
    {
        using var connection = new SqliteConnection(_connectionString);
        connection.Open();

        var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT OrderId FROM Cancellations;";

        var set = new HashSet<long>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
            set.Add(reader.GetInt64(0));

        return set;
    }

    public List<string> GetPositions(string account)
    {
        using var connection = new SqliteConnection(_connectionString);
        connection.Open();

        var cmd = connection.CreateCommand();
        cmd.CommandText = @"
            SELECT Symbol,
                SUM(CASE WHEN BuyerAccount = $acct THEN Quantity ELSE 0 END) -
                SUM(CASE WHEN SellerAccount = $acct THEN Quantity ELSE 0 END) AS NetQty
            FROM Trades
            GROUP BY Symbol
            HAVING NetQty != 0
            ORDER BY Symbol;
            ";
        cmd.Parameters.AddWithValue("$acct", account);

        var lines = new List<string>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            string symbol = reader.GetString(0);
            long net = reader.GetInt64(1);
            lines.Add($"{symbol}: {net}");
        }

        if (lines.Count == 0)
            lines.Add("(no open positions)");

        return lines;
    }

    public List<(long TradeId, string Symbol, int Quantity, decimal Price, string Buyer, string Seller)> GetAllTradesForPnl()
    {
        using var connection = new SqliteConnection(_connectionString);
        connection.Open();

        var cmd = connection.CreateCommand();
        cmd.CommandText = @"
            SELECT TradeId, Symbol, Quantity, Price,
                IFNULL(BuyerAccount, 'anonymous') as Buyer,
                IFNULL(SellerAccount, 'anonymous') as Seller
            FROM Trades
            ORDER BY TradeId ASC;
            ";

        var rows = new List<(long, string, int, decimal, string, string)>();

        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            long id = reader.GetInt64(0);
            string sym = reader.GetString(1);
            int qty = reader.GetInt32(2);
            decimal price = reader.GetDecimal(3);
            string buyer = reader.GetString(4);
            string seller = reader.GetString(5);

            rows.Add((id, sym, qty, price, buyer, seller));
        }

        return rows;
    }

    public Dictionary<string, decimal> GetLastTradePrices()
    {
        using var connection = new SqliteConnection(_connectionString);
        connection.Open();

        var cmd = connection.CreateCommand();
        cmd.CommandText = @"
        SELECT t.Symbol, t.Price
        FROM Trades t
        JOIN (
            SELECT Symbol, MAX(TradeId) AS MaxId
            FROM Trades
            GROUP BY Symbol
        ) last ON last.Symbol = t.Symbol AND last.MaxId = t.TradeId;
        ";

        var map = new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase);

        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            string sym = reader.GetString(0);
            decimal price = reader.GetDecimal(1);
            map[sym] = price;
        }

        return map;
    }



}