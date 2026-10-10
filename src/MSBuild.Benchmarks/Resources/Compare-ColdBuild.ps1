[CmdletBinding()]
param(
    [Parameter(Mandatory)][string[]]$Before,
    [Parameter(Mandatory)][string[]]$After,
    [ValidateRange(1000, 100000)][int]$Resamples = 10000,
    [int]$Seed = 20261005,
    [switch]$PeContent
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
if ($Before.Count -eq 0 -or $Before.Count -ne $After.Count) {
    throw 'Before and After must contain the same nonzero number of paired run directories.'
}

function Read-Run([string]$Directory) {
    $identity = Get-Content -LiteralPath (Join-Path $Directory 'identity.json') -Raw | ConvertFrom-Json
    $timing = Get-Content -LiteralPath (Join-Path $Directory 'timing.json') -Raw | ConvertFrom-Json
    $outputs = @(Get-Content -LiteralPath (Join-Path $Directory 'outputs.json') -Raw | ConvertFrom-Json)
    if ($identity.etw -or -not $timing.authoritativeTiming -or
        $timing.exitCode -ne 0 -or $timing.elapsedMilliseconds -le 0 -or $outputs.Count -eq 0) {
        throw "Invalid uninstrumented product run: $Directory"
    }
    if ($PeContent) {
        $outputs = @(Get-Content -LiteralPath (Join-Path $Directory 'content-outputs.json') -Raw | ConvertFrom-Json)
        if ($outputs.Count -eq 0) {
            throw "The normalized PE-content oracle is empty: $Directory"
        }
    }
    return @{ Identity = $identity; Timing = $timing; Outputs = $outputs }
}

function Median([double[]]$Values) {
    $sorted = @($Values | Sort-Object)
    $middle = [int][Math]::Floor($sorted.Count / 2)
    if ($sorted.Count % 2 -eq 0) {
        return ($sorted[$middle - 1] + $sorted[$middle]) / 2
    }
    return $sorted[$middle]
}

$ratios = [Collections.Generic.List[double]]::new()
$pairs = @(
    for ($i = 0; $i -lt $Before.Count; $i++) {
        $baseline = Read-Run $Before[$i]
        $candidate = Read-Run $After[$i]
        foreach ($property in @('orchardRevision', 'orchardGeneratorHash', 'sdk', 'maxNodes', 'osFileCache', 'verbosity', 'binaryLog', 'terminalLogger', 'outputHandling', 'outputOracle', 'fingerprintHash')) {
            if ($baseline.Identity.$property -cne $candidate.Identity.$property) {
                throw "Pair $i measured different $property values."
            }
        }
        $baselineArguments = @($baseline.Identity.arguments | ForEach-Object { if ($_ -like '-bl:*') { '-bl:<run-owned-path>' } else { $_ } })
        $candidateArguments = @($candidate.Identity.arguments | ForEach-Object { if ($_ -like '-bl:*') { '-bl:<run-owned-path>' } else { $_ } })
        if (($baselineArguments -join "`n") -cne ($candidateArguments -join "`n")) {
            throw "Pair $i measured different build commands."
        }
        $baselineOutputs = @{}
        foreach ($output in $baseline.Outputs) {
            $baselineOutputs.Add($output.path, $(if ($PeContent) { $output.ContentHash } else { $output.hash }))
        }
        if ($baseline.Outputs.Count -ne $candidate.Outputs.Count) {
            throw "Pair $i produced different assembly populations."
        }
        foreach ($output in $candidate.Outputs) {
            if (-not $baselineOutputs.ContainsKey($output.path) -or
                $baselineOutputs[$output.path] -cne $(if ($PeContent) { $output.ContentHash } else { $output.hash })) {
                throw "Pair $i has an assembly output mismatch: $($output.path)"
            }
        }
        $ratio = $candidate.Timing.elapsedMilliseconds / $baseline.Timing.elapsedMilliseconds
        $ratios.Add($ratio)
        [ordered]@{
            before = $Before[$i]
            after = $After[$i]
            beforeMilliseconds = $baseline.Timing.elapsedMilliseconds
            afterMilliseconds = $candidate.Timing.elapsedMilliseconds
            ratio = $ratio
            equivalentAssemblyCount = $candidate.Outputs.Count
        }
    }
)
$random = [Random]::new($Seed)
$bootstrap = [double[]]::new($Resamples)
$sample = [double[]]::new($ratios.Count)
for ($resample = 0; $resample -lt $Resamples; $resample++) {
    for ($i = 0; $i -lt $sample.Length; $i++) {
        $sample[$i] = $ratios[$random.Next($ratios.Count)]
    }
    $bootstrap[$resample] = Median $sample
}
[Array]::Sort($bootstrap)
$lower = $bootstrap[[int][Math]::Floor($Resamples * 0.025)]
$upper = $bootstrap[[int][Math]::Floor($Resamples * 0.975)]
[ordered]@{
    pairCount = $ratios.Count
    outputOracle = if ($PeContent) { 'PE content excluding MVID/timestamp/debug/signature only' } else { 'exact bytes' }
    pairs = $pairs
    medianRatio = Median $ratios.ToArray()
    percentileBootstrap95 = @($lower, $upper)
    bootstrapSeed = $Seed
    resamples = $Resamples
    decision = if ($ratios.Count -lt 10) {
        'pilot only; insufficient independent pairs for confirmation'
    } elseif ($lower -gt 1) {
        'credible regression; reject'
    } elseif ($upper - $lower -gt 0.02) {
        'inconclusive; precision target not established'
    } else {
        'no credible regression at the predeclared precision; inspect mechanism and attribution gates separately'
    }
} | ConvertTo-Json -Depth 8
