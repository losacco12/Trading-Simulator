using TradingServer.Core;

namespace TradingServer.Protocol;

public enum TimeInForce
{
    IOC,
    FOK
}

public static class TifOrderParser
{
    // Expected: IOC BUY <SYMBOL> <QTY> <PRICE>
    //           FOK SELL <SYMBOL> <QTY> <PRICE>
    public static bool TryParse(string input, out TimeInForce tif, out string orderText, out string error)
    {
        tif = TimeInForce.IOC;
        orderText = "";
        error = "";

        string[] parts = input.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 5)
        {
            error = "Expected format: IOC BUY <SYMBOL> <QTY> <PRICE> (or FOK SELL ...)";
            return false;
        }

        string prefix = parts[0].ToUpperInvariant();
        if (prefix != "IOC" && prefix != "FOK")
        {
            error = "Expected IOC or FOK";
            return false;
        }

        tif = prefix == "IOC" ? TimeInForce.IOC : TimeInForce.FOK;

        // Rebuild the normal limit order text: BUY <SYMBOL> <QTY> <PRICE>
        orderText = $"{parts[1]} {parts[2]} {parts[3]} {parts[4]}";
        return true;
    }
}
