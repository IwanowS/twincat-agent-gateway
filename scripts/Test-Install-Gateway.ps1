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

    $versionsAfterFirst = @(
        Get-ChildItem `
            -LiteralPath (Join-Path $testRoot 'versions') `
            -Directory
    )
    & $installer `
        -InstallRoot $testRoot `
        -NonInteractive `
        -NoPathUpdate `
        -SkipBuild |
        Out-Null
    $versionsAfterSecond = @(
        Get-ChildItem `
            -LiteralPath (Join-Path $testRoot 'versions') `
            -Directory
    )

    if ($versionsAfterFirst.Count -ne 1 `
        -or $versionsAfterSecond.Count -ne 1 `
        -or $versionsAfterFirst[0].Name `
            -ne $versionsAfterSecond[0].Name) {
        throw 'Repeated installation was not idempotent.'
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
        $versionsAfterSecond[0].FullName `
        'gateway\twincat-gateway.exe'
    $mcpTarget = Join-Path `
        $versionsAfterSecond[0].FullName `
        'mcp\twincat-gateway-mcp.exe'
    if (-not (Test-Path -LiteralPath $desktopTarget) `
        -or -not (Test-Path -LiteralPath $mcpTarget)) {
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
