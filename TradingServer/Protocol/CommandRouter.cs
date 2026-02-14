using TradingServer.Core;

namespace TradingServer.Protocol;

public static class CommandRouter
{
    public static bool TryHandleCommand(string input, Exchange exchange, ref string? sessionAccount, out string response)
    {
        response = "";

        string[] parts = input.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0) return false;

        string cmd = parts[0].ToUpperInvariant();

        if (cmd == "BOOK" && parts.Length == 2)
        {
            string symbol = parts[1].ToUpperInvariant();
            response = exchange.GetBook(symbol);
            return true;
        }

        if (cmd == "ORDERS")
        {
            int limit = 10;
            if (parts.Length == 2 && int.TryParse(parts[1], out int parsed) && parsed > 0)
                limit = parsed;

            response = exchange.GetLatestOrders(limit);
            return true;
        }

        if (cmd == "TRADES")
        {
            int limit = 10;
            if (parts.Length == 2 && int.TryParse(parts[1], out int parsed) && parsed > 0)
                limit = parsed;

            response = exchange.GetLatestTrades(limit);
            return true;
        }
        
        if (cmd == "CANCEL" && parts.Length == 2 && long.TryParse(parts[1], out long id))
        {
        if (string.IsNullOrWhiteSpace(sessionAccount))
            {
                response = "ERROR: You must LOGIN first.\n";
                return true;
            }

            response = exchange.CancelOrder(id, sessionAccount);
            return true;
        }

        if (cmd == "LOGIN" && parts.Length == 2)
        {
            sessionAccount = parts[1];
            response = $"OK: Logged in as {sessionAccount}\n";
            return true;
        }

        if (cmd == "POSITIONS")
        {
            if (string.IsNullOrWhiteSpace(sessionAccount))
            {
                response = "ERROR: You must LOGIN first.\n";
                return true;
            }

            response = exchange.GetPositions(sessionAccount);
            return true;
        }

        if (cmd == "PNL")
        {
            if (string.IsNullOrWhiteSpace(sessionAccount))
            {
                response = "ERROR: You must LOGIN first.\n";
                return true;
            }

            response = exchange.GetPnl(sessionAccount);
            return true;
        }

        if (cmd == "EVENTS")
        {
            int limit = 10;
            string? acct = null;

            if (parts.Length >= 2 && int.TryParse(parts[1], out int parsed) && parsed > 0)
                limit = parsed;

            if (parts.Length >= 3)
                acct = parts[2];

            response = exchange.GetLatestEvents(limit, acct);
            return true;
        }

        if (cmd == "REPLAYCHECK")
        {
            int limit = 20000;
            if (parts.Length == 2 && int.TryParse(parts[1], out int parsed) && parsed > 0)
                limit = parsed;

            response = exchange.ReplayCheckExpanded(limit);
            return true;
        }


        return false;
    }  
}