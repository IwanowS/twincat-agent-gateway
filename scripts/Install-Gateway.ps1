<#
.SYNOPSIS
Installs TwinCAT Agent Gateway commands for the current Windows user.

.DESCRIPTION
Builds the repository in Release configuration unless -SkipBuild is specified,
replaces the desktop gateway and MCP adapter at one stable per-user application
path, and creates quiet command shims named twincat-gateway and
twincat-gateway-mcp.

By default, the script offers to add the command directory to the current
user's PATH. It does not require elevation and does not start the gateway,
TwinCAT XAE, or a TwinCAT runtime.

.PARAMETER InstallRoot
Destination root for application files and command shims. The default is
%LOCALAPPDATA%\TwinCatAgentGateway.

.PARAMETER NonInteractive
Suppresses the PATH confirmation prompt and accepts its default answer. Unless
-NoPathUpdate is also specified, the user PATH is updated when necessary.

.PARAMETER NoPathUpdate
Prevents any persistent user PATH change. The installed command shims remain
available directly from the InstallRoot\bin directory.

.PARAMETER SkipBuild
Skips the Release build. Use this only when the required Release artifacts
already exist in the repository output directories.

.PARAMETER Force
Replaces an existing installation without prompting. An existing installation
cannot be replaced non-interactively unless this switch is specified.

.PARAMETER Help
Displays the full help for this script and exits without building, copying
files, or changing PATH.

.EXAMPLE
.\scripts\Install-Gateway.ps1

Builds and installs the gateway interactively for the current user.

.EXAMPLE
.\scripts\Install-Gateway.ps1 -NonInteractive -Force -Verbose

Builds and replaces an existing installation, accepts the default PATH update,
and shows detailed installation progress.

.EXAMPLE
.\scripts\Install-Gateway.ps1 -InstallRoot C:\Tools\TwinCatGateway -NoPathUpdate

Installs into a custom directory without changing PATH.

.EXAMPLE
.\scripts\Install-Gateway.ps1 -Help

Displays full help and performs no installation actions.

.NOTES
Run this script from a normal, non-elevated PowerShell. It installs applications
only; Codex MCP registration and agent skills are separate explicit steps.
Project configurations and logs outside the app directory are preserved.
Running installed gateway or MCP processes must be closed before replacement.
-SkipBuild is intended for verified local artifacts and installer smoke tests.
#>
[CmdletBinding()]
param(
    [string]$InstallRoot,

    [switch]$NonInteractive,

    [switch]$NoPathUpdate,

    [switch]$SkipBuild,

    [switch]$Force,

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
$applicationRoot = Join-Path $resolvedInstallRoot 'app'
$legacyVersionsRoot = Join-Path $resolvedInstallRoot 'versions'
$installedDesktop = Join-Path $applicationRoot 'gateway'
$installedMcp = Join-Path $applicationRoot 'mcp'
$commandDirectory = Join-Path $resolvedInstallRoot 'bin'

function Get-InstalledGatewayProcesses {
    $installationRoots = @(
        $applicationRoot,
        $legacyVersionsRoot
    ) | ForEach-Object {
        [IO.Path]::GetFullPath($_).TrimEnd(
            [IO.Path]::DirectorySeparatorChar,
            [IO.Path]::AltDirectorySeparatorChar
        ) + [IO.Path]::DirectorySeparatorChar
    }

    foreach ($processName in @(
            'twincat-gateway',
            'twincat-gateway-mcp')) {
        foreach ($process in @(
                Get-Process `
                    -Name $processName `
                    -ErrorAction SilentlyContinue)) {
            try {
                $processPath = $process.MainModule.FileName
                if ($installationRoots.Where({
                            $processPath.StartsWith(
                                $_,
                                [StringComparison]::OrdinalIgnoreCase)
                        }).Count -gt 0) {
                    [PSCustomObject]@{
                        Id = $process.Id
                        Path = $processPath
                    }
                }
            }
            catch {
                Write-Verbose (
                    "Could not inspect process $($process.Id): " +
                    $_.Exception.Message)
            }
            finally {
                $process.Dispose()
            }
        }
    }
}

function Assert-NoInstalledGatewayProcesses {
    $runningProcesses = @(Get-InstalledGatewayProcesses)
    if ($runningProcesses.Count -eq 0) {
        return
    }

    $processDescription = $runningProcesses |
        ForEach-Object {
            "PID $($_.Id) '$($_.Path)'"
        }
    throw (
        "Close the installed TwinCAT Agent Gateway and MCP adapter " +
        "before replacement. Running: " +
        ($processDescription -join ', '))
}

$installationExists =
    (Test-Path -LiteralPath $applicationRoot) `
    -or (Test-Path -LiteralPath $legacyVersionsRoot)
if ($installationExists -and -not $Force) {
    if ($NonInteractive) {
        throw (
            "An existing TwinCAT Agent Gateway installation was found " +
            "at '$resolvedInstallRoot'. Rerun with -Force to replace it.")
    }

    Write-Verbose 'Requesting confirmation before replacing the installation.'
    $replaceAnswer = Read-Host (
        "Replace the existing TwinCAT Agent Gateway installation " +
        "at '$resolvedInstallRoot'? [y/N]")
    $replaceConfirmed =
        $replaceAnswer.Equals(
            'y',
            [StringComparison]::OrdinalIgnoreCase) `
        -or $replaceAnswer.Equals(
            'yes',
            [StringComparison]::OrdinalIgnoreCase)
    if (-not $replaceConfirmed) {
        Write-Output 'Installation cancelled. No files were changed.'
        return
    }
}
elseif ($installationExists) {
    Write-Verbose 'Replacing the existing installation by request.'
}

