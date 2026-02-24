using System.Diagnostics;
using System.Threading;

namespace TradingServer.Core.Metrics;

public sealed class MetricsCollector
{
    private long _commands;
    private long _ordersAccepted;
    private long _ordersRejected;
    private long _trades;
    private long _tradeVolume;
    private long _errors;

    private readonly LatencyRing _commandRtt = new(capacity: 50_000);
    private readonly LatencyRing _matchLatency = new(capacity: 50_000);

    public void IncCommands() => Interlocked.Increment(ref _commands);
    public void IncOrdersAccepted() => Interlocked.Increment(ref _ordersAccepted);
    public void IncOrdersRejected() => Interlocked.Increment(ref _ordersRejected);
    public void IncTrades(int tradeCount, int volume)
    {
        Interlocked.Add(ref _trades, tradeCount);
        Interlocked.Add(ref _tradeVolume, volume);
    }
    public void IncErrors() => Interlocked.Increment(ref _errors);

    public void ObserveCommandRttMs(double ms) => _commandRtt.Add(ms);
    public void ObserveMatchLatencyMs(double ms) => _matchLatency.Add(ms);

    public MetricsSnapshot Snapshot()
    {
        return new MetricsSnapshot
        {
            CommandsTotal = Interlocked.Read(ref _commands),
            OrdersAcceptedTotal = Interlocked.Read(ref _ordersAccepted),
            OrdersRejectedTotal = Interlocked.Read(ref _ordersRejected),
            TradesTotal = Interlocked.Read(ref _trades),
            TradeVolumeTotal = Interlocked.Read(ref _tradeVolume),
            ErrorsTotal = Interlocked.Read(ref _errors),
            CommandRtt = _commandRtt.Stats(),
            MatchLatency = _matchLatency.Stats()
        };
    }

    public void Reset()
    {
        Interlocked.Exchange(ref _commands, 0);
        Interlocked.Exchange(ref _ordersAccepted, 0);
        Interlocked.Exchange(ref _ordersRejected, 0);
        Interlocked.Exchange(ref _trades, 0);
        Interlocked.Exchange(ref _tradeVolume, 0);
        Interlocked.Exchange(ref _errors, 0);

        _commandRtt.Clear();
        _matchLatency.Clear();
    }
}