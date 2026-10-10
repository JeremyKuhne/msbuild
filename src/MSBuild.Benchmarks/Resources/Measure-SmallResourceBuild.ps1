[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$BaselineDotNet,
    [Parameter(Mandatory)][string]$CandidateDotNet,
    [Parameter(Mandatory)][string]$ArtifactsPath,
    [string]$Project = 'D:\repos\OrchardCore-profile\src\OrchardCore\OrchardCore.Navigation.Core\OrchardCore.Navigation.Core.csproj',
    [ValidateRange(1, 30)][int]$Rounds = 10,
    [ValidateSet('Build', 'Evaluate')][string]$Phase = 'Build'
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$Project = (Resolve-Path -LiteralPath $Project).Path
$projectRoot = Split-Path -Parent $Project
$BaselineDotNet = (Resolve-Path -LiteralPath $BaselineDotNet).Path
$CandidateDotNet = (Resolve-Path -LiteralPath $CandidateDotNet).Path
$ArtifactsPath = [IO.Path]::GetFullPath($ArtifactsPath)
if (Test-Path -LiteralPath $ArtifactsPath) {
    throw "Refusing to overwrite measurements: $ArtifactsPath"
}
New-Item -ItemType Directory -Path $ArtifactsPath | Out-Null
Copy-Item -LiteralPath $PSCommandPath -Destination (Join-Path $ArtifactsPath 'harness.ps1')

$env:MSBUILDDISABLENODEREUSE = '1'
$env:MSBUILDTERMINALLOGGER = 'off'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = '1'
$env:MSBUILD_USE_INDEXED_STRING_RESOURCES = '0'
$properties = @('-p:Configuration=Release', '-p:TargetFramework=net10.0',
    '-p:BuildProjectReferences=false', '-p:UseSharedCompilation=false')
$probeText = & $BaselineDotNet msbuild $Project @properties `
    '-getProperty:BaseOutputPath,BaseIntermediateOutputPath,IntermediateOutputPath,TargetPath' '-nr:false' '-v:quiet'
if ($LASTEXITCODE -ne 0) {
    throw "Project inventory failed ($LASTEXITCODE)."
}
$probe = ($probeText -join "`n") | ConvertFrom-Json
if ([string]::IsNullOrWhiteSpace($probe.Properties.TargetPath)) {
    throw 'Project inventory returned an empty TargetPath.'
}
$bin = [IO.Path]::GetFullPath((Join-Path $projectRoot $probe.Properties.BaseOutputPath)).TrimEnd('\')
$intermediate = [IO.Path]::GetFullPath((Join-Path $projectRoot $probe.Properties.IntermediateOutputPath)).TrimEnd('\')
foreach ($path in @($bin, $intermediate)) {
    if (-not $path.StartsWith($projectRoot + '\', [StringComparison]::OrdinalIgnoreCase)) {
        throw "Reset directory is outside the selected project: $path"
    }
    $ancestor = $path
    while ($ancestor -ne $projectRoot) {
        if ((Test-Path -LiteralPath $ancestor) -and
            ((Get-Item -LiteralPath $ancestor -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)) {
            throw "Reset path contains a reparse point: $ancestor"
        }
        $ancestor = [IO.Path]::GetDirectoryName($ancestor)
    }
}

$assets = Join-Path $projectRoot 'obj\project.assets.json'
if (-not (Test-Path -LiteralPath $assets)) {
    & $BaselineDotNet restore $Project '-nr:false' '-v:quiet'
    if ($LASTEXITCODE -ne 0) {
        throw "Project restore failed ($LASTEXITCODE)."
    }
}
$arguments = @('msbuild', $Project) + $properties + @('-m:1', '-nr:false', '-tl:off', '-v:quiet')
$arguments += if ($Phase -eq 'Build') { '-t:Build' } else { '-getProperty:TargetPath' }
$arms = @(
    @{ name = 'baseline'; dotnet = $BaselineDotNet; indexed = '0' },
    @{ name = 'candidate-platform'; dotnet = $CandidateDotNet; indexed = '0' },
    @{ name = 'candidate-indexed'; dotnet = $CandidateDotNet; indexed = '1' }
)
$identities = @(
    foreach ($arm in $arms) {
        $sdk = (& $arm.dotnet --version)
        if ($LASTEXITCODE -ne 0) {
            throw "SDK identity failed for $($arm.name)."
        }
        $sdkRoot = Join-Path (Split-Path -Parent $arm.dotnet) "sdk\$sdk"
        [ordered]@{
            arm = $arm.name
            executable = $arm.dotnet
            indexed = $arm.indexed
            sdk = $sdk
            assemblies = @(
                foreach ($name in @('MSBuild.dll', 'Microsoft.Build.dll', 'Microsoft.Build.Framework.dll',
                    'Microsoft.Build.Tasks.Core.dll', 'Microsoft.Build.Utilities.Core.dll')) {
                    [ordered]@{ name = $name; hash = (Get-FileHash -LiteralPath (Join-Path $sdkRoot $name) -Algorithm SHA256).Hash }
                }
            )
        }
    }
)
if (@($identities.sdk | Sort-Object -Unique).Count -ne 1) {
    throw 'Small-build subjects must use the same SDK version.'
}
$identity = [ordered]@{
    project = $Project
    projectHash = (Get-FileHash -LiteralPath $Project -Algorithm SHA256).Hash
    sourceCount = @(Get-ChildItem -LiteralPath $projectRoot -File -Filter '*.cs' -Recurse |
        Where-Object FullName -NotMatch '\\(bin|obj)\\').Count
    dependencyPolicy = 'prebuilt project references; BuildProjectReferences=false'
    reset = @($bin, $intermediate)
    cachePolicy = 'fresh root/compiler processes and selected project compilation outputs; NuGet assets retained; OS cache not flushed'
    assetsHash = (Get-FileHash -LiteralPath $assets -Algorithm SHA256).Hash
    arguments = $arguments
    harnessHash = (Get-FileHash -LiteralPath $PSCommandPath -Algorithm SHA256).Hash
    phase = $Phase
    rounds = $Rounds
    binlog = $false
    verbosity = 'quiet'
    terminalLogger = 'off'
    consoleHandling = if ($Phase -eq 'Build') {
        'inherited streams; no redirection or text logger'
    } else {
        'property response captured in memory, validated and reprinted outside timing; stderr inherited; no text-file logger'
    }
    uiCulture = (Get-UICulture).Name
    culture = (Get-Culture).Name
    arms = $identities
    startedUtc = [DateTime]::UtcNow.ToString('O')
}
$identity | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $ArtifactsPath 'identity.json') -Encoding utf8
$results = [Collections.Generic.List[object]]::new()
$expectedHash = $null
for ($round = 0; $round -lt $Rounds; $round++) {
    $order = if ($round % 2 -eq 0) { @(0, 1, 2) } else { @(2, 1, 0) }
    foreach ($armIndex in $order) {
        $arm = $arms[$armIndex]
        foreach ($path in @($bin, $intermediate)) {
            if (Test-Path -LiteralPath $path) {
                Remove-Item -LiteralPath $path -Recurse -Force
            }
        }
        $env:MSBUILD_USE_INDEXED_STRING_RESOURCES = $arm.indexed
        $start = [Diagnostics.ProcessStartInfo]::new()
        $start.FileName = $arm.dotnet
        $start.WorkingDirectory = $projectRoot
        $start.UseShellExecute = $false
        $start.RedirectStandardOutput = $Phase -eq 'Evaluate'
        foreach ($argument in $arguments) {
            $start.ArgumentList.Add($argument)
        }
        $watch = [Diagnostics.Stopwatch]::StartNew()
        $process = [Diagnostics.Process]::Start($start)
        $propertyResponse = if ($Phase -eq 'Evaluate') { $process.StandardOutput.ReadToEndAsync() } else { $null }
        $process.WaitForExit()
        $watch.Stop()
        $exitCode = $process.ExitCode
        $result = [ordered]@{
            round = $round + 1
            arm = $arm.name
            indexed = $arm.indexed
            wallMilliseconds = $watch.Elapsed.TotalMilliseconds
            rootCpuMilliseconds = $process.TotalProcessorTime.TotalMilliseconds
            rootPeakWorkingSetBytes = $process.PeakWorkingSet64
            rootPid = $process.Id
            exitCode = $exitCode
            timeUtc = [DateTime]::UtcNow.ToString('O')
        }
        $process.Dispose()
        if ($exitCode -ne 0) {
            $result | ConvertTo-Json -Compress | Add-Content -LiteralPath (Join-Path $ArtifactsPath 'failed.jsonl') -Encoding utf8
            throw "Small build failed for $($arm.name), round $($round + 1), exit $exitCode."
        }
        if ($Phase -eq 'Build') {
            if ((Get-Item -LiteralPath $probe.Properties.TargetPath).Length -eq 0) {
                throw "Small build produced an empty assembly: $($arm.name), round $($round + 1)."
            }
            $hash = (Get-FileHash -LiteralPath $probe.Properties.TargetPath -Algorithm SHA256).Hash
            if ($expectedHash -and $hash -cne $expectedHash) {
                throw "Small build changed assembly output: $($arm.name), round $($round + 1)."
            }
            $expectedHash = $hash
            $result.outputHash = $hash
        }
        else {
            $targetPath = $propertyResponse.GetAwaiter().GetResult().Trim()
            if ($targetPath -cne $probe.Properties.TargetPath) {
                throw "Evaluation returned a different TargetPath: $($arm.name), round $($round + 1): $targetPath"
            }
            $result.targetPath = $targetPath
            Write-Host $targetPath
        }
        $results.Add($result)
        $result | ConvertTo-Json -Compress | Add-Content -LiteralPath (Join-Path $ArtifactsPath 'runs.jsonl') -Encoding utf8
        Write-Host ("Round {0} {1}: {2:N1} ms wall, {3:N1} ms root CPU" -f
            $result.round, $result.arm, $result.wallMilliseconds, $result.rootCpuMilliseconds)
    }
}
if ($results.Count -ne $Rounds * $arms.Count) {
    throw 'Small-build result population is incomplete.'
}
$results | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $ArtifactsPath 'results.json') -Encoding utf8
Write-Host "Completed $($results.Count) fresh-process measurements: $ArtifactsPath"
