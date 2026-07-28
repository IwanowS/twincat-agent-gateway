<#
.SYNOPSIS
Installs TwinCAT Agent Gateway commands for the current Windows user.

.DESCRIPTION
Builds the repository in Release configuration unless -SkipBuild is specified,
copies the desktop gateway and MCP adapter into a deterministic version
directory, and creates quiet command shims named twincat-gateway and
twincat-gateway-mcp.

By default, the script offers to add the command directory to the current
user's PATH. It does not require elevation and does not start the gateway,
TwinCAT XAE, or a TwinCAT runtime.

.PARAMETER InstallRoot
Destination root for versioned application files and command shims. The
default is %LOCALAPPDATA%\TwinCatAgentGateway.

.PARAMETER NonInteractive
Suppresses the PATH confirmation prompt and accepts its default answer. Unless
-NoPathUpdate is also specified, the user PATH is updated when necessary.

.PARAMETER NoPathUpdate
Prevents any persistent user PATH change. The installed command shims remain
available directly from the InstallRoot\bin directory.

.PARAMETER SkipBuild
Skips the Release build. Use this only when the required Release artifacts
already exist in the repository output directories.

.PARAMETER Help
Displays the full help for this script and exits without building, copying
files, or changing PATH.

.EXAMPLE
.\scripts\Install-Gateway.ps1

Builds and installs the gateway interactively for the current user.

.EXAMPLE
.\scripts\Install-Gateway.ps1 -NonInteractive -Verbose

Builds and installs the gateway, accepts the default PATH update, and shows
detailed installation progress.

.EXAMPLE
.\scripts\Install-Gateway.ps1 -InstallRoot C:\Tools\TwinCatGateway -NoPathUpdate

Installs into a custom directory without changing PATH.

.EXAMPLE
.\scripts\Install-Gateway.ps1 -Help

Displays full help and performs no installation actions.

.NOTES
Run this script from a normal, non-elevated PowerShell. It installs applications
only; Codex MCP registration and agent skills are separate explicit steps.
Reinstalling identical artifacts is idempotent. -SkipBuild is intended for
verified local artifacts and installer smoke tests.
#>
[CmdletBinding()]
param(
    [string]$InstallRoot,

    [switch]$NonInteractive,

    [switch]$NoPathUpdate,

    [switch]$SkipBuild,

    [switch]$Help
)

if ($Help) {
    Get-Help $PSCommandPath -Full
    return
}

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

. (Join-Path $PSScriptRoot 'Install-Gateway.Common.ps1')

$repositoryRoot = Split-Path -Parent $PSScriptRoot
Write-Verbose "Repository root: '$repositoryRoot'."
if ([string]::IsNullOrWhiteSpace($InstallRoot)) {
    $InstallRoot = Join-Path `
        $env:LOCALAPPDATA `
        'TwinCatAgentGateway'
}

$resolvedInstallRoot = [IO.Path]::GetFullPath($InstallRoot)
Write-Verbose "Install root: '$resolvedInstallRoot'."
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
    Write-Verbose "Building '$solutionPath' in Release configuration."
    & dotnet build `
        $solutionPath `
        '--configuration' 'Release'
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet build failed with exit code $LASTEXITCODE."
    }
}
else {
    Write-Verbose 'Skipping the Release build by request.'
}

foreach ($requiredPath in @(
        $desktopExecutable,
        $mcpExecutable,
        $setupSource)) {
    if (-not (Test-Path -LiteralPath $requiredPath)) {
        throw "Required install artifact was not found at '$requiredPath'."
    }
}
Write-Verbose 'Required Release artifacts are available.'

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
Write-Verbose "Installing deterministic version '$versionName'."

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

Write-Verbose "Copying desktop gateway files to '$installedDesktop'."
Copy-Item `
    -Path (Join-Path $desktopOutput '*') `
    -Destination $installedDesktop `
    -Recurse `
    -Force
Write-Verbose "Copying MCP adapter files to '$installedMcp'."
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

Write-Verbose "Updating command shims in '$commandDirectory'."
Write-CommandShim `
    -Path (Join-Path $commandDirectory 'twincat-gateway.cmd') `
    -Target (Join-Path $installedDesktop 'twincat-gateway.exe')
Write-CommandShim `
    -Path (Join-Path $commandDirectory 'twincat-gateway-mcp.cmd') `
    -Target (Join-Path $installedMcp 'twincat-gateway-mcp.exe')

$shouldUpdatePath = -not $NoPathUpdate
if ($shouldUpdatePath -and -not $NonInteractive) {
    Write-Verbose 'Requesting confirmation before changing the user PATH.'
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
    Write-Verbose "Checking the user PATH for '$commandDirectory'."
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
        Write-Verbose 'Updated the persistent user PATH.'
    }
    else {
        $pathStatus = 'already contained the command directory'
        Write-Verbose 'The persistent user PATH already contains the command directory.'
    }

    $processPath = Get-PathWithEntry `
        -CurrentPath $env:Path `
        -Entry $commandDirectory
    $env:Path = $processPath.Value
}
elseif ($NoPathUpdate) {
    $pathStatus = 'skipped by -NoPathUpdate'
    Write-Verbose 'Skipped the user PATH update by request.'
}
else {
    $pathStatus = 'declined'
    Write-Verbose 'The user declined the PATH update.'
}

Write-Output ""
Write-Output "TwinCAT Agent Gateway installed."
Write-Output "Install root: $resolvedInstallRoot"
Write-Output "Installed version: $versionName"
Write-Output "Command directory: $commandDirectory"
Write-Output "User PATH: $pathStatus"
Write-Output "A new PowerShell may be required to see PATH changes."
Write-Output ""
Write-Verbose "Printing canonical setup instructions from '$setupSource'."
Write-Output (Get-Content -LiteralPath $setupSource -Raw)
