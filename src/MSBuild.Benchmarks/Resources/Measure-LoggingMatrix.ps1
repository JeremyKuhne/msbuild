[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$BaselineDotNet,
    [Parameter(Mandatory)][string]$CandidateDotNet,
    [Parameter(Mandatory)][string]$FingerprintTool,
    [Parameter(Mandatory)][string]$ArtifactsPath,
    [string]$OrchardRoot = 'D:\repos\OrchardCore-profile',
    [ValidateRange(1, 256)][int]$MaxNodes = 8
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$ArtifactsPath = [IO.Path]::GetFullPath($ArtifactsPath)
if (Test-Path -LiteralPath $ArtifactsPath) {
    throw "Matrix destination already exists: $ArtifactsPath"
}
New-Item -ItemType Directory -Path $ArtifactsPath | Out-Null
$configurations = @(
    foreach ($verbosity in @('quiet', 'normal', 'diagnostic')) {
        foreach ($binlog in @($false, $true)) {
            [ordered]@{ name = "$verbosity-binlog-$([int]$binlog)"; verbosity = $verbosity; binlog = $binlog }
        }
    }
)
[ordered]@{
    dedicatedMachine = $true
    repetitions = 3
    expectedBuildCount = 36
    configurations = $configurations
    baseline = $BaselineDotNet
    candidate = $CandidateDotNet
    maxNodes = $MaxNodes
    terminalLogger = 'off'
    cachePolicy = 'fresh processes and compilation outputs; restore outside timing; OS cache not flushed'
    order = 'rotate configuration order by round; alternate paired arms'
    normalization = 'compare full PE bytes excluding MVID, COFF timestamp, debug data and strong-name signature; preserve all IL, remaining metadata, resources and native sections'
    startedUtc = [DateTime]::UtcNow.ToString('O')
} | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $ArtifactsPath 'matrix.json') -Encoding utf8
$pairs = [Collections.Generic.List[object]]::new()
for ($round = 0; $round -lt 3; $round++) {
    for ($position = 0; $position -lt $configurations.Count; $position++) {
        $configuration = $configurations[($position + $round * 2) % $configurations.Count]
        $arms = if (($round + $position) % 2 -eq 0) { @('baseline', 'candidate') } else { @('candidate', 'baseline') }
        $runPaths = @{}
        foreach ($arm in $arms) {
            $runId = "$($configuration.name)-pair-$($round + 1)-$arm"
            $parameters = @{
                SubjectDotNet = if ($arm -eq 'baseline') { $BaselineDotNet } else { $CandidateDotNet }
                ControlDotNet = $BaselineDotNet
                FingerprintTool = $FingerprintTool
                ArtifactsPath = $ArtifactsPath
                RunId = $runId
                OrchardRoot = $OrchardRoot
                MaxNodes = $MaxNodes
                Verbosity = $configuration.verbosity
                BinaryLog = $configuration.binlog
                IndexedResources = $arm -eq 'candidate'
            }
            & (Join-Path $PSScriptRoot 'Measure-ColdBuild.ps1') @parameters
            $runPaths[$arm] = Join-Path $ArtifactsPath $runId
        }
        $pair = [ordered]@{
            configuration = $configuration.name
            pair = $round + 1
            order = $arms
            before = $runPaths.baseline
            after = $runPaths.candidate
        }
        $pairs.Add($pair)
        $pair | ConvertTo-Json -Compress | Add-Content -LiteralPath (Join-Path $ArtifactsPath 'pairs.jsonl') -Encoding utf8
        & (Join-Path $PSScriptRoot 'Compare-ColdBuild.ps1') `
            -Before $pair.before -After $pair.after -PeContent |
            Set-Content -LiteralPath (Join-Path $ArtifactsPath "$($configuration.name)-pair-$($round + 1)-comparison.json") -Encoding utf8
    }
}
if ($pairs.Count -ne 18) {
    throw "Matrix is incomplete: $($pairs.Count) of 18 pairs."
}
foreach ($configuration in $configurations) {
    $matching = @($pairs | Where-Object { $_.configuration -eq $configuration.name } | Sort-Object pair)
    if ($matching.Count -ne 3) {
        throw "Configuration $($configuration.name) has $($matching.Count) pairs instead of 3."
    }
    & (Join-Path $PSScriptRoot 'Compare-ColdBuild.ps1') `
        -Before @($matching | ForEach-Object { $_.before }) `
        -After @($matching | ForEach-Object { $_.after }) -PeContent |
        Set-Content -LiteralPath (Join-Path $ArtifactsPath "$($configuration.name)-comparison.json") -Encoding utf8
}
Write-Host "Completed all 36 clean-build measurements: $ArtifactsPath"
