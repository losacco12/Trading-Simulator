namespace TradingServer.Core;

public class MatchResult
{
    public List<long> FilledOrderIds { get; } = new List<long>();
    public List<Trade> Trades { get; } = new List<Trade>();
    public string BookSummary { get; set; } = "";
}
