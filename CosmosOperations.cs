using System.Diagnostics;
using Microsoft.Azure.Cosmos;

namespace CosmosLoadTest;

// Executes each of the 9 document operations against a Cosmos container,
// recording latency / success / error / RU into metrics.
public sealed class CosmosOperations
{
    private readonly Container _container;
    private readonly DocumentPool _pool;
    private readonly Config _cfg;
    private readonly Metrics _metrics;
    private readonly string _filler;
    private long _seq;
    private static readonly Random _rng = new();

    public CosmosOperations(Container container, DocumentPool pool, Config cfg, Metrics metrics)
    {
        _container = container;
        _pool = pool;
        _cfg = cfg;
        _metrics = metrics;
        _filler = BuildFiller(cfg.DocumentSizeBytes);
    }

    public static string BuildFiller(int targetBytes)
    {
        // Base doc overhead (ids, keys, numbers) is ~180 bytes; pad the rest.
        int fill = Math.Max(0, targetBytes - 180);
        return new string('x', fill);
    }

    private string RandomPk() => $"pk-{_cfg.PartitionKeyStart + _rng.Next(_cfg.PartitionKeyCount)}";

    private Doc NewDoc(string id = null, string pk = null)
    {
        id ??= Guid.NewGuid().ToString("N");
        pk ??= RandomPk();
        return new Doc
        {
            Id = id,
            Pk = pk,
            Category = "load",
            Seq = Interlocked.Increment(ref _seq),
            Ts = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            Data = _filler,
        };
    }

    public async Task ExecuteAsync(OperationType op)
    {
        var sw = Stopwatch.StartNew();
        var m = _metrics.Ops[op];
        try
        {
            double ru = op switch
            {
                OperationType.Read => await DoRead(),
                OperationType.Query => await DoQuery(),
                OperationType.Create => await DoCreate(),
                OperationType.ReadFeed => await DoReadFeed(),
                OperationType.Upsert => await DoUpsert(),
                OperationType.Patch => await DoPatch(),
                OperationType.Replace => await DoReplace(),
                OperationType.Delete => await DoDelete(),
                OperationType.Batch => await DoBatch(),
                _ => 0
            };
            sw.Stop();
            m.RecordSuccess(sw.Elapsed.TotalMilliseconds, ru);
        }
        catch (Exception ex)
        {
            sw.Stop();
            m.RecordError(sw.Elapsed.TotalMilliseconds, Reason(ex));
        }
    }

    private static string Reason(Exception ex)
    {
        if (ex is CosmosException ce)
            return $"{(int)ce.StatusCode} {ce.StatusCode}";
        return ex.GetType().Name;
    }

    // ---- Operations -------------------------------------------------------

    private async Task<double> DoCreate()
    {
        var doc = NewDoc();
        var resp = await _container.CreateItemAsync(doc, new PartitionKey(doc.Pk));
        _pool.Add(doc.Id, doc.Pk);
        return resp.RequestCharge;
    }

    private async Task<double> DoUpsert()
    {
        // Reuse an existing id when available (true upsert path), else create new.
        string id, pk;
        if (!_pool.TryPeek(out id, out pk)) { id = null; pk = null; }
        var doc = NewDoc(id, pk);
        var resp = await _container.UpsertItemAsync(doc, new PartitionKey(doc.Pk));
        if (id == null) _pool.Add(doc.Id, doc.Pk);
        return resp.RequestCharge;
    }

    private async Task<double> DoRead()
    {
        if (!_pool.TryPeek(out var id, out var pk))
            throw new InvalidOperationException("NoTargetInPool");
        var resp = await _container.ReadItemAsync<Doc>(id, new PartitionKey(pk));
        return resp.RequestCharge;
    }

    private async Task<double> DoReplace()
    {
        if (!_pool.TryPeek(out var id, out var pk))
            throw new InvalidOperationException("NoTargetInPool");
        var doc = NewDoc(id, pk);
        doc.UpdatedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var resp = await _container.ReplaceItemAsync(doc, id, new PartitionKey(pk));
        return resp.RequestCharge;
    }

    private async Task<double> DoPatch()
    {
        if (!_pool.TryPeek(out var id, out var pk))
            throw new InvalidOperationException("NoTargetInPool");
        var ops = new List<PatchOperation>
        {
            PatchOperation.Set("/updatedAt", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds())
        };
        var resp = await _container.PatchItemAsync<Doc>(id, new PartitionKey(pk), ops);
        return resp.RequestCharge;
    }

    private async Task<double> DoDelete()
    {
        if (!_pool.TryTake(out var id, out var pk))
            throw new InvalidOperationException("NoTargetInPool");
        var resp = await _container.DeleteItemAsync<Doc>(id, new PartitionKey(pk));
        return resp.RequestCharge;
    }

    private async Task<double> DoQuery()
    {
        string pk = RandomPk();
        var query = new QueryDefinition(
            "SELECT TOP 10 c.id, c.seq FROM c WHERE c.category = @cat ORDER BY c.seq DESC")
            .WithParameter("@cat", "load");
        double ru = 0;
        using var it = _container.GetItemQueryIterator<Doc>(
            query,
            requestOptions: new QueryRequestOptions { PartitionKey = new PartitionKey(pk), MaxItemCount = 10 });
        while (it.HasMoreResults)
        {
            var page = await it.ReadNextAsync();
            ru += page.RequestCharge;
        }
        return ru;
    }

    private async Task<double> DoReadFeed()
    {
        // Read one page from the change feed (beginning) to generate feed-read load.
        double ru = 0;
        using var it = _container.GetChangeFeedIterator<Doc>(
            ChangeFeedStartFrom.Beginning(), ChangeFeedMode.Incremental,
            new ChangeFeedRequestOptions { PageSizeHint = 100 });
        if (it.HasMoreResults)
        {
            try
            {
                var page = await it.ReadNextAsync();
                ru += page.RequestCharge;
            }
            catch (CosmosException ce) when (ce.StatusCode == System.Net.HttpStatusCode.NotModified)
            {
                // No new changes — still a valid feed read.
            }
        }
        return ru;
    }

    private async Task<double> DoBatch()
    {
        // Transactional batch: two creates in the same logical partition.
        string pk = RandomPk();
        var d1 = NewDoc(null, pk);
        var d2 = NewDoc(null, pk);
        var batch = _container.CreateTransactionalBatch(new PartitionKey(pk))
            .CreateItem(d1)
            .CreateItem(d2);
        using var resp = await batch.ExecuteAsync();
        if (!resp.IsSuccessStatusCode)
            throw new InvalidOperationException($"Batch {(int)resp.StatusCode} {resp.StatusCode}");
        _pool.Add(d1.Id, pk);
        _pool.Add(d2.Id, pk);
        return resp.RequestCharge;
    }
}
