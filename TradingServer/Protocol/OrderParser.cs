using System.Globalization;
using TradingServer.Core;

namespace TradingServer.Protocol;

public static class OrderParser
{
    // Validation <ORDER> <QUANTITY> <PRICE>
    public static bool TryParseOrder(string input, out Order? order, out string error)
    {
        order = null;
        error = "";

        // Split on spaces, remove empty chunks (handles extra spaces)
        string[] parts = input.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        
        
        
        // Validate proper number of inputs
        if (parts.Length != 4)
            {
                error = "Expected format: BUY <SYMBOL> <QTY> <PRICE>";
                return false;
            }
            
            string sideText = parts[0].ToUpperInvariant();

        
        // Validate BUY or SELL
        if (sideText != "BUY" && sideText != "SELL")
            {
                error = "First word must be BUY or SELL";
                return false;
            }

        
        string symbol = parts[1].ToUpperInvariant();
        
        
        
        // Simple symbol validation
        if (symbol.Length < 1 || symbol.Length > 5)
        {
            error = "Symbol must be 1 to 5 letters (example: AAPL)";
            return false;
        }

        foreach (char c in symbol)
        {
            if (!char.IsLetter(c))
            {
                error = "Symbol must contain only letters";
                return false;
            }
        }
        
        
        // Quantity Validation
        if (!int.TryParse(parts[2], out int qty))
        {
            error = "Quantity must be a whole number (example: 10)";
            return false;
        }

        if (qty <= 0)
        {
            error = "Quantity must be greater than 0";
            return false;
        }
        
        
        
        // Price Validation
        if (!decimal.TryParse(parts[3], NumberStyles.Number, CultureInfo.InvariantCulture, out decimal price))// Invariant culture so "100.50" works regardless of locale settings
        {
            error = "Price must be a number (example: 100.50)";
            return false;
        }

        if (price <= 0)
        {
            error = "Price must be greater than 0";
            return false;
        }
        
        OrderSide side = sideText == "BUY" ? OrderSide.Buy : OrderSide.Sell;

        order = new Order(0,side, symbol, qty, price);
        return true;
    }
}