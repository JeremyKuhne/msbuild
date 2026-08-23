[CmdletBinding(PositionalBinding = $false)]
param(
    [Parameter(Mandatory = $true)]
    [string] $Destination,

    [switch] $SkipRestore
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$orchardCoreRepository = "https://github.com/OrchardCMS/OrchardCore.git"
$orchardCoreCommit = "6a28ae14c64aedcf5c9c748d602fed8696f1e153"
$repositoryRoot = Split-Path -Parent $PSScriptRoot
$destinationPath = [IO.Path]::GetFullPath($Destination)
$git = (Get-Command git -ErrorAction Stop).Source

function Invoke-Git([string[]] $Arguments)
{
    & $git @Arguments
    if ($LASTEXITCODE -ne 0)
    {
        throw "git $($Arguments -join ' ') failed with exit code $LASTEXITCODE."
    }
}

function Get-GitOutput([string[]] $Arguments)
{
    $output = & $git @Arguments
    if ($LASTEXITCODE -ne 0)
    {
        throw "git $($Arguments -join ' ') failed with exit code $LASTEXITCODE."
    }

    return $output
}

function Assert-CleanCheckout
{
    $status = @(Get-GitOutput @("-C", $destinationPath, "status", "--porcelain"))
    if ($status.Count -ne 0)
    {
        throw "The Orchard Core checkout at '$destinationPath' has local changes."
    }
}

$gitDirectory = Join-Path $destinationPath ".git"
if (!(Test-Path $gitDirectory -PathType Container))
{
    if (Test-Path $destinationPath)
    {
        $existingEntries = @(Get-ChildItem $destinationPath -Force)
        if ($existingEntries.Count -ne 0)
        {
            throw "The destination '$destinationPath' exists and is not an empty directory."
        }
    }
    else
    {
        $parent = Split-Path -Parent $destinationPath
        [IO.Directory]::CreateDirectory($parent) | Out-Null
    }

    Invoke-Git @("clone", "--filter=blob:none", "--no-checkout", $orchardCoreRepository, $destinationPath)
}
else
{
    Assert-CleanCheckout

    $origin = (Get-GitOutput @("-C", $destinationPath, "remote", "get-url", "origin")).Trim()
    if (![string]::Equals($origin, $orchardCoreRepository, [StringComparison]::OrdinalIgnoreCase))
    {
        throw "The checkout origin is '$origin'; expected '$orchardCoreRepository'."
    }
}

& $git -C $destinationPath cat-file -e "$orchardCoreCommit^{commit}" 2>$null
if ($LASTEXITCODE -ne 0)
{
    Invoke-Git @("-C", $destinationPath, "fetch", "--depth=1", "origin", $orchardCoreCommit)
}

Invoke-Git @("-C", $destinationPath, "checkout", "--detach", $orchardCoreCommit)
Assert-CleanCheckout

$head = (Get-GitOutput @("-C", $destinationPath, "rev-parse", "HEAD")).Trim()
if ($head -cne $orchardCoreCommit)
{
    throw "The Orchard Core checkout is at '$head'; expected '$orchardCoreCommit'."
}

$solutionPath = Join-Path $destinationPath "OrchardCore.slnx"
if (!(Test-Path $solutionPath -PathType Leaf))
{
    throw "The pinned Orchard Core checkout does not contain '$solutionPath'."
}

if (!$SkipRestore)
{
    $dotnet = Join-Path $repositoryRoot ".dotnet\dotnet.exe"
    if (!(Test-Path $dotnet -PathType Leaf))
    {
        throw "The repository SDK is missing. Run '.\build.cmd -v quiet' before restoring Orchard Core."
    }

    & $dotnet restore $solutionPath --nologo
    if ($LASTEXITCODE -ne 0)
    {
        throw "Restoring '$solutionPath' failed with exit code $LASTEXITCODE."
    }

    Assert-CleanCheckout
}

Write-Host "Orchard Core is ready at $solutionPath"