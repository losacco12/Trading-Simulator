using System.Collections.Concurrent;
using System.Text;

namespace TradingServer.Core.MarketData;

public sealed class MarketDataBroadcaster
{
    private sealed class Subscriber
    {
        public required StreamWriter Writer { get; init; }

        // Per-symbol subscriptions
        private readonly HashSet<string> _symbols =
            new(StringComparer.OrdinalIgnoreCase);

        public object WriteLock { get; } = new object();

        public void Subscribe(string symbol)
            => _symbols.Add(symbol);

        public void Unsubscribe(string symbol)
            => _symbols.Remove(symbol);

        public bool IsSubscribed(string symbol)
            => _symbols.Contains(symbol);
    }

    private readonly ConcurrentDictionary<int, Subscriber> _clients = new();
    private long _seq = 0;

    // Called when a client connects
    public void Add(int clientId, StreamWriter writer)
    {
        
        _clients[clientId] = new Subscriber        
        {
            Writer = writer
        };
    }

    // Called when a client disconnects
    public void Remove(int clientId)
    {
        _clients.TryRemove(clientId, out _);
    }

    public bool Subscribe(int clientId, string symbol)
    {
        if (_clients.TryGetValue(clientId, out var sub))
        {
            sub.Subscribe(symbol);
            return true;
        }
        return false;
    }

    public bool Unsubscribe(int clientId, string symbol)
    {
        if (_clients.TryGetValue(clientId, out var sub))
        {
            sub.Unsubscribe(symbol);
            return true;
        }
        return false;
    }

    public void Publish(string symbol, string payload, int? excludeClientId = null)
    {
        long seq = Interlocked.Increment(ref _seq);
        string ts = DateTime.UtcNow.ToString("O"); // ISO-8601

        // Example: MD 123 2026-02-23T...Z BOOK AAPL buys=... 
        string line = $"MD {seq} {ts} {payload}";

        foreach (var kvp in _clients)
        {
            if (excludeClientId.HasValue && kvp.Key == excludeClientId.Value)
                continue;

            var sub = kvp.Value;
            if (!sub.IsSubscribed(symbol)) continue;

            try
            {
                lock (sub.WriteLock)
                {
                    sub.Writer.WriteLine(line);
                }
            }
            catch
            {
                // ignore broken pipe
            }
        }
    }
}
