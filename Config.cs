using System.Text.Json;
using System.Text.Json.Serialization;

namespace CosmosLoadTest;

// Runtime configuration. Loaded from a JSON file and/or overridden by CLI args.
public class Config
{
    public string Endpoint { get; set; } = "";
    public string Key { get; set; } = "";
    public string Database { get; set; } = "loadtest";
    public string Container { get; set; } = "col";

    // Provisioned RU/s used only if the tool has to create the container.
    public int ThroughputRu { get; set; } = 10000;

    // Global throughput target across ALL operations combined.
    public int RequestsPerSec { get; set; } = 100;

    // Total number of operations to execute across all op types.
    public long TotalRequests { get; set; } = 1000;

    // Target serialized size of documents created/updated.
    public int DocumentSizeBytes { get; set; } = 1024;

    // Number of distinct logical partition-key values to spread load across.
    public int PartitionKeyCount { get; set; } = 100;

    // Max concurrent in-flight operations.
    public int MaxConcurrency { get; set; } = 256;

    // Pre-seed pool settings (used ONLY when Create percentage == 0).
    public int PreSeedCount { get; set; } = 10000;
    public int PreSeedDocSizeBytes { get; set; } = 1024;

    // Per-operation percentages. Values are relative weights; they are
    // normalized so the resulting target counts sum to TotalRequests.
    // A value of 0 means the operation does not run.
    public Dictionary<string, double> Percentages { get; set; } = new()
    {
        ["Read"] = 0,
        ["Query"] = 0,
        ["Create"] = 0,
        ["ReadFeed"] = 0,
        ["Upsert"] = 0,
        ["Patch"] = 0,
        ["Replace"] = 0,
        ["Delete"] = 0,
        ["Batch"] = 0,
    };

    public string ReportPath { get; set; } = "report.html";

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        Converters = { new JsonStringEnumConverter() }
    };

    public static Config Load(string[] args)
    {
        var cfg = new Config();

        // 1. Optional config file (default appsettings.json or --config <path>)
        string configPath = GetArg(args, "--config") ?? "appsettings.json";
        if (File.Exists(configPath))
        {
            var fileCfg = JsonSerializer.Deserialize<Config>(File.ReadAllText(configPath), JsonOpts);
            if (fileCfg != null) cfg = fileCfg;
        }

        // 2. CLI overrides
        Override(args, "--endpoint", v => cfg.Endpoint = v);
        Override(args, "--key", v => cfg.Key = v);
        Override(args, "--database", v => cfg.Database = v);
        Override(args, "--container", v => cfg.Container = v);
        Override(args, "--throughput", v => cfg.ThroughputRu = int.Parse(v));
        Override(args, "--rps", v => cfg.RequestsPerSec = int.Parse(v));
        Override(args, "--total", v => cfg.TotalRequests = long.Parse(v));
        Override(args, "--docsize", v => cfg.DocumentSizeBytes = int.Parse(v));
        Override(args, "--pkcount", v => cfg.PartitionKeyCount = int.Parse(v));
        Override(args, "--concurrency", v => cfg.MaxConcurrency = int.Parse(v));
        Override(args, "--preseed", v => cfg.PreSeedCount = int.Parse(v));
        Override(args, "--report", v => cfg.ReportPath = v);

        // Per-op percentage overrides: --read --query --create ... --batch
        foreach (var name in Enum.GetNames<OperationType>())
            Override(args, "--" + name.ToLowerInvariant(), v => cfg.Percentages[name] = double.Parse(v));

        // Env fallback for secrets (handy in Azure deployment).
        if (string.IsNullOrEmpty(cfg.Endpoint))
            cfg.Endpoint = Environment.GetEnvironmentVariable("COSMOS_ENDPOINT") ?? "";
        if (string.IsNullOrEmpty(cfg.Key))
            cfg.Key = Environment.GetEnvironmentVariable("COSMOS_KEY") ?? "";

        return cfg;
    }

    // Returns normalized target counts per enabled operation, summing to TotalRequests.
    public Dictionary<OperationType, long> ComputeTargets()
    {
        var weights = new Dictionary<OperationType, double>();
        double sum = 0;
        foreach (var op in Enum.GetValues<OperationType>())
        {
            double w = Percentages.TryGetValue(op.ToString(), out var x) ? x : 0;
            if (w < 0) w = 0;
            weights[op] = w;
            sum += w;
        }
        var targets = new Dictionary<OperationType, long>();
        if (sum <= 0)
            throw new InvalidOperationException("All operation percentages are zero. Nothing to run.");

        // Largest-remainder method so the counts sum exactly to TotalRequests.
        var exact = new Dictionary<OperationType, double>();
        long floorSum = 0;
        foreach (var (op, w) in weights)
        {
            double share = (w / sum) * TotalRequests;
            exact[op] = share;
            long fl = (long)Math.Floor(share);
            targets[op] = fl;
            floorSum += fl;
        }
        long remainder = TotalRequests - floorSum;
        // Distribute leftover to ops with the largest fractional parts.
        foreach (var op in exact.OrderByDescending(kv => kv.Value - Math.Floor(kv.Value)).Select(kv => kv.Key))
        {
            if (remainder <= 0) break;
            if (weights[op] <= 0) continue; // never assign to disabled ops
            targets[op] += 1;
            remainder--;
        }
        return targets;
    }

    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(Endpoint) || Endpoint.StartsWith("<"))
            throw new InvalidOperationException("Endpoint is required (config, --endpoint, or COSMOS_ENDPOINT).");
        if (string.IsNullOrWhiteSpace(Key) || Key.StartsWith("<"))
            throw new InvalidOperationException("Key is required (config, --key, or COSMOS_KEY).");
        if (RequestsPerSec <= 0) throw new InvalidOperationException("RequestsPerSec must be > 0.");
        if (TotalRequests <= 0) throw new InvalidOperationException("TotalRequests must be > 0.");
    }

    private static string GetArg(string[] args, string name)
    {
        for (int i = 0; i < args.Length - 1; i++)
            if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase))
                return args[i + 1];
        return null;
    }

    private static void Override(string[] args, string name, Action<string> set)
    {
        var v = GetArg(args, name);
        if (v != null) set(v);
    }
}
