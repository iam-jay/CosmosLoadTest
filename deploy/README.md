# Deploy CosmosLoadTest on an Azure Windows VM

This ARM template spins up a **Windows VM** that automatically downloads, builds,
and runs the CosmosLoadTest workload via a CustomScript extension. The HTML report
is left on the VM (RDP in to view it).

## What it creates
- NSG (RDP 3389 inbound — restrict with `allowRdpFromIp`)
- Public IP, VNet, Subnet, NIC
- Windows Server 2022 VM
- CustomScript extension that runs `scripts/run-loadtest.ps1`:
  installs .NET 8 SDK, downloads this repo (zip), builds, and runs the workload.

## Deploy
```powershell
az group create -n cosmosload-rg -l eastus

az deployment group create `
  -g cosmosload-rg `
  --template-file template.json `
  --parameters parameters.json `
  --parameters adminPassword='<VM_PASSWORD>' cosmosKey='<COSMOS_PRIMARY_KEY>' `
               cosmosURI='https://<acct>.documents.azure.com:443/'
```

Set the workload via parameters (see `parameters.json`):
`requestsPerSec`, `totalRequests`, `documentSizeBytes`, and the per-op percentages
`readPct`, `queryPct`, `createPct`, `readFeedPct`, `upsertPct`, `patchPct`,
`replacePct`, `deletePct`, `batchPct` (a `0` disables that op).

## After deployment
RDP into the VM (public IP, `adminUsername` / password) and inspect:
- `C:\CosmosLoadTest\report.html` — the HTML report
- `C:\CosmosLoadTest\run.log` — bootstrap log
- `C:\CosmosLoadTest\workload.log` — workload console output
- `C:\CosmosLoadTest\build.log` — build output

## Notes
- The Cosmos key and VM password are passed as **SecureString** and sent to the VM
  via the extension's `protectedSettings` (encrypted, not shown in deployment logs).
- `allowRdpFromIp` defaults to `*` (any). Set it to your IP/CIDR for safety.
- The template pulls code from `repoOwner/repoName@repoBranch`
  (default `iam-jay/CosmosLoadTest@main`). Point these at a fork/branch if needed.
- Pre-seeding (10k×1KB by default) happens only when `createPct` is `0`.
