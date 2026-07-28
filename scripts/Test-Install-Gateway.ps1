[CmdletBinding()]
param(
    [switch]$SkipBuild
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

. (Join-Path $PSScriptRoot 'Install-Gateway.Common.ps1')

$testRoot = Join-Path `
    ([IO.Path]::GetTempPath()) `
    "TwinCatGatewayInstallTests-$([Guid]::NewGuid().ToString('N'))"
$installer = Join-Path `
    $PSScriptRoot `
    'Install-Gateway.ps1'

try {
    $arguments = @{
        InstallRoot = $testRoot
        NonInteractive = $true
        NoPathUpdate = $true
    }
    if ($SkipBuild) {
        $arguments.SkipBuild = $true
    }

    $firstOutput = & $installer @arguments | Out-String
    foreach ($expectedExample in @(
            'twincat-gateway',
            'twincat-gateway-mcp')) {
        if (-not $firstOutput.Contains($expectedExample)) {
            throw "Install output did not include the '$expectedExample' command example."
        }
    }

    $applicationRoot = Join-Path $testRoot 'app'
    $preservedConfiguration =
        Join-Path $testRoot 'twincat-gateway.json'
    $preservedLog = Join-Path $testRoot 'Logs\preserved.log'
    New-Item `
        -ItemType Directory `
        -Path (Split-Path -Parent $preservedLog) `
        -Force |
        Out-Null
    [IO.File]::WriteAllText(
        $preservedConfiguration,
        '{"schemaVersion":1}')
    [IO.File]::WriteAllText(
        $preservedLog,
        'preserve')

    $replacementFailedClosed = $false
    try {
        & $installer `
            -InstallRoot $testRoot `
            -NonInteractive `
            -NoPathUpdate `
            -SkipBuild |
            Out-Null
    }
    catch {
        $replacementFailedClosed =
            $_.Exception.Message.Contains(
                'Rerun with -Force')
    }

    if (-not $replacementFailedClosed) {
        throw 'Non-interactive replacement did not require -Force.'
    }

    $legacyVersion = Join-Path $testRoot 'versions\legacy'
    New-Item `
        -ItemType Directory `
        -Path $legacyVersion `
        -Force |
        Out-Null
    [IO.File]::WriteAllText(
        (Join-Path $legacyVersion 'marker.txt'),
        'legacy')

    & $installer `
        -InstallRoot $testRoot `
        -NonInteractive `
        -NoPathUpdate `
        -SkipBuild `
        -Force |
        Out-Null

    $installedMcpDirectory = Join-Path $applicationRoot 'mcp'
    $installedMcpMarker = Join-Path $installedMcpDirectory 'preserved.marker'
    $installedMcpShim = Join-Path `
        $testRoot `
        'bin\twincat-gateway-mcp.cmd'
    [IO.File]::WriteAllText(
        $installedMcpMarker,
        'preserve-mcp')
    $mcpShimBefore = [IO.File]::ReadAllBytes($installedMcpShim)
    $gatewayOnlyOutput = & $installer `
        -InstallRoot $testRoot `
        -NonInteractive `
        -SkipBuild `
        -GatewayOnly `
        -Force |
        Out-String
    if (-not (Test-Path -LiteralPath $installedMcpMarker) `
        -or [IO.File]::ReadAllText($installedMcpMarker) `
            -ne 'preserve-mcp') {
        throw 'Gateway-only replacement modified the MCP directory.'
    }

    $mcpShimAfter = [IO.File]::ReadAllBytes($installedMcpShim)
    if (-not [Linq.Enumerable]::SequenceEqual(
            [byte[]]$mcpShimBefore,
            [byte[]]$mcpShimAfter)) {
        throw 'Gateway-only replacement modified the MCP command shim.'
    }

    if (-not $gatewayOnlyOutput.Contains('MCP adapter: preserved') `
        -or -not $gatewayOnlyOutput.Contains(
            'Codex restart: not required')) {
        throw 'Gateway-only replacement output omitted restart guidance.'
    }

    if (-not (Test-Path -LiteralPath $preservedConfiguration) `
        -or -not (Test-Path -LiteralPath $preservedLog)) {
        throw 'Replacement removed configuration or logs.'
    }

    if (Test-Path -LiteralPath (Join-Path $testRoot 'versions')) {
        throw 'Replacement retained the legacy versions directory.'
    }

    $commandDirectory = Join-Path $testRoot 'bin'
    foreach ($command in @(
            'twincat-gateway.cmd',
            'twincat-gateway-mcp.cmd')) {
        $path = Join-Path $commandDirectory $command
        if (-not (Test-Path -LiteralPath $path)) {
            throw "Installed command '$path' was not found."
        }
    }

    $originalProcessPath = $env:Path
    try {
        $env:Path = "$commandDirectory$([IO.Path]::PathSeparator)$env:Path"
        foreach ($commandName in @(
                'twincat-gateway',
                'twincat-gateway-mcp')) {
            $resolvedCommand = Get-Command `
                -Name $commandName `
                -CommandType Application `
                -ErrorAction Stop
            if (-not $resolvedCommand.Source.StartsWith(
                    $commandDirectory,
                    [StringComparison]::OrdinalIgnoreCase)) {
                throw "Command '$commandName' did not resolve through the installed shim."
            }
        }

        $mcpHelp = & (Join-Path `
                $commandDirectory `
                'twincat-gateway-mcp.cmd') `
            '--help' |
            Out-String
        if ($LASTEXITCODE -ne 0 `
            -or -not $mcpHelp.Contains(
                '--gateway-command <command>') `
            -or -not $mcpHelp.Contains(
                '[default:')) {
            throw (
                "Installed MCP command help is incomplete. Exit code: " +
                "$LASTEXITCODE. Output: $mcpHelp")
        }
    }
    finally {
        $env:Path = $originalProcessPath
    }

    $pathOnce = Get-PathWithEntry `
        -CurrentPath 'C:\Windows' `
        -Entry $commandDirectory
    $pathTwice = Get-PathWithEntry `
        -CurrentPath $pathOnce.Value `
        -Entry $commandDirectory
    if (-not $pathOnce.Changed `
        -or $pathTwice.Changed `
        -or $pathOnce.Value -ne $pathTwice.Value) {
        throw 'PATH update is not idempotent.'
    }

    $desktopTarget = Join-Path `
        $applicationRoot `
        'gateway\twincat-gateway.exe'
    $mcpTarget = Join-Path `
        $applicationRoot `
        'mcp\twincat-gateway-mcp.exe'
    $configurationReferenceTarget = Join-Path `
        $applicationRoot `
        'gateway\CONFIGURATION.md'
    if (-not (Test-Path -LiteralPath $desktopTarget) `
        -or -not (Test-Path -LiteralPath $mcpTarget) `
        -or -not (Test-Path `
            -LiteralPath $configurationReferenceTarget)) {
        throw 'Installed application artifacts are incomplete.'
    }

    Write-Output "Install smoke passed: $testRoot"
}
finally {
    if (Test-Path -LiteralPath $testRoot) {
        $resolvedTestRoot = [IO.Path]::GetFullPath($testRoot)
        $resolvedTemporaryRoot = [IO.Path]::GetFullPath(
            [IO.Path]::GetTempPath())
        if (-not $resolvedTestRoot.StartsWith(
                $resolvedTemporaryRoot,
                [StringComparison]::OrdinalIgnoreCase)) {
            throw "Refusing to remove test path '$resolvedTestRoot'."
        }

        Remove-Item `
            -LiteralPath $resolvedTestRoot `
            -Recurse `
            -Force
    }
}
