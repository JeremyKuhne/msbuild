[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$Filtrace,
    [Parameter(Mandatory)][string]$Trace,
    [Parameter(Mandatory)][string]$ArtifactsPath,
    [ValidateRange(1, 60)][int]$SampleSeconds = 10,
    [ValidateSet('cache', 'info', 'ledger')][string]$Operation = 'cache',
    [int]$ProcessId,
    [string]$Symbols,
    [string]$ResourceAnalyzer,
    [string]$AnalysisDotNet
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$Filtrace = (Resolve-Path -LiteralPath $Filtrace).Path
$Trace = (Resolve-Path -LiteralPath $Trace).Path
$ArtifactsPath = [IO.Path]::GetFullPath($ArtifactsPath)
if (Test-Path -LiteralPath $ArtifactsPath) {
    throw "Refusing to overwrite conversion evidence: $ArtifactsPath"
}
New-Item -ItemType Directory -Path $ArtifactsPath | Out-Null
$cachePath = [IO.Path]::ChangeExtension($Trace, '.etlx')
$arguments = @('cache', $Trace, '--action', 'convert')
$executable = $Filtrace
$filtraceCore = Join-Path (Split-Path -Parent $Filtrace) 'Filtrace.Core.dll'
if ($Operation -eq 'info') {
    if ($ProcessId -le 0) {
        throw 'Scoped trace info requires the exact captured root process ID.'
    }
    $arguments = @('info', $Trace, '--pid', "$ProcessId", '--children', 'include', '--format', 'json')
    if ($Symbols) {
        $arguments += @('--symbols', (Resolve-Path -LiteralPath $Symbols).Path)
    }
}
if ($Operation -eq 'ledger') {
    if ($ProcessId -le 0 -or -not $ResourceAnalyzer -or -not $AnalysisDotNet) {
        throw 'A resource ledger requires the exact root PID, pinned ResourceAnalyzer DLL and AnalysisDotNet executable.'
    }
    $ResourceAnalyzer = (Resolve-Path -LiteralPath $ResourceAnalyzer).Path
    $executable = (Resolve-Path -LiteralPath $AnalysisDotNet).Path
    $analysisCore = Join-Path (Split-Path -Parent $ResourceAnalyzer) 'Filtrace.Core.dll'
    if ((Get-FileHash -LiteralPath $analysisCore -Algorithm SHA256).Hash -cne
        (Get-FileHash -LiteralPath $filtraceCore -Algorithm SHA256).Hash) {
        throw 'Resource analyzer and pinned filtrace executable use different Filtrace.Core binaries.'
    }
    $arguments = @($ResourceAnalyzer, $Trace, "$ProcessId", (Join-Path $ArtifactsPath 'analysis'))
    if ($Symbols) {
        $arguments += (Resolve-Path -LiteralPath $Symbols).Path
    }
}
$identity = [ordered]@{
    trace = $Trace
    traceBytes = (Get-Item -LiteralPath $Trace).Length
    existingCache = Test-Path -LiteralPath $cachePath
    existingCacheBytes = if (Test-Path -LiteralPath $cachePath) { (Get-Item -LiteralPath $cachePath).Length } else { 0 }
    analyzer = $Filtrace
    analyzerHash = (Get-FileHash -LiteralPath $Filtrace -Algorithm SHA256).Hash
    filtraceCoreHash = (Get-FileHash -LiteralPath $filtraceCore -Algorithm SHA256).Hash
    executable = $executable
    analysisSdk = if ($Operation -eq 'ledger') { (& $executable --version) } else { $null }
    resourceAnalyzerHash = if ($ResourceAnalyzer) { (Get-FileHash -LiteralPath $ResourceAnalyzer -Algorithm SHA256).Hash } else { $null }
    operation = $Operation
    arguments = $arguments
    dedicatedMachine = $true
    memoryPressureStopPolicy = 'record pressure and continue; no working-set or available-RAM cutoff'
    sampleSeconds = $SampleSeconds
    startedUtc = [DateTime]::UtcNow.ToString('O')
    os = [Environment]::OSVersion.VersionString
}
$identity | ConvertTo-Json -Depth 5 |
    Set-Content -LiteralPath (Join-Path $ArtifactsPath 'identity.json') -Encoding utf8
$stdout = Join-Path $ArtifactsPath 'stdout.txt'
$stderr = Join-Path $ArtifactsPath 'stderr.txt'
$start = [Diagnostics.ProcessStartInfo]::new()
$start.FileName = $executable
$start.UseShellExecute = $false
$start.RedirectStandardOutput = $true
$start.RedirectStandardError = $true
foreach ($argument in $identity.arguments) {
    $start.ArgumentList.Add($argument)
}
$stopwatch = [Diagnostics.Stopwatch]::StartNew()
$process = [Diagnostics.Process]::Start($start)
$outputTask = $process.StandardOutput.ReadToEndAsync()
$errorTask = $process.StandardError.ReadToEndAsync()
$samples = [Collections.Generic.List[object]]::new()
while (-not $process.WaitForExit($SampleSeconds * 1000)) {
    $process.Refresh()
    $memory = Get-CimInstance Win32_PerfFormattedData_PerfOS_Memory
    $pagefiles = @(Get-CimInstance Win32_PageFileUsage)
    $sample = [ordered]@{
        elapsedSeconds = $stopwatch.Elapsed.TotalSeconds
        utc = [DateTime]::UtcNow.ToString('O')
        processId = $process.Id
        cpuSeconds = $process.TotalProcessorTime.TotalSeconds
        workingSetBytes = $process.WorkingSet64
        privateBytes = $process.PrivateMemorySize64
        peakWorkingSetBytes = $process.PeakWorkingSet64
        availableBytes = [long]$memory.AvailableBytes
        committedBytes = [long]$memory.CommittedBytes
        commitLimitBytes = [long]$memory.CommitLimit
        pageReadsPerSecond = [long]$memory.PageReadsPersec
        pagesInputPerSecond = [long]$memory.PagesInputPersec
        pagesOutputPerSecond = [long]$memory.PagesOutputPersec
        pagefileUsageMB = ($pagefiles | Measure-Object CurrentUsage -Sum).Sum
    }
    $samples.Add($sample)
    $sample | ConvertTo-Json -Compress |
        Add-Content -LiteralPath (Join-Path $ArtifactsPath 'samples.jsonl') -Encoding utf8
    Write-Host ("{0} PID {1}: {2:N0}s, working set {3:N2} GiB, private {4:N2} GiB, available {5:N2} GiB" -f
        $Operation, $process.Id, $sample.elapsedSeconds, ($sample.workingSetBytes / 1GB),
        ($sample.privateBytes / 1GB), ($sample.availableBytes / 1GB))
}
$stopwatch.Stop()
$outputTask.GetAwaiter().GetResult() | Set-Content -LiteralPath $stdout -Encoding utf8
$errorTask.GetAwaiter().GetResult() | Set-Content -LiteralPath $stderr -Encoding utf8
$exitCode = $process.ExitCode
$result = [ordered]@{
    processId = $process.Id
    exitCode = $exitCode
    elapsedSeconds = $stopwatch.Elapsed.TotalSeconds
    cpuSeconds = $process.TotalProcessorTime.TotalSeconds
    sampleCount = $samples.Count
    maxWorkingSetBytes = ($samples | ForEach-Object { $_.workingSetBytes } | Measure-Object -Maximum).Maximum
    maxPrivateBytes = ($samples | ForEach-Object { $_.privateBytes } | Measure-Object -Maximum).Maximum
    minimumAvailableBytes = ($samples | ForEach-Object { $_.availableBytes } | Measure-Object -Minimum).Minimum
    maxCommittedBytes = ($samples | ForEach-Object { $_.committedBytes } | Measure-Object -Maximum).Maximum
    maxPagefileUsageMB = ($samples | ForEach-Object { $_.pagefileUsageMB } | Measure-Object -Maximum).Maximum
    cacheExists = Test-Path -LiteralPath $cachePath
    cacheBytes = if (Test-Path -LiteralPath $cachePath) { (Get-Item -LiteralPath $cachePath).Length } else { 0 }
    completedUtc = [DateTime]::UtcNow.ToString('O')
}
$result | ConvertTo-Json -Depth 5 |
    Set-Content -LiteralPath (Join-Path $ArtifactsPath 'result.json') -Encoding utf8
$process.Dispose()
Get-Content -LiteralPath $stdout
Get-Content -LiteralPath $stderr
if ($exitCode -ne 0) {
    throw "filtrace $Operation failed with exit code $exitCode; evidence retained."
}
if (-not $result.cacheExists -or $result.cacheBytes -eq 0) {
    throw 'filtrace reported success without a populated ETLX cache.'
}
if ($Operation -eq 'ledger' -and -not (Test-Path -LiteralPath (Join-Path $ArtifactsPath 'analysis\resource-cpu.json'))) {
    throw 'Resource analysis reported success without a completed resource CPU ledger.'
}
Write-Host "$Operation completed; stress evidence: $ArtifactsPath"
