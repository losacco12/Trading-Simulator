using TradingServer.Core;

namespace TradingServer.Protocol;

public static class MarketOrderParser
{
    // Expected MARKET BUY <SYMBOL> <QTY>
    //          MARKET SELL <SYMBOL> <QTY>

    public static bool TryParse(string input, out OrderSide side, out string symbol, out int qty, out string error)
    {
        side = OrderSide.Buy;
        symbol = "";
        qty = 0;
        error="";

        string[] parts = input.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        
        //Validate correct number of parts
        if (parts.Length != 4)
        {
            error = "Expected format: MARKET BUY <SYMBOL> <QTY>";
            return false;
        }
        
        // Check if market
        if (!parts[0].Equals("MARKET", StringComparison.OrdinalIgnoreCase))
        {
            error = "Expected MARKET";
            return false;
        }

        //Check Side
        string sideText = parts[1].ToUpperInvariant();
        if (sideText != "BUY" && sideText != "SELL")
        {
            error = "Second word must be BUY or SELL";
            return false;
        }


        side = sideText == "BUY" ? OrderSide.Buy : OrderSide.Sell;

        symbol = parts[2].ToUpperInvariant();
        
        // Symbol length validation
        if (symbol.Length < 1 || symbol.Length > 5)
        {
            error = "Symbol must be 1 to 5 letters (example: AAPL)";
            return false;
        }

        // Symbol character validation
        foreach (char c in symbol)
        {
            if (!char.IsLetter(c))
            {
                error = "Symbol must contain only letters";
                return false;
            }
        }

        // Validate quantity is whole number of shares
        if (!int.TryParse(parts[3], out qty) || qty <= 0)
        {
            error = "Quantity must be a whole number > 0";
            return false;
        }

        return true;

    }
}