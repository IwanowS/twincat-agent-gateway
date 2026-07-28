[CmdletBinding()]
param(
    [string]$InstallRoot,

    [switch]$NonInteractive,

    [switch]$NoPathUpdate,

    [switch]$SkipBuild
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

. (Join-Path $PSScriptRoot 'Install-Gateway.Common.ps1')

$repositoryRoot = Split-Path -Parent $PSScriptRoot
if ([string]::IsNullOrWhiteSpace($InstallRoot)) {
    $InstallRoot = Join-Path `
        $env:LOCALAPPDATA `
        'TwinCatAgentGateway'
}

$resolvedInstallRoot = [IO.Path]::GetFullPath($InstallRoot)
$solutionPath = Join-Path `
    $repositoryRoot `
    'TwinCatGateway.sln'
$desktopOutput = Join-Path `
    $repositoryRoot `
    'src\TwinCatGateway.Desktop\bin\Release\net48'
$mcpOutput = Join-Path `
    $repositoryRoot `
    'src\TwinCatGateway.Mcp\bin\Release\net8.0'
$desktopExecutable = Join-Path `
    $desktopOutput `
    'twincat-gateway.exe'
$mcpExecutable = Join-Path `
    $mcpOutput `
    'twincat-gateway-mcp.exe'
$setupSource = Join-Path `
    $repositoryRoot `
    'setup\SETUP_INSTRUCTIONS.txt'

if (-not $SkipBuild) {
    & dotnet build `
        $solutionPath `
        '--configuration' 'Release'
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet build failed with exit code $LASTEXITCODE."
    }
}

foreach ($requiredPath in @(
        $desktopExecutable,
        $mcpExecutable,
        $setupSource)) {
    if (-not (Test-Path -LiteralPath $requiredPath)) {
        throw "Required install artifact was not found at '$requiredPath'."
    }
}

$assemblyVersion = [Reflection.AssemblyName]::GetAssemblyName(
    $desktopExecutable).Version.ToString(3)
$desktopHash = (Get-FileHash `
    -LiteralPath $desktopExecutable `
    -Algorithm SHA256).Hash.Substring(0, 8).ToLowerInvariant()
$mcpHash = (Get-FileHash `
    -LiteralPath $mcpExecutable `
    -Algorithm SHA256).Hash.Substring(0, 8).ToLowerInvariant()
$versionName = "$assemblyVersion-$desktopHash-$mcpHash"
$versionRoot = Join-Path `
    (Join-Path $resolvedInstallRoot 'versions') `
    $versionName
$installedDesktop = Join-Path $versionRoot 'gateway'
$installedMcp = Join-Path $versionRoot 'mcp'
$commandDirectory = Join-Path $resolvedInstallRoot 'bin'

foreach ($directory in @(
        $installedDesktop,
        $installedMcp,
        $commandDirectory)) {
    New-Item `
        -ItemType Directory `
        -Path $directory `
        -Force |
        Out-Null
}

Copy-Item `
    -Path (Join-Path $desktopOutput '*') `
    -Destination $installedDesktop `
    -Recurse `
    -Force
Copy-Item `
    -Path (Join-Path $mcpOutput '*') `
    -Destination $installedMcp `
    -Recurse `
    -Force

function Write-CommandShim {
    param(
        [Parameter(Mandatory)]
        [string]$Path,

        [Parameter(Mandatory)]
        [string]$Target
    )

    $escapedTarget = $Target.Replace('%', '%%')
    $temporaryPath = "$Path.$PID.tmp"
    try {
        [IO.File]::WriteAllLines(
            $temporaryPath,
            @(
                '@echo off'
                "@`"$escapedTarget`" %*"
            ),
            [Text.Encoding]::ASCII)
        Move-Item `
            -LiteralPath $temporaryPath `
            -Destination $Path `
            -Force
    }
    finally {
        if (Test-Path -LiteralPath $temporaryPath) {
            Remove-Item -LiteralPath $temporaryPath -Force
        }
    }
}

Write-CommandShim `
    -Path (Join-Path $commandDirectory 'twincat-gateway.cmd') `
    -Target (Join-Path $installedDesktop 'twincat-gateway.exe')
Write-CommandShim `
    -Path (Join-Path $commandDirectory 'twincat-gateway-mcp.cmd') `
    -Target (Join-Path $installedMcp 'twincat-gateway-mcp.exe')

$shouldUpdatePath = -not $NoPathUpdate
if ($shouldUpdatePath -and -not $NonInteractive) {
    $answer = Read-Host `
        "Add '$commandDirectory' to the user PATH? [Y/n]"
    $shouldUpdatePath = [string]::IsNullOrWhiteSpace($answer) `
        -or $answer.Equals(
            'y',
            [StringComparison]::OrdinalIgnoreCase) `
        -or $answer.Equals(
            'yes',
            [StringComparison]::OrdinalIgnoreCase)
}

$pathStatus = 'not changed'
if ($shouldUpdatePath) {
    $userPath = [Environment]::GetEnvironmentVariable(
        'Path',
        [EnvironmentVariableTarget]::User)
    $updatedUserPath = Get-PathWithEntry `
        -CurrentPath $userPath `
        -Entry $commandDirectory
    if ($updatedUserPath.Changed) {
        [Environment]::SetEnvironmentVariable(
            'Path',
            $updatedUserPath.Value,
            [EnvironmentVariableTarget]::User)
        $pathStatus = 'updated'
    }
    else {
        $pathStatus = 'already contained the command directory'
    }

    $processPath = Get-PathWithEntry `
        -CurrentPath $env:Path `
        -Entry $commandDirectory
    $env:Path = $processPath.Value
}
elseif ($NoPathUpdate) {
    $pathStatus = 'skipped by -NoPathUpdate'
}
else {
    $pathStatus = 'declined'
}

Write-Output ""
Write-Output "TwinCAT Agent Gateway installed."
Write-Output "Install root: $resolvedInstallRoot"
Write-Output "Installed version: $versionName"
Write-Output "Command directory: $commandDirectory"
Write-Output "User PATH: $pathStatus"
Write-Output "A new PowerShell may be required to see PATH changes."
Write-Output ""
Write-Output (Get-Content -LiteralPath $setupSource -Raw)
