[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$SubjectDotNet,
    [Parameter(Mandatory)][string]$ControlDotNet,
    [Parameter(Mandatory)][string]$ArtifactsPath,
    [Parameter(Mandatory)][ValidatePattern('^[a-zA-Z0-9-]+$')][string]$RunId,
    [string]$OrchardRoot = 'D:\repos\OrchardCore-profile',
    [ValidateRange(1, 256)][int]$MaxNodes = 8,
    [string]$Filtrace,
    [string]$FingerprintTool,
    [ValidateSet('quiet', 'minimal', 'normal', 'detailed', 'diagnostic')][string]$Verbosity = 'minimal',
    [switch]$CollectEtw,
    [switch]$IndexedResources,
    [switch]$BinaryLog
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$OrchardRoot = (Resolve-Path -LiteralPath $OrchardRoot).Path.TrimEnd('\', '/')
$SubjectDotNet = (Resolve-Path -LiteralPath $SubjectDotNet).Path
$ControlDotNet = (Resolve-Path -LiteralPath $ControlDotNet).Path
if ($FingerprintTool) {
    $FingerprintTool = (Resolve-Path -LiteralPath $FingerprintTool).Path
}
$ArtifactsPath = [IO.Path]::GetFullPath($ArtifactsPath)
$runPath = Join-Path $ArtifactsPath $RunId
if (Test-Path -LiteralPath $runPath) {
    throw "Run destination already exists: $runPath"
}
if ($CollectEtw -and -not $Filtrace) {
    throw 'ETW collection requires an explicit pinned filtrace executable.'
}
if ($CollectEtw) {
    $Filtrace = (Resolve-Path -LiteralPath $Filtrace).Path
    if (-not [Security.Principal.WindowsPrincipal]::new(
        [Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole(
        [Security.Principal.WindowsBuiltInRole]::Administrator)) {
        throw 'ETW collection requires an elevated Windows shell.'
    }
}

New-Item -ItemType Directory -Path $runPath | Out-Null
$harnessHash = (Get-FileHash -LiteralPath $PSCommandPath -Algorithm SHA256).Hash
Copy-Item -LiteralPath $PSCommandPath -Destination (Join-Path $runPath 'harness.ps1')
$env:MSBUILDDISABLENODEREUSE = '1'
$env:MSBUILDTERMINALLOGGER = 'off'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = '1'
$env:MSBUILD_USE_INDEXED_STRING_RESOURCES = if ($IndexedResources) { '1' } else { '0' }

function Invoke-Control([string[]]$Arguments) {
    & $ControlDotNet @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "Control command failed ($LASTEXITCODE): $($Arguments -join ' ')"
    }
}

function Assert-OutputDirectory([string]$Path) {
    $fullPath = [IO.Path]::GetFullPath($Path).TrimEnd('\', '/')
    if (-not $fullPath.StartsWith($OrchardRoot + [IO.Path]::DirectorySeparatorChar,
        [StringComparison]::OrdinalIgnoreCase)) {
        throw "Output directory is outside the profiling checkout: $fullPath"
    }
    if ([IO.Path]::GetFileName($fullPath) -notin @('bin', 'obj')) {
        throw "Refusing to remove a non-bin/obj output directory: $fullPath"
    }
    $ancestor = $fullPath
    while ($ancestor -ne $OrchardRoot) {
        if (Test-Path -LiteralPath $ancestor) {
            $item = Get-Item -LiteralPath $ancestor -Force
            if ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) {
                throw "Output path contains a reparse point: $ancestor"
            }
        }
        $ancestor = [IO.Path]::GetDirectoryName($ancestor)
    }
    return $fullPath
}

Push-Location $OrchardRoot
try {
    $inventory = Join-Path $runPath 'output-directories.txt'
    Invoke-Control @('msbuild', (Join-Path $PSScriptRoot 'ProfileOutputs.proj'),
        '-t:Inventory', "-p:ProfileRoot=$OrchardRoot", "-p:ProfileInventory=$inventory",
        '-nr:false', '-v:quiet')
    $directories = @(Get-Content -LiteralPath $inventory |
        ForEach-Object { Assert-OutputDirectory $_ } | Sort-Object -Unique)
    if ($directories.Count -eq 0) {
        throw 'The output inventory is empty.'
    }
    foreach ($directory in $directories) {
        if (Test-Path -LiteralPath $directory) {
            # Only evaluated, checkout-contained bin/obj directories reach this deletion.
            Remove-Item -LiteralPath $directory -Recurse -Force
        }
    }
    Invoke-Control @('restore', 'OrchardCore.slnx', '-nr:false', "-m:$MaxNodes",
        '-p:UseSharedCompilation=false', '-v:quiet')

    $arguments = @('build', 'OrchardCore.slnx', '-c', 'Release', '--no-restore',
        '--disable-build-servers', "-m:$MaxNodes", '-nr:false',
        '-p:UseSharedCompilation=false', '-tl:off', "-v:$Verbosity")
    if ($BinaryLog) {
        $arguments += "-bl:$(Join-Path $runPath 'build.binlog')"
    }
    $identity = [ordered]@{
        runId = $RunId
        subject = $SubjectDotNet
        subjectHash = (Get-FileHash -LiteralPath $SubjectDotNet -Algorithm SHA256).Hash
        control = $ControlDotNet
        orchardRevision = (& git --no-pager rev-parse HEAD)
        orchardStatus = @(& git --no-pager status --porcelain)
        orchardGeneratorHash = (Get-FileHash -LiteralPath (Join-Path $OrchardRoot 'src\OrchardCore\OrchardCore.SourceGenerators\ArgumentsFromInterceptor.cs') -Algorithm SHA256).Hash
        sdk = (& $SubjectDotNet --version)
        maxNodes = $MaxNodes
        arguments = $arguments
        indexedResources = [bool]$IndexedResources
        etw = [bool]$CollectEtw
        binaryLog = [bool]$BinaryLog
        verbosity = $Verbosity
        terminalLogger = 'off'
        outputHandling = 'inherited console streams; no text-file logger or output redirection'
        osFileCache = 'not flushed'
        timeUtc = [DateTime]::UtcNow.ToString('O')
        harnessHash = $harnessHash
        outputOracle = if ($FingerprintTool) { 'pe-content-identity-debug-signature-only' } else { 'exact-bytes' }
        fingerprintHash = if ($FingerprintTool) { (Get-FileHash -LiteralPath $FingerprintTool -Algorithm SHA256).Hash } else { $null }
    }
    $sdkRoot = Join-Path (Split-Path -Parent $SubjectDotNet) "sdk\$($identity.sdk)"
    $identity.subjectAssemblies = @(
        foreach ($name in @('MSBuild.dll', 'Microsoft.Build.dll', 'Microsoft.Build.Framework.dll',
            'Microsoft.Build.Tasks.Core.dll', 'Microsoft.Build.Utilities.Core.dll')) {
            $assembly = Join-Path $sdkRoot $name
            [ordered]@{
                name = $name
                hash = (Get-FileHash -LiteralPath $assembly -Algorithm SHA256).Hash
                pdbHash = if (Test-Path -LiteralPath ([IO.Path]::ChangeExtension($assembly, '.pdb'))) {
                    (Get-FileHash -LiteralPath ([IO.Path]::ChangeExtension($assembly, '.pdb')) -Algorithm SHA256).Hash
                } else { $null }
            }
        }
    )
    $identity | ConvertTo-Json -Depth 8 |
        Set-Content -LiteralPath (Join-Path $runPath 'identity.json') -Encoding utf8

    if ($CollectEtw) {
        $trace = Join-Path $runPath 'build.etl'
        $captureText = & $Filtrace collect --launch $SubjectDotNet `
            --launch-args ($arguments -join ' ') --working-directory $OrchardRoot `
            --output $trace --profile cpu --format json
        $collectorExit = $LASTEXITCODE
        $captureText | Set-Content -LiteralPath (Join-Path $runPath 'capture.json') -Encoding utf8
        if ($collectorExit -ne 0) {
            throw "filtrace collection failed ($collectorExit)."
        }
        $captureEnvelope = ($captureText -join "`n") | ConvertFrom-Json
        $capture = $captureEnvelope.result
        if ($capture.processExitCode -ne 0) {
            throw "Traced build failed ($($capture.processExitCode))."
        }
    }
    else {
        $start = [Diagnostics.ProcessStartInfo]::new()
        $start.FileName = $SubjectDotNet
        $start.WorkingDirectory = $OrchardRoot
        $start.UseShellExecute = $false
        foreach ($argument in $arguments) {
            $start.ArgumentList.Add($argument)
        }
        $stopwatch = [Diagnostics.Stopwatch]::StartNew()
        $process = [Diagnostics.Process]::Start($start)
        $peakWorkingSet = 0L
        while (-not $process.WaitForExit(250)) {
            $peakWorkingSet = [Math]::Max($peakWorkingSet, $process.PeakWorkingSet64)
        }
        $exitCode = $process.ExitCode
        $stopwatch.Stop()
        [ordered]@{
            elapsedMilliseconds = $stopwatch.Elapsed.TotalMilliseconds
            exitCode = $exitCode
            authoritativeTiming = $true
            processId = $process.Id
            rootCpuMilliseconds = $process.TotalProcessorTime.TotalMilliseconds
            rootPeakWorkingSetBytes = $peakWorkingSet
            counterScope = 'root process only; use ETW for complete process-tree CPU attribution'
        } | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $runPath 'timing.json') -Encoding utf8
        $process.Dispose()
        if ($exitCode -ne 0) {
            throw "Measured build failed ($exitCode)."
        }
    }

    $outputs = @(
        foreach ($directory in $directories) {
            if ([IO.Path]::GetFileName($directory) -eq 'bin' -and
                (Test-Path -LiteralPath $directory)) {
                Get-ChildItem -LiteralPath $directory -Recurse -File -Filter '*.dll' |
                    ForEach-Object {
                        [ordered]@{
                            path = $_.FullName.Substring($OrchardRoot.Length + 1)
                            hash = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash
                        }
                    }
            }
        }
    )
    if ($outputs.Count -eq 0) {
        throw 'Build returned success but produced no assembly outputs.'
    }
    $outputs | ConvertTo-Json -Depth 4 |
        Set-Content -LiteralPath (Join-Path $runPath 'outputs.json') -Encoding utf8
    if ($FingerprintTool) {
        & $ControlDotNet $FingerprintTool $OrchardRoot $inventory (Join-Path $runPath 'content-outputs.json')
        if ($LASTEXITCODE -ne 0) {
            throw "Independent output fingerprint validation failed ($LASTEXITCODE)."
        }
    }
    if ($BinaryLog) {
        $binlog = Get-Item -LiteralPath (Join-Path $runPath 'build.binlog')
        if ($binlog.Length -eq 0) {
            throw 'Binary logging was requested but produced an empty log.'
        }
        [ordered]@{ bytes = $binlog.Length; path = $binlog.FullName } |
            ConvertTo-Json | Set-Content -LiteralPath (Join-Path $runPath 'binlog.json') -Encoding utf8
    }
    Write-Host "Validated $($outputs.Count) assembly outputs: $runPath"
}
finally {
    Pop-Location
}
