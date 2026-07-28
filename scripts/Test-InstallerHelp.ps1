[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$scripts = @(
    @{
        Path = Join-Path $PSScriptRoot 'Install-Gateway.ps1'
        Parameters = @(
            'InstallRoot',
            'NonInteractive',
            'NoPathUpdate',
            'SkipBuild',
            'Force',
            'Help'
        )
    },
    @{
        Path = Join-Path $PSScriptRoot 'Install-CodexMcp.ps1'
        Parameters = @(
            'ServerName',
            'McpCommand',
            'CodexCommand',
            'Help'
        )
    },
    @{
        Path = Join-Path $PSScriptRoot 'Install-Skills.ps1'
        Parameters = @(
            'Scope',
            'ProjectPath',
            'Destination',
            'Help'
        )
    }
)

foreach ($script in $scripts) {
    $path = $script.Path
    $name = Split-Path -Leaf $path
    $standardHelp = Get-Help $path
    $fullHelp = Get-Help $path -Full
    $examplesHelp = Get-Help $path -Examples

    if ([string]::IsNullOrWhiteSpace($standardHelp.Synopsis) `
        -or [string]::IsNullOrWhiteSpace($fullHelp.Description.Text)) {
        throw "Comment-based help is incomplete for '$name'."
    }

    $examples = @($examplesHelp.Examples.Example)
    if ($examples.Count -lt 2) {
        throw "Expected multiple help examples for '$name'."
    }

    $documentedParameters = @(
        $fullHelp.Parameters.Parameter |
            ForEach-Object Name
    )
    foreach ($parameter in $script.Parameters) {
        if ($documentedParameters -notcontains $parameter) {
            throw "Parameter '$parameter' is undocumented for '$name'."
        }
    }

    $explicitHelp = & $path -Help | Out-String
    if (-not $explicitHelp.Contains($standardHelp.Synopsis)) {
        throw "Explicit -Help output is incomplete for '$name'."
    }

    $standardSwitchHelp = & $path -? | Out-String
    if (-not $standardSwitchHelp.Contains($standardHelp.Synopsis)) {
        throw "Standard -? output is incomplete for '$name'."
    }
}

Write-Output 'Installer help checks passed.'
