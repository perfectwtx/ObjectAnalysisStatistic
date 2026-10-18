#requires -Version 7.0
param(
    [ValidateSet('Short', 'Default', 'Dry')][string]$Job = 'Short',
    [string]$Filter = '*',
    [string]$Output = 'artifacts/benchmarks/current',
    [switch]$SkipResourceProbes
)
$ErrorActionPreference = 'Stop'
$repositoryRoot = Split-Path $PSScriptRoot -Parent
Push-Location $repositoryRoot
try {
    $project = 'benchmarks/ObjectAnalyzer.Benchmarks/ObjectAnalyzer.Benchmarks.csproj'
    dotnet build $project -c Release -v quiet
    if ($LASTEXITCODE -ne 0) { throw 'Benchmark build failed.' }
    if ((Test-Path -LiteralPath $Output) -and @(Get-ChildItem -LiteralPath $Output -Force).Count -gt 0) { throw 'Choose a fresh output directory to avoid mixing runs.' }
    New-Item -ItemType Directory -Path $Output -Force | Out-Null
    # A finite measured baseline; use Default for longer investigations.
    $benchmarkArguments = @('--filter', $Filter, '--job', $Job, '--exporters', 'json', '--artifacts', $Output)
    if ($Job -eq 'Short') { $benchmarkArguments += @('--warmupCount', '5', '--iterationCount', '10') }
    $sources = @('testsupport/DeterministicRecords.cs', $project) + @(Get-ChildItem 'benchmarks/ObjectAnalyzer.Benchmarks' -Filter '*.cs' | Sort-Object Name | ForEach-Object FullName)
    $hashInput = ($sources | ForEach-Object { (Get-FileHash -LiteralPath $_ -Algorithm SHA256).Hash }) -join ':'
    $algorithm = [Security.Cryptography.SHA256]::Create()
    try { $harnessHash = [Convert]::ToHexString($algorithm.ComputeHash([Text.Encoding]::UTF8.GetBytes($hashInput))) } finally { $algorithm.Dispose() }
    $sourceDirty = @(git status --porcelain --untracked-files=all -- src testsupport benchmarks/ObjectAnalyzer.Benchmarks).Count -gt 0
    @{
        Schema = 'performance-run/v1'; Revision = (git rev-parse HEAD).Trim(); SourceDirty = $sourceDirty
        StartedAtUtc = [DateTime]::UtcNow.ToString('O'); Workload = 'mixed-records/v1'
        WorkloadSha256 = (Get-FileHash testsupport/DeterministicRecords.cs -Algorithm SHA256).Hash
        HarnessSha256 = $harnessHash; Arguments = $benchmarkArguments
    } | ConvertTo-Json -Depth 4 | Set-Content (Join-Path $Output 'run-metadata.json') -Encoding utf8
    dotnet run --project $project -c Release --no-build --no-restore -- @benchmarkArguments
    if ($LASTEXITCODE -ne 0) { throw 'Benchmark run failed.' }
    $reports = @(Get-ChildItem (Join-Path $Output 'results') -Filter '*-full-compressed.json')
    if ($reports.Count -eq 0) { throw 'No benchmark reports were produced.' }
    foreach ($report in $reports) {
        $data = Get-Content -Raw $report.FullName | ConvertFrom-Json
        foreach ($benchmark in $data.Benchmarks) {
            if ($null -eq $benchmark.Statistics -or $benchmark.Statistics.N -lt 1) { throw "No measurements: $($benchmark.FullName)" }
        }
    }
    if (!$SkipResourceProbes) {
        foreach ($cardinality in @(32, 4096)) {
            foreach ($parallelism in @(1, 4)) {
                dotnet run --project $project -c Release --no-build --no-restore -- --probe $cardinality $parallelism (Join-Path $Output "probe-$cardinality-$parallelism.json")
                if ($LASTEXITCODE -ne 0) { throw 'Resource probe failed.' }
            }
        }
    }
} finally { Pop-Location }
