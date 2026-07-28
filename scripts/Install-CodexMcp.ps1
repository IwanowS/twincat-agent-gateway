[CmdletBinding()]
param(
    [string]$ServerName = 'twincat-gateway-mcp',

    [string]$McpCommand = 'twincat-gateway-mcp',

    [string]$CodexCommand = 'codex'
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

if (-not (Get-Command `
        -Name $CodexCommand `
        -CommandType Application `
        -ErrorAction SilentlyContinue)) {
    throw "Codex CLI command '$CodexCommand' is not available."
}

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
    return if ($null -eq $property) {
        $null
    }
    else {
        $property.Value
    }
}

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
        -and $currentArguments.Count -eq 0
}

if ($matches) {
    Write-Output "Codex MCP '$ServerName' is already registered."
    exit 0
}

if ($registered.Count -gt 0) {
    & $CodexCommand mcp remove $ServerName
    if ($LASTEXITCODE -ne 0) {
        throw "Codex could not remove the outdated MCP registration '$ServerName'."
    }
}

& $CodexCommand mcp add $ServerName -- $McpCommand
if ($LASTEXITCODE -ne 0) {
    throw "Codex could not register MCP server '$ServerName'."
}

Write-Output "Codex MCP '$ServerName' registered globally as '$McpCommand'."
