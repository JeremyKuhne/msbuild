[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$Speedscope,
    [Parameter(Mandatory)][string]$TraceInfo
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$document = Get-Content -LiteralPath $Speedscope -Raw | ConvertFrom-Json
$info = Get-Content -LiteralPath $TraceInfo -Raw | ConvertFrom-Json
if (-not $info.result.cpuSampling.timeWeightsEstablished) {
    throw 'Trace-established CPU time units are required for this ledger; report raw counts separately.'
}
if ($document.profiles.Count -eq 0 -or $document.shared.frames.Count -eq 0) {
    throw 'The speedscope export contains no profiles or frames.'
}

$groups = [ordered]@{
    lookup = 'Microsoft\.Build\..*(AssemblyResources\.GetString|Resources\.SR\.|StringResourceManager\.|IndexedStringResourceTable\.)'
    resourceHelpers = 'Microsoft\.Build\..*ResourceUtilities\.'
    formatting = 'Microsoft\.Build\.Framework\.Utilities\.MessageFormatter\.'
    parsing = 'Microsoft\.Build\.Framework\.Utilities\.MessageParser\.'
    taskResources = 'Microsoft\.Build\..*TaskLoggingHelper(Extension)?\.(FormatResourceString|GetResourceMessage|Log(Error|Warning).*Resources)'
    deferredMessages = 'Microsoft\.Build\.Framework\.LazyFormattedBuildEventArgs\.(get_Message|FormatMessageWithoutMutation)'
}
$self = @{}
$inclusive = @{}
$coverage = @{}
foreach ($name in $groups.Keys) {
    $self[$name] = 0.0
    $inclusive[$name] = 0.0
    $coverage[$name] = 0
}
$union = 0.0
$total = 0.0
$records = 0
foreach ($profile in $document.profiles) {
    if ($profile.type -ne 'sampled' -or $profile.unit -ne 'milliseconds' -or
        $profile.samples.Count -ne $profile.weights.Count) {
        throw 'Expected interval-established sampled profiles in milliseconds with one weight per stack.'
    }
    for ($i = 0; $i -lt $profile.samples.Count; $i++) {
        $weight = [double]$profile.weights[$i]
        if ($weight -lt 0 -or [double]::IsNaN($weight) -or [double]::IsInfinity($weight)) {
            throw 'Invalid sample weight.'
        }
        $stack = $profile.samples[$i]
        $total += $weight
        $records++
        $matched = [Collections.Generic.HashSet[string]]::new()
        foreach ($frameIndex in $stack) {
            $frame = $document.shared.frames[$frameIndex].name
            foreach ($name in $groups.Keys) {
                if ($frame -match $groups[$name]) {
                    $null = $matched.Add($name)
                }
            }
        }
        if ($matched.Count -gt 0) {
            $union += $weight
            foreach ($name in $matched) {
                $inclusive[$name] += $weight
                $coverage[$name]++
            }
        }
        if ($stack.Count -gt 0) {
            $leaf = $document.shared.frames[$stack[-1]].name
            foreach ($name in $groups.Keys) {
                if ($leaf -match $groups[$name]) {
                    $self[$name] += $weight
                    break
                }
            }
        }
    }
}
if ($records -eq 0 -or $total -le 0) {
    throw 'The scoped export has no positive CPU population.'
}
[ordered]@{
    unit = 'sampled CPU milliseconds'
    totalCpu = $total
    sampleRecords = $records
    resourcePipelineUnionCpu = $union
    resourcePipelineUnionPercent = 100 * $union / $total
    groups = @(
        foreach ($name in $groups.Keys) {
            [ordered]@{
                name = $name
                selfCpu = $self[$name]
                inclusiveCpu = $inclusive[$name]
                contributingRecords = $coverage[$name]
                status = if ($coverage[$name] -eq 0) { 'not sampled or unresolved; not proof of zero cost' } else { 'sampled' }
            }
        }
    )
    interpretation = 'Inclusive groups overlap and must not be added. The union counts each stack once. CPU share is not wall-clock latency share; inlined/unresolved frames limit coverage.'
} | ConvertTo-Json -Depth 8
