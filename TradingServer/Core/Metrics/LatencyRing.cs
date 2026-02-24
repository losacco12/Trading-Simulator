using System;

namespace TradingServer.Core.Metrics;

internal sealed class LatencyRing
{
    private readonly double[] _buf;
    private int _next;
    private int _count;
    private readonly object _lock = new();

    public LatencyRing(int capacity)
    {
        _buf = new double[capacity];
    }

    public void Add(double value)
    {
        lock (_lock)
        {
            _buf[_next] = value;
            _next = (_next + 1) % _buf.Length;
            if (_count < _buf.Length) _count++;
        }
    }

    public void Clear()
    {
        lock (_lock)
        {
            _next = 0;
            _count = 0;
        }
    }

    public LatencyStats Stats()
    {
        double[] snapshot;
        lock (_lock)
        {
            snapshot = new double[_count];
            // copy oldest->newest not required for stats, just copy any
            Array.Copy(_buf, snapshot, _count);
        }

        if (snapshot.Length == 0) return new LatencyStats();

        Array.Sort(snapshot);

        double avg = 0;
        for (int i = 0; i < snapshot.Length; i++) avg += snapshot[i];
        avg /= snapshot.Length;

        double P(double pct)
        {
            if (snapshot.Length == 1) return snapshot[0];
            double rank = pct * (snapshot.Length - 1);
            int lo = (int)Math.Floor(rank);
            int hi = (int)Math.Ceiling(rank);
            if (lo == hi) return snapshot[lo];
            double w = rank - lo;
            return snapshot[lo] * (1 - w) + snapshot[hi] * w;
        }

        return new LatencyStats
        {
            Count = snapshot.Length,
            AvgMs = avg,
            P50Ms = P(0.50),
            P95Ms = P(0.95),
            P99Ms = P(0.99),
            MaxMs = snapshot[^1]
        };
    }
}