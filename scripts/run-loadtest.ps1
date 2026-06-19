<#
.SYNOPSIS
  Bootstrap script run inside the Windows VM by the ARM CustomScriptExtension.
  Installs the .NET 8 SDK, downloads the CosmosLoadTest repo, builds it, and runs
  the workload with the provided parameters. The HTML report is left on the VM.

.NOTES
  All output is logged to C:\CosmosLoadTest\run.log. The report is written to
  C:\CosmosLoadTest\repo\<RepoName>-<Branch>\report.html and copied to
  C:\CosmosLoadTest\report.html (RDP into the VM to view it).
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)] [string]$Endpoint,
    [Parameter(Mandatory = $true)] [string]$Key,
    [string]$Database = "loadtest",
    [string]$Container = "col",
    [int]$Throughput = 10000,

    [int]$Rps = 100,
    [long]$Total = 1000,
    [int]$DocSize = 1024,
    [int]$PkCount = 100,
    [int]$Concurrency = 256,
    [int]$PreSeed = 10000,

    # Per-operation percentages (relative weights; 0 disables an op).
    [double]$Read = 0,
    [double]$Query = 0,
    [double]$Create = 0,
    [double]$ReadFeed = 0,
    [double]$Upsert = 0,
    [double]$Patch = 0,
    [double]$Replace = 0,
    [double]$Delete = 0,
    [double]$Batch = 0,

    [string]$RepoOwner = "iam-jay",
    [string]$RepoName = "CosmosLoadTest",
    [string]$Branch = "main"
)

$ErrorActionPreference = "Stop"
$root = "C:\CosmosLoadTest"
New-Item -ItemType Directory -Path $root -Force | Out-Null
$log = Join-Path $root "run.log"

function Log($msg) {
    $line = "[{0:u}] {1}" -f (Get-Date), $msg
    Write-Host $line
    Add-Content -Path $log -Value $line
}

try {
    [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
    Log "=== CosmosLoadTest VM bootstrap starting ==="
    Log "Repo: $RepoOwner/$RepoName@$Branch  DB: $Database/$Container"

    # 0. Pre-flight: refuse to run with an all-zero operation mix (would otherwise
    #    fail deep in the tool). Surfaces a clear message in the extension status.
    $pctSum = $Read + $Query + $Create + $ReadFeed + $Upsert + $Patch + $Replace + $Delete + $Batch
    if ($pctSum -le 0) {
        throw "All operation percentages are 0. Set at least one of -Read/-Query/-Create/-ReadFeed/-Upsert/-Patch/-Replace/-Delete/-Batch > 0."
    }
    Log "Operation mix sum = $pctSum (Read=$Read Query=$Query Create=$Create ReadFeed=$ReadFeed Upsert=$Upsert Patch=$Patch Replace=$Replace Delete=$Delete Batch=$Batch)"

    # 1. Install .NET 8 SDK (official dotnet-install script).
    Log "Installing .NET 8 SDK..."
    $dotnetDir = "C:\dotnet"
    $installScript = Join-Path $env:TEMP "dotnet-install.ps1"
    Invoke-WebRequest -Uri "https://dot.net/v1/dotnet-install.ps1" -OutFile $installScript
    & $installScript -Channel 8.0 -InstallDir $dotnetDir -NoPath
    $dotnet = Join-Path $dotnetDir "dotnet.exe"
    if (-not (Test-Path $dotnet)) { throw ".NET install failed: $dotnet not found." }
    Log "dotnet: $(& $dotnet --version)"

    # 2. Download the repo as a zip (no git dependency) and extract.
    Log "Downloading repo zip..."
    $zip = Join-Path $env:TEMP "repo.zip"
    $zipUrl = "https://github.com/$RepoOwner/$RepoName/archive/refs/heads/$Branch.zip"
    Invoke-WebRequest -Uri $zipUrl -OutFile $zip
    $extract = Join-Path $root "repo"
    if (Test-Path $extract) { Remove-Item $extract -Recurse -Force }
    Expand-Archive -Path $zip -DestinationPath $extract -Force
    # Zip extracts to <RepoName>-<Branch>\
    $srcDir = Get-ChildItem -Path $extract -Directory | Select-Object -First 1 | ForEach-Object FullName
    Log "Source: $srcDir"

    # 3. Build (Release).
    Log "Building (Release)..."
    Push-Location $srcDir
    & $dotnet build -c Release | Tee-Object -FilePath (Join-Path $root "build.log")
    if ($LASTEXITCODE -ne 0) { throw "Build failed (exit $LASTEXITCODE)." }

    # 4. Run the workload. Report is written next to the project.
    $reportPath = Join-Path $srcDir "report.html"
    $runArgs = @(
        "run", "-c", "Release", "--no-build", "--",
        "--endpoint", $Endpoint,
        "--key", $Key,
        "--database", $Database,
        "--container", $Container,
        "--throughput", $Throughput,
        "--rps", $Rps,
        "--total", $Total,
        "--docsize", $DocSize,
        "--pkcount", $PkCount,
        "--concurrency", $Concurrency,
        "--preseed", $PreSeed,
        "--report", $reportPath,
        "--read", $Read, "--query", $Query, "--create", $Create,
        "--readfeed", $ReadFeed, "--upsert", $Upsert, "--patch", $Patch,
        "--replace", $Replace, "--delete", $Delete, "--batch", $Batch
    )
    Log "Starting workload: total=$Total rps=$Rps docsize=$DocSize"
    & $dotnet @runArgs 2>&1 | Tee-Object -FilePath (Join-Path $root "workload.log")
    $exit = $LASTEXITCODE
    Pop-Location

    if (Test-Path $reportPath) {
        Copy-Item $reportPath (Join-Path $root "report.html") -Force
        Log "Report available at: $reportPath  (and $root\report.html)"
    }
    if ($exit -ne 0) {
        Log "WORKLOAD FAILED (exit $exit). See $root\workload.log for the error."
        throw "Workload exited with code $exit. Check C:\CosmosLoadTest\workload.log."
    }
    Log "=== Workload finished successfully (exit $exit) ==="
    exit 0
}
catch {
    Log "BOOTSTRAP ERROR: $($_.Exception.Message)"
    Log $_.ScriptStackTrace
    # Non-zero exit makes the CustomScript extension report Failed and shows this
    # message in the VM instance view / deployment status.
    Write-Error $_.Exception.Message
    exit 1
}