if ($installationExists) {
    Assert-NoInstalledGatewayProcesses
}

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
$stagingRoot = Join-Path `
    $resolvedInstallRoot `
    ('.install-staging-' + [Guid]::NewGuid().ToString('N'))
$stagedDesktop = Join-Path $stagingRoot 'gateway'
$stagedMcp = Join-Path $stagingRoot 'mcp'
$installRootPrefix =
    $resolvedInstallRoot.TrimEnd(
        [IO.Path]::DirectorySeparatorChar,
        [IO.Path]::AltDirectorySeparatorChar
    ) + [IO.Path]::DirectorySeparatorChar
Write-Verbose "Preparing application version '$assemblyVersion'."

try {
    foreach ($directory in @(
            $stagedDesktop,
            $stagedMcp)) {
        New-Item `
            -ItemType Directory `
            -Path $directory `
            -Force |
            Out-Null
    }

    Write-Verbose "Staging desktop gateway files in '$stagedDesktop'."
    Copy-Item `
        -Path (Join-Path $desktopOutput '*') `
        -Destination $stagedDesktop `
        -Recurse `
        -Force
    Write-Verbose "Staging MCP adapter files in '$stagedMcp'."
    Copy-Item `
        -Path (Join-Path $mcpOutput '*') `
        -Destination $stagedMcp `
        -Recurse `
        -Force

    if ($installationExists) {
        Assert-NoInstalledGatewayProcesses
    }

    foreach ($obsoleteDirectory in @(
            $applicationRoot,
            $legacyVersionsRoot)) {
        if (-not (Test-Path -LiteralPath $obsoleteDirectory)) {
            continue
        }

        $resolvedObsoleteDirectory =
            [IO.Path]::GetFullPath($obsoleteDirectory)
        if (-not $resolvedObsoleteDirectory.StartsWith(
                $installRootPrefix,
                [StringComparison]::OrdinalIgnoreCase)) {
            throw (
                "Refusing to remove install path " +
                "'$resolvedObsoleteDirectory'.")
        }

        Write-Verbose "Removing '$resolvedObsoleteDirectory'."
        Remove-Item `
            -LiteralPath $resolvedObsoleteDirectory `
            -Recurse `
            -Force
    }

    Write-Verbose "Installing application files to '$applicationRoot'."
    Move-Item `
        -LiteralPath $stagingRoot `
        -Destination $applicationRoot
}
finally {
    if (Test-Path -LiteralPath $stagingRoot) {
        $resolvedStagingRoot = [IO.Path]::GetFullPath($stagingRoot)
        if (-not $resolvedStagingRoot.StartsWith(
                $installRootPrefix,
                [StringComparison]::OrdinalIgnoreCase)) {
            throw "Refusing to remove staging path '$resolvedStagingRoot'."
        }

        Remove-Item `
            -LiteralPath $resolvedStagingRoot `
            -Recurse `
            -Force
    }
}

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

New-Item `
    -ItemType Directory `
    -Path $commandDirectory `
    -Force |
    Out-Null
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
Write-Output "Application directory: $applicationRoot"
Write-Output "Installed version: $assemblyVersion"
Write-Output "Command directory: $commandDirectory"
Write-Output "User PATH: $pathStatus"
Write-Output "A new PowerShell may be required to see PATH changes."
Write-Output ""
Write-Verbose "Printing canonical setup instructions from '$setupSource'."
Write-Output (Get-Content -LiteralPath $setupSource -Raw)
