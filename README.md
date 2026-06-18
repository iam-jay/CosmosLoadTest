# CosmosLoadTest

A .NET 8 console tool that drives a configurable Azure Cosmos DB (NoSQL) workload
across all 9 document operations and produces an HTML report.

## Deploy on an Azure Windows VM (one click)

Fill the workload details in the Azure Portal — no file editing needed:

[![Deploy To Azure](https://aka.ms/deploytoazurebutton)](https://portal.azure.com/#blade/Microsoft_Azure_CreateUIDef/CustomDeploymentBlade/uri/https%3A%2F%2Fraw.githubusercontent.com%2Fiam-jay%2FCosmosLoadTest%2Fmain%2Fdeploy%2Ftemplate.json/createUIDefinitionUri/https%3A%2F%2Fraw.githubusercontent.com%2Fiam-jay%2FCosmosLoadTest%2Fmain%2Fdeploy%2FcreateUiDefinition.json)

See [`deploy/README.md`](deploy/README.md) for details (and a basic-form button).

## Operations supported
Read, Query, Create, ReadFeed (change feed), Upsert, Patch, Replace, Delete, Batch
(transactional batch). Any operation set to `0%` is skipped.

## How it works
- **Quota scheduler:** `target[op] = percentage × TotalRequests` (weights are
  normalized so targets sum exactly to `TotalRequests`). Dispatch is paced to the
  global `RequestsPerSec`. Each pick is weighted by *remaining* quota, so when an
  operation reaches its target it drops out and its share redistributes to the rest.
- **Document pool:** Read/Replace/Patch/Delete need existing documents.
  - If `Create% == 0`, the tool **pre-seeds 10,000 × 1KB docs** first.
  - If `Create% > 0`, created documents feed a shared pool during the run (no pre-seed).
- **Report:** per-operation totals, success, errors, success rate, ops/min,
  success/min, latency p50/p95/p99, average RU, and failures-by-operation.

## Build
```powershell
dotnet build -c Release
```

## Run
Provide credentials via config file, CLI args, or `COSMOS_ENDPOINT` / `COSMOS_KEY` env vars.

```powershell
# Using CLI args
dotnet run -c Release -- `
  --endpoint https://<acct>.documents.azure.com:443/ --key <KEY> `
  --database loadtest --container col `
  --rps 200 --total 20000 --docsize 1024 `
  --create 60 --read 20 --replace 10 --delete 10
```

Or copy `appsettings.sample.json` to `appsettings.json`, fill it in, and run:
```powershell
dotnet run -c Release
```

### Validate your mix without connecting
```powershell
dotnet run -c Release -- --total 20 --create 60 --read 20 --delete 10 --dryrun
```

## Key options
| Arg | Meaning | Default |
|-----|---------|---------|
| `--endpoint` / `--key` | Cosmos endpoint + primary key | (required) |
| `--database` / `--container` | target ids | loadtest / col |
| `--throughput` | RU/s if container must be created | 10000 |
| `--rps` | global requests/sec | 100 |
| `--total` | total operation count | 1000 |
| `--docsize` | document size (bytes) | 1024 |
| `--pkcount` | distinct partition-key values | 100 |
| `--concurrency` | max in-flight ops | 256 |
| `--preseed` | pre-seed count when Create%=0 | 10000 |
| `--report` | HTML report path | report.html |
| `--read … --batch` | per-op percentages (0 disables) | from config |

## Output
An HTML report (default `report.html`) plus a console summary.
