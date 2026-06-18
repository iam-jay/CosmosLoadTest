using System.Collections.Concurrent;

namespace CosmosLoadTest;

// Per-operation metrics with thread-safe counters and a bounded latency sample
// (reservoir) for percentile calculation without unbounded memory growth.
public sealed class OpMetrics
{
    private const int MaxSamples = 100_000;

    private long _attempts;
    private long _success;
    private long _errors;
    private double _totalLatencyMs;
    private double _maxLatencyMs;
    private double _totalRuCharge;

    private readonly object _sampleLock = new();
    private readonly List<double> _latencySamples = new();
    private long _sampleSeen;
    private readonly Random _rng = new();

    private readonly ConcurrentDictionary<string, long> _errorReasons = new();

    public long Attempts => Interlocked.Read(ref _attempts);
    public long Success => Interlocked.Read(ref _success);
    public long Errors => Interlocked.Read(ref _errors);
    public double TotalLatencyMs => _totalLatencyMs;
    public double MaxLatencyMs => _maxLatencyMs;
    public double TotalRuCharge => _totalRuCharge;
    public IReadOnlyDictionary<string, long> ErrorReasons => _errorReasons;

    public void RecordSuccess(double latencyMs, double ru)
    {
        Interlocked.Increment(ref _attempts);
        Interlocked.Increment(ref _success);
        AddLatency(latencyMs);
        AddDouble(ref _totalRuCharge, ru);
    }

    public void RecordError(double latencyMs, string reason)
    {
        Interlocked.Increment(ref _attempts);
        Interlocked.Increment(ref _errors);
        AddLatency(latencyMs);
        _errorReasons.AddOrUpdate(reason, 1, (_, c) => c + 1);
    }

    private void AddLatency(double latencyMs)
    {
        AddDouble(ref _totalLatencyMs, latencyMs);
        UpdateMax(latencyMs);
        lock (_sampleLock)
        {
            _sampleSeen++;
            if (_latencySamples.Count < MaxSamples)
                _latencySamples.Add(latencyMs);
            else
            {
                // Reservoir sampling to keep a representative set.
                long r = (long)(_rng.NextDouble() * _sampleSeen);
                if (r < MaxSamples) _latencySamples[(int)r] = latencyMs;
            }
        }
    }

    public (double p50, double p95, double p99) Percentiles()
    {
        lock (_sampleLock)
        {
            if (_latencySamples.Count == 0) return (0, 0, 0);
            var sorted = _latencySamples.ToArray();
            Array.Sort(sorted);
            return (Pct(sorted, 0.50), Pct(sorted, 0.95), Pct(sorted, 0.99));
        }
    }

    private static double Pct(double[] sorted, double p)
    {
        int idx = (int)Math.Ceiling(p * sorted.Length) - 1;
        idx = Math.Clamp(idx, 0, sorted.Length - 1);
        return sorted[idx];
    }

    private static void AddDouble(ref double target, double value)
    {
        double init, computed;
        do
        {
            init = target;
            computed = init + value;
        } while (Interlocked.CompareExchange(ref target, computed, init) != init);
    }

    private void UpdateMax(double value)
    {
        double init;
        do
        {
            init = _maxLatencyMs;
            if (value <= init) return;
        } while (Interlocked.CompareExchange(ref _maxLatencyMs, value, init) != init);
    }
}

public sealed class Metrics
{
    public Dictionary<OperationType, OpMetrics> Ops { get; } = new();
    public DateTime StartUtc { get; set; }
    public DateTime EndUtc { get; set; }

    public Metrics()
    {
        foreach (var op in Enum.GetValues<OperationType>())
            Ops[op] = new OpMetrics();
    }

    public TimeSpan Duration => EndUtc - StartUtc;
    public long TotalAttempts => Ops.Values.Sum(o => o.Attempts);
    public long TotalSuccess => Ops.Values.Sum(o => o.Success);
    public long TotalErrors => Ops.Values.Sum(o => o.Errors);
}
