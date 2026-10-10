[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$BaselineDotNet,
    [Parameter(Mandatory)][string]$CandidateDotNet,
    [Parameter(Mandatory)][string]$FingerprintTool,
    [Parameter(Mandatory)][string]$Filtrace,
    [Parameter(Mandatory)][string]$ArtifactsPath,
    [string]$OrchardRoot = 'D:\repos\OrchardCore-profile',
    [ValidateRange(1, 256)][int]$MaxNodes = 8
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$ArtifactsPath = [IO.Path]::GetFullPath($ArtifactsPath)
if (Test-Path -LiteralPath $ArtifactsPath) {
    throw "ETW matrix destination already exists: $ArtifactsPath"
}
New-Item -ItemType Directory -Path $ArtifactsPath | Out-Null
[ordered]@{
    expectedCaptures = 4
    verbosity = 'normal'
    profile = 'cpu'
    sampleInterval = 'collector default 1 ms; verify trace provenance'
    baseline = $BaselineDotNet
    candidate = $CandidateDotNet
    binlog = @($false, $true)
    startedUtc = [DateTime]::UtcNow.ToString('O')
} | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $ArtifactsPath 'matrix.json') -Encoding utf8
foreach ($binlog in @($false, $true)) {
    $arms = if ($binlog) { @('candidate', 'baseline') } else { @('baseline', 'candidate') }
    $runPaths = @{}
    foreach ($arm in $arms) {
        $runId = "normal-binlog-$([int]$binlog)-$arm"
        $parameters = @{
            SubjectDotNet = if ($arm -eq 'baseline') { $BaselineDotNet } else { $CandidateDotNet }
            ControlDotNet = $BaselineDotNet
            FingerprintTool = $FingerprintTool
            Filtrace = $Filtrace
            ArtifactsPath = $ArtifactsPath
            RunId = $runId
            OrchardRoot = $OrchardRoot
            MaxNodes = $MaxNodes
            Verbosity = 'normal'
            BinaryLog = $binlog
            IndexedResources = $arm -eq 'candidate'
            CollectEtw = $true
        }
        & (Join-Path $PSScriptRoot 'Measure-ColdBuild.ps1') @parameters
        $runPaths[$arm] = Join-Path $ArtifactsPath $runId
    }
    $baseline = @(Get-Content -LiteralPath (Join-Path $runPaths.baseline 'content-outputs.json') -Raw | ConvertFrom-Json)
    $candidate = @(Get-Content -LiteralPath (Join-Path $runPaths.candidate 'content-outputs.json') -Raw | ConvertFrom-Json)
    if ($baseline.Count -eq 0 -or $baseline.Count -ne $candidate.Count) {
        throw "ETW binlog=$binlog has different or empty output populations."
    }
    $expected = @{}
    foreach ($output in $baseline) {
        $expected.Add($output.Path, $output.ContentHash)
    }
    foreach ($output in $candidate) {
        if (-not $expected.ContainsKey($output.Path) -or $expected[$output.Path] -cne $output.ContentHash) {
            throw "ETW binlog=$binlog output mismatch: $($output.Path)"
        }
    }
    [ordered]@{ binlog = $binlog; before = $runPaths.baseline; after = $runPaths.candidate; equivalentOutputs = $baseline.Count } |
        ConvertTo-Json -Compress | Add-Content -LiteralPath (Join-Path $ArtifactsPath 'pairs.jsonl') -Encoding utf8
}
Write-Host "Completed all four independently reset normal-verbosity ETW captures: $ArtifactsPath"
