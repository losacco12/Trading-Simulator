namespace TradingServer.Core.Replay;

public sealed class ReplayState
{
    public Dictionary<string, HashSet<long>> BookRestingIds { get; } =
        new(StringComparer.OrdinalIgnoreCase);

    public Dictionary<long, int> RemainingQtyByOrderId { get; } = new();

    public HashSet<long> OpenOrderIds { get; } = new();

    public long MaxSeenOrderId { get; set; } = 0;
}