using System.Diagnostics;
using Microsoft.Azure.Cosmos;
using CosmosLoadTest;

Console.WriteLine("=== Cosmos DB Load Test ===");

Config cfg;
bool dryRun = args.Any(a => string.Equals(a, "--dryrun", StringComparison.OrdinalIgnoreCase));
try
{
    cfg = Config.Load(args);
    if (!dryRun) cfg.Validate();
}
catch (Exception ex)
{
    Console.Error.WriteLine("Config error: " + ex.Message);
    PrintUsage();
    return 1;
}

var targets = cfg.ComputeTargets();
long totalTarget = targets.Values.Sum();

Console.WriteLine($"Endpoint   : {cfg.Endpoint}");
Console.WriteLine($"Database   : {cfg.Database} / Container: {cfg.Container}");
Console.WriteLine($"Total ops  : {totalTarget:N0}  @ {cfg.RequestsPerSec:N0}/s  (doc {cfg.DocumentSizeBytes} B)");
Console.WriteLine("Targets    :");
foreach (var op in Enum.GetValues<OperationType>())
    if (targets[op] > 0) Console.WriteLine($"   {op,-9}: {targets[op]:N0}");

// Dry-run: validate the workload mix (quota math) without connecting.
if (dryRun)
{
    Console.WriteLine($"\n[dry-run] target counts sum to {totalTarget:N0} (TotalRequests={cfg.TotalRequests:N0}).");
    bool createIn = targets[OperationType.Create] > 0;
    Console.WriteLine($"[dry-run] Create in workload: {createIn} -> " +
        (createIn ? "documents produced during run (no pre-seed)."
                  : (AnyNeedsTarget(targets) ? $"pre-seed {cfg.PreSeedCount:N0} docs." : "no pre-seed needed.")));
    return 0;
}

bool createInWorkload = targets[OperationType.Create] > 0;

var clientOptions = new CosmosClientOptions
{
    ApplicationName = "CosmosLoadTest",
    ConnectionMode = ConnectionMode.Direct,
    AllowBulkExecution = false,
    MaxRetryAttemptsOnRateLimitedRequests = 9,
    MaxRetryWaitTimeOnRateLimitedRequests = TimeSpan.FromSeconds(30),
};

using var client = new CosmosClient(cfg.Endpoint, cfg.Key, clientOptions);

// Ensure database + container exist (partition key "/pk").
Console.WriteLine("\nEnsuring database/container exist...");
Database database = await client.CreateDatabaseIfNotExistsAsync(cfg.Database);
Container container = await database.CreateContainerIfNotExistsAsync(
    new ContainerProperties(cfg.Container, "/pk"), throughput: cfg.ThroughputRu);

var pool = new DocumentPool();
var metrics = new Metrics();
var ops = new CosmosOperations(container, pool, cfg, metrics);

// Pre-seed ONLY when Create is not part of the workload.
if (!createInWorkload && AnyNeedsTarget(targets))
{
    Console.WriteLine($"\nCreate% = 0 -> pre-seeding {cfg.PreSeedCount:N0} docs " +
                      $"({cfg.PreSeedDocSizeBytes} B) so read/replace/patch/delete have targets...");
    await PreSeed(container, pool, cfg);
    Console.WriteLine($"Pre-seed complete. Pool size: {pool.Count:N0}");
}
else if (createInWorkload)
{
    Console.WriteLine("\nCreate is in the workload -> documents are produced during the run (no pre-seed).");
}

// Run the workload.
Console.WriteLine($"\nDriving workload...");
metrics.StartUtc = DateTime.UtcNow;
var scheduler = new Scheduler(cfg, ops, pool, targets);

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

var progress = StartProgress(metrics, totalTarget, cts.Token);
await scheduler.RunAsync(cts.Token);
metrics.EndUtc = DateTime.UtcNow;
cts.Cancel();
try { await progress; } catch { /* ignore */ }

// Report.
Console.WriteLine("\n=== Summary ===");
Console.WriteLine($"Total attempts : {metrics.TotalAttempts:N0}");
Console.WriteLine($"Success        : {metrics.TotalSuccess:N0}");
Console.WriteLine($"Errors         : {metrics.TotalErrors:N0}");
Console.WriteLine($"Duration       : {metrics.Duration.TotalSeconds:N1}s");

HtmlReport.Write(cfg.ReportPath, cfg, metrics, targets, scheduler.Dispatched);
string fullReport = Path.GetFullPath(cfg.ReportPath);
Console.WriteLine($"\nHTML report written to: {fullReport}");
return 0;

// ---- helpers --------------------------------------------------------------

static bool AnyNeedsTarget(Dictionary<OperationType, long> targets) =>
    targets.Any(kv => kv.Value > 0 && kv.Key.NeedsExistingTarget());

static async Task PreSeed(Container container, DocumentPool pool, Config cfg)
{
    string filler = CosmosOperations.BuildFiller(cfg.PreSeedDocSizeBytes);
    var sem = new SemaphoreSlim(Math.Max(8, cfg.MaxConcurrency));
    var tasks = new List<Task>();
    var rng = new Random();
    long done = 0;
    var swp = Stopwatch.StartNew();

    for (int i = 0; i < cfg.PreSeedCount; i++)
    {
        await sem.WaitAsync();
        string id = $"seed-{i}";
        string pk = $"pk-{rng.Next(cfg.PartitionKeyCount)}";
        var doc = new Doc
        {
            Id = id, Pk = pk, Category = "load",
            Seq = i, Ts = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), Data = filler
        };
        tasks.Add(Task.Run(async () =>
        {
            try
            {
                await container.CreateItemAsync(doc, new PartitionKey(pk));
                pool.Add(id, pk);
            }
            catch (CosmosException ce) when (ce.StatusCode == System.Net.HttpStatusCode.Conflict)
            {
                pool.Add(id, pk); // already there from a prior run
            }
            catch { /* tolerate transient seed errors */ }
            finally
            {
                sem.Release();
                long d = Interlocked.Increment(ref done);
                if (d % 10000 == 0)
                    Console.WriteLine($"   seeded {d:N0}/{cfg.PreSeedCount:N0} ({d / swp.Elapsed.TotalSeconds:N0}/s)");
            }
        }));
    }
    await Task.WhenAll(tasks);
}

static Task StartProgress(Metrics metrics, long totalTarget, CancellationToken ct)
{
    return Task.Run(async () =>
    {
        long last = 0;
        while (!ct.IsCancellationRequested)
        {
            await Task.Delay(2000, ct);
            long attempts = metrics.TotalAttempts;
            long errs = metrics.TotalErrors;
            double rate = (attempts - last) / 2.0;
            Console.WriteLine($"   progress {attempts:N0}/{totalTarget:N0} | {rate:N0}/s | errors {errs:N0}");
            last = attempts;
        }
    }, ct);
}

static void PrintUsage()
{
    Console.WriteLine(@"
Usage: CosmosLoadTest [--config appsettings.json] [overrides]

Connection:
  --endpoint <url>        Cosmos account endpoint (or COSMOS_ENDPOINT env)
  --key <key>             Primary key (or COSMOS_KEY env)
  --database <name>       Database id (default: loadtest)
  --container <name>      Container id (default: col)
  --throughput <ru>       RU/s if container must be created (default: 10000)

Workload:
  --rps <n>               Global requests/sec (default: 100)
  --total <n>             Total operation count (default: 1000)
  --docsize <bytes>       Document size in bytes (default: 1024)
  --pkcount <n>           Distinct partition-key values (default: 100)
  --concurrency <n>       Max in-flight ops (default: 256)
  --preseed <n>           Pre-seed doc count when Create%=0 (default: 10000)
  --report <path>         HTML report output (default: report.html)

Operation percentages (relative weights, 0 disables an op):
  --read --query --create --readfeed --upsert --patch --replace --delete --batch

Example:
  CosmosLoadTest --endpoint https://acct.documents.azure.com:443/ --key KEY \
    --rps 200 --total 20000 --docsize 1024 \
    --create 60 --read 20 --replace 10 --delete 10
");
}
