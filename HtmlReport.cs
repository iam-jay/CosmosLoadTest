using System.Globalization;
using System.Text;

namespace CosmosLoadTest;

// Renders a self-contained HTML report of the workload run.
public static class HtmlReport
{
    public static void Write(string path, Config cfg, Metrics metrics,
        Dictionary<OperationType, long> targets, IReadOnlyDictionary<OperationType, long> dispatched)
    {
        double minutes = Math.Max(metrics.Duration.TotalMinutes, 1e-9);
        var sb = new StringBuilder();

        sb.Append("""
<!DOCTYPE html>
<html lang="en">
<head>
<meta charset="utf-8"/>
<meta name="viewport" content="width=device-width, initial-scale=1"/>
<title>Cosmos Load Test Report</title>
<style>
  body { font-family: Segoe UI, Arial, sans-serif; margin: 24px; color: #222; }
  h1 { margin-bottom: 4px; }
  .sub { color: #666; margin-top: 0; }
  table { border-collapse: collapse; width: 100%; margin: 16px 0; }
  th, td { border: 1px solid #ddd; padding: 8px 10px; text-align: right; }
  th { background: #0b5394; color: #fff; text-align: right; }
  th:first-child, td:first-child { text-align: left; }
  tr:nth-child(even) { background: #f6f8fa; }
  .ok { color: #137333; font-weight: 600; }
  .err { color: #c5221f; font-weight: 600; }
  .cards { display: flex; gap: 16px; flex-wrap: wrap; margin: 16px 0; }
  .card { border: 1px solid #ddd; border-radius: 8px; padding: 14px 18px; min-width: 140px; }
  .card .v { font-size: 26px; font-weight: 700; }
  .card .l { color: #666; font-size: 13px; }
  .muted { color: #888; font-size: 12px; }
  code { background:#f0f0f0; padding:1px 4px; border-radius:3px; }
</style>
</head>
<body>
""");

        sb.Append($"<h1>Cosmos DB Load Test Report</h1>\n");
        sb.Append($"<p class='sub'>{Esc(cfg.Endpoint)} &mdash; db <code>{Esc(cfg.Database)}</code> / container <code>{Esc(cfg.Container)}</code></p>\n");

        // Summary cards
        long total = metrics.TotalAttempts;
        long success = metrics.TotalSuccess;
        long errors = metrics.TotalErrors;
        double overallRate = total / Math.Max(metrics.Duration.TotalSeconds, 1e-9);
        sb.Append("<div class='cards'>\n");
        sb.Append(Card("Total Ops", total.ToString("N0")));
        sb.Append(Card("Success", $"<span class='ok'>{success:N0}</span>"));
        sb.Append(Card("Errors", $"<span class='err'>{errors:N0}</span>"));
        sb.Append(Card("Success Rate", Pct(success, total)));
        sb.Append(Card("Duration", $"{metrics.Duration.TotalSeconds:N1}s"));
        sb.Append(Card("Throughput", $"{overallRate:N1}/s"));
        sb.Append("</div>\n");

        sb.Append($"<p class='muted'>Started {metrics.StartUtc:u} &middot; Ended {metrics.EndUtc:u} &middot; " +
                  $"Target total {targets.Values.Sum():N0} &middot; Requested rate {cfg.RequestsPerSec:N0}/s &middot; " +
                  $"Doc size {cfg.DocumentSizeBytes:N0} B</p>\n");

        // Per-operation table
        sb.Append("<h2>Per-Operation Results</h2>\n<table>\n<thead><tr>");
        foreach (var h in new[] { "Operation", "Target", "Attempts", "Success", "Errors",
                                  "Success %", "Ops/min", "Success/min", "p50 ms", "p95 ms", "p99 ms", "Avg RU" })
            sb.Append($"<th>{h}</th>");
        sb.Append("</tr></thead>\n<tbody>\n");

        foreach (var op in Enum.GetValues<OperationType>())
        {
            var m = metrics.Ops[op];
            long tgt = targets.TryGetValue(op, out var t) ? t : 0;
            if (tgt == 0 && m.Attempts == 0) continue; // hide unused ops
            var (p50, p95, p99) = m.Percentiles();
            double opsMin = m.Attempts / minutes;
            double succMin = m.Success / minutes;
            double avgRu = m.Success > 0 ? m.TotalRuCharge / m.Success : 0;
            sb.Append("<tr>");
            sb.Append($"<td>{op}</td>");
            sb.Append($"<td>{tgt:N0}</td>");
            sb.Append($"<td>{m.Attempts:N0}</td>");
            sb.Append($"<td class='ok'>{m.Success:N0}</td>");
            sb.Append($"<td class='{(m.Errors > 0 ? "err" : "")}'>{m.Errors:N0}</td>");
            sb.Append($"<td>{Pct(m.Success, m.Attempts)}</td>");
            sb.Append($"<td>{opsMin:N1}</td>");
            sb.Append($"<td>{succMin:N1}</td>");
            sb.Append($"<td>{p50:N1}</td>");
            sb.Append($"<td>{p95:N1}</td>");
            sb.Append($"<td>{p99:N1}</td>");
            sb.Append($"<td>{avgRu:N2}</td>");
            sb.Append("</tr>\n");
        }
        sb.Append("</tbody>\n</table>\n");

        // Failures by operation
        sb.Append("<h2>Failures by Operation</h2>\n");
        bool anyErr = false;
        sb.Append("<table>\n<thead><tr><th>Operation</th><th>Errors</th><th>Top Reasons</th></tr></thead>\n<tbody>\n");
        foreach (var op in Enum.GetValues<OperationType>())
        {
            var m = metrics.Ops[op];
            if (m.Errors == 0) continue;
            anyErr = true;
            string reasons = string.Join("<br/>", m.ErrorReasons
                .OrderByDescending(kv => kv.Value)
                .Take(5)
                .Select(kv => $"{Esc(kv.Key)} &times; {kv.Value:N0}"));
            sb.Append($"<tr><td>{op}</td><td class='err'>{m.Errors:N0}</td><td style='text-align:left'>{reasons}</td></tr>\n");
        }
        if (!anyErr)
            sb.Append("<tr><td colspan='3' style='text-align:left' class='ok'>No failures recorded.</td></tr>\n");
        sb.Append("</tbody>\n</table>\n");

        sb.Append($"<p class='muted'>Generated {DateTime.UtcNow:u} by CosmosLoadTest.</p>\n");
        sb.Append("</body>\n</html>\n");

        File.WriteAllText(path, sb.ToString());
    }

    private static string Card(string label, string value) =>
        $"<div class='card'><div class='v'>{value}</div><div class='l'>{Esc(label)}</div></div>\n";

    private static string Pct(long num, long den) =>
        den == 0 ? "-" : (100.0 * num / den).ToString("N1", CultureInfo.InvariantCulture) + "%";

    private static string Esc(string s) => string.IsNullOrEmpty(s) ? "" :
        s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");
}
