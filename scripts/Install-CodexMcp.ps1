<#
.SYNOPSIS
Registers the installed TwinCAT Gateway MCP adapter with Codex.

.DESCRIPTION
Registers a global stdio MCP server through the Codex CLI. The script compares
an existing registration with the requested command, keeps an exact match, or
replaces an outdated registration with the same name.

The script does not edit Codex configuration files directly and does not
install or start TwinCAT Agent Gateway.

.PARAMETER ServerName
Global Codex MCP registration name. The default is twincat-gateway-mcp.

.PARAMETER McpCommand
Application command Codex will launch for the stdio MCP server. The command
must already be discoverable through PATH.

.PARAMETER CodexCommand
Codex CLI application command used to inspect and update MCP registrations.
The default is codex.

.PARAMETER Help
Displays the full help for this script and exits without reading or changing
Codex MCP registrations.

.EXAMPLE
.\scripts\Install-CodexMcp.ps1

Registers the installed twincat-gateway-mcp command under its default name.

.EXAMPLE
.\scripts\Install-CodexMcp.ps1 -ServerName tc-gateway -McpCommand C:\Tools\twincat-gateway-mcp.cmd

Registers a custom command path under a custom Codex MCP server name.

.EXAMPLE
.\scripts\Install-CodexMcp.ps1 -Verbose

Registers the adapter and shows command discovery and registration details.

.EXAMPLE
.\scripts\Install-CodexMcp.ps1 -Help

Displays full help and performs no Codex configuration changes.

.NOTES
Install TwinCAT Agent Gateway first and open a new PowerShell if the installer
updated PATH. This script changes the current user's global Codex MCP
configuration. Do not enable global and project-local registrations of the same
server simultaneously.
#>
[CmdletBinding()]
param(
    [string]$ServerName = 'twincat-gateway-mcp',

    [string]$McpCommand = 'twincat-gateway-mcp',

    [string]$CodexCommand = 'codex',

    [switch]$Help
)

if ($Help) {
    Get-Help $PSCommandPath -Full
    return
}

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

Write-Verbose "Resolving Codex CLI command '$CodexCommand'."
if (-not (Get-Command `
        -Name $CodexCommand `
        -CommandType Application `
        -ErrorAction SilentlyContinue)) {
    throw "Codex CLI command '$CodexCommand' is not available."
}

Write-Verbose "Resolving MCP application command '$McpCommand'."
if (-not (Get-Command `
        -Name $McpCommand `
        -CommandType Application `
        -ErrorAction SilentlyContinue)) {
    throw @"
MCP command '$McpCommand' is not available.
Install the gateway first and open a new PowerShell if PATH was updated.
"@
}

function Get-OptionalProperty {
    param(
        [AllowNull()]
        [object]$InputObject,

        [Parameter(Mandatory)]
        [string]$Name
    )

    if ($null -eq $InputObject) {
        return $null
    }

    $property = $InputObject.PSObject.Properties[$Name]
    if ($null -eq $property) {
        return $null
    }

    return $property.Value
}

Write-Verbose 'Reading global Codex MCP registrations.'
$listJson = & $CodexCommand mcp list --json
if ($LASTEXITCODE -ne 0) {
    throw 'Codex could not list existing MCP registrations.'
}

$servers = @($listJson | ConvertFrom-Json)
$registered = @(
    $servers |
        Where-Object {
            [string]::Equals(
                (Get-OptionalProperty `
                    -InputObject $_ `
                    -Name 'name'),
                $ServerName,
                [StringComparison]::OrdinalIgnoreCase)
        }
)
$matches = $false
if ($registered.Count -gt 0) {
    Write-Verbose "Reading existing MCP registration '$ServerName'."
    $currentJson = & $CodexCommand `
        mcp get $ServerName --json
    if ($LASTEXITCODE -ne 0) {
        throw "Codex could not read MCP registration '$ServerName'."
    }

    $current = $currentJson | ConvertFrom-Json
    $transport = Get-OptionalProperty `
        -InputObject $current `
        -Name 'transport'
    $directCommand = Get-OptionalProperty `
        -InputObject $current `
        -Name 'command'
    $currentCommand = if ($null -ne $directCommand) {
        $directCommand
    }
    elseif ($null -ne $transport) {
        Get-OptionalProperty `
            -InputObject $transport `
            -Name 'command'
    }
    else {
        $null
    }
    $directArguments = Get-OptionalProperty `
        -InputObject $current `
        -Name 'args'
    $transportArguments = Get-OptionalProperty `
        -InputObject $transport `
        -Name 'args'
    $currentArguments = if ($null -ne $directArguments) {
        @($directArguments)
    }
    elseif ($null -ne $transportArguments) {
        @($transportArguments)
    }
    else {
        @()
    }
    $matches = [string]::Equals(
            $currentCommand,
            $McpCommand,
            [StringComparison]::OrdinalIgnoreCase) `
        -and @($currentArguments).Count -eq 0
}

if ($matches) {
    Write-Verbose "Existing MCP registration '$ServerName' already matches."
    Write-Output "Codex MCP '$ServerName' is already registered."
    Write-Output "Example: codex mcp get $ServerName --json"
    exit 0
}

if ($registered.Count -gt 0) {
    Write-Verbose "Removing outdated MCP registration '$ServerName'."
    & $CodexCommand mcp remove $ServerName
    if ($LASTEXITCODE -ne 0) {
        throw "Codex could not remove the outdated MCP registration '$ServerName'."
    }
}

Write-Verbose "Registering MCP server '$ServerName' with command '$McpCommand'."
& $CodexCommand mcp add $ServerName -- $McpCommand
if ($LASTEXITCODE -ne 0) {
    throw "Codex could not register MCP server '$ServerName'."
}

Write-Output "Codex MCP '$ServerName' registered globally as '$McpCommand'."
Write-Output "Example: codex mcp get $ServerName --json"
