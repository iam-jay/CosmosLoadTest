using System.Diagnostics;

namespace CosmosLoadTest;

// Quota-based weighted scheduler with global rate pacing.
//  - Each op has target[op] = round(pct * total).
//  - Dispatches at RequestsPerSec; each pick is weighted by REMAINING quota.
//  - When an op reaches its target it drops out and its share redistributes.
//  - Stops once TotalRequests have been dispatched (or no op can run).
public sealed class Scheduler
{
    private readonly Config _cfg;
    private readonly CosmosOperations _ops;
    private readonly DocumentPool _pool;
    private readonly Dictionary<OperationType, long> _targets;
    private readonly Dictionary<OperationType, long> _dispatched = new();
    private readonly Random _rng = new();

    public Scheduler(Config cfg, CosmosOperations ops, DocumentPool pool, Dictionary<OperationType, long> targets)
    {
        _cfg = cfg;
        _ops = ops;
        _pool = pool;
        _targets = targets;
        foreach (var op in Enum.GetValues<OperationType>())
            _dispatched[op] = 0;
    }

    public async Task RunAsync(CancellationToken ct)
    {
        long total = _targets.Values.Sum();
        long dispatched = 0;
        var sem = new SemaphoreSlim(_cfg.MaxConcurrency);
        var inflight = new List<Task>();
        var sw = Stopwatch.StartNew();

        while (dispatched < total && !ct.IsCancellationRequested)
        {
            // Pace to the global RequestsPerSec target.
            double allowed = _cfg.RequestsPerSec * sw.Elapsed.TotalSeconds;
            if (dispatched >= allowed)
            {
                await Task.Delay(2, ct);
                PruneCompleted(inflight);
                continue;
            }

            bool poolEmpty = _pool.IsEmpty;
            OperationType? pick = PickOp(poolEmpty);
            if (pick == null)
            {
                // Nothing runnable right now. If dependent ops are blocked only
                // because the pool is empty AND something can still produce, wait.
                if (poolEmpty && CanStillProduce())
                {
                    await Task.Delay(5, ct);
                    PruneCompleted(inflight);
                    continue;
                }
                break; // genuinely nothing left to do
            }

            var op = pick.Value;
            _dispatched[op]++;
            dispatched++;

            await sem.WaitAsync(ct);
            var task = Task.Run(async () =>
            {
                try { await _ops.ExecuteAsync(op); }
                finally { sem.Release(); }
            }, ct);
            inflight.Add(task);

            if (inflight.Count >= _cfg.MaxConcurrency * 2)
                PruneCompleted(inflight);
        }

        await Task.WhenAll(inflight);
    }

    private bool CanStillProduce()
    {
        foreach (var op in Enum.GetValues<OperationType>())
            if (op.ProducesDocuments() && Remaining(op) > 0) return true;
        return false;
    }

    private long Remaining(OperationType op) => _targets[op] - _dispatched[op];

    // Weighted random selection by remaining quota. When the pool is empty,
    // operations that need an existing target are excluded.
    private OperationType? PickOp(bool poolEmpty)
    {
        double totalWeight = 0;
        Span<double> weights = stackalloc double[Enum.GetValues<OperationType>().Length];
        var values = Enum.GetValues<OperationType>();
        for (int i = 0; i < values.Length; i++)
        {
            var op = values[i];
            long rem = Remaining(op);
            double w = 0;
            if (rem > 0 && !(poolEmpty && op.NeedsExistingTarget()))
                w = rem;
            weights[i] = w;
            totalWeight += w;
        }
        if (totalWeight <= 0) return null;

        double r = _rng.NextDouble() * totalWeight;
        double acc = 0;
        for (int i = 0; i < values.Length; i++)
        {
            acc += weights[i];
            if (r <= acc && weights[i] > 0) return values[i];
        }
        // Fallback: return last op with positive weight.
        for (int i = values.Length - 1; i >= 0; i--)
            if (weights[i] > 0) return values[i];
        return null;
    }

    private static void PruneCompleted(List<Task> tasks)
    {
        for (int i = tasks.Count - 1; i >= 0; i--)
            if (tasks[i].IsCompleted) tasks.RemoveAt(i);
    }

    public IReadOnlyDictionary<OperationType, long> Dispatched => _dispatched;
}
