[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release',

    [string]$Version = '0.1.0',

    [string]$OutputDirectory
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$repositoryRoot = Split-Path -Parent $PSScriptRoot
$artifactsRoot = Join-Path $repositoryRoot 'artifacts'
if ([string]::IsNullOrWhiteSpace($OutputDirectory)) {
    $OutputDirectory = $artifactsRoot
}

$outputRoot = [IO.Path]::GetFullPath($OutputDirectory)
$packageName = "TwinCatAgentGateway-$Version-windows"
$stagingRoot = Join-Path `
    $artifactsRoot `
    ".portable-$([Diagnostics.Process]::GetCurrentProcess().Id)"
$packageRoot = Join-Path $stagingRoot $packageName
$archivePath = Join-Path $outputRoot "$packageName.zip"

function Invoke-DotNetPublish {
    param(
        [Parameter(Mandatory)]
        [string]$Project,

        [Parameter(Mandatory)]
        [string]$Destination
    )

    & dotnet publish `
        $Project `
        '--configuration' $Configuration `
        '--no-restore' `
        '--output' $Destination `
        "-p:Version=$Version"
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet publish failed for '$Project' with exit code $LASTEXITCODE."
    }
}

try {
    New-Item -ItemType Directory -Path $packageRoot -Force |
        Out-Null
    New-Item -ItemType Directory -Path $outputRoot -Force |
        Out-Null

    Invoke-DotNetPublish `
        -Project (Join-Path `
            $repositoryRoot `
            'src\TwinCatGateway.Desktop\TwinCatGateway.Desktop.csproj') `
        -Destination (Join-Path $packageRoot 'desktop')
    Invoke-DotNetPublish `
        -Project (Join-Path `
            $repositoryRoot `
            'src\TwinCatGateway.Cli\TwinCatGateway.Cli.csproj') `
        -Destination (Join-Path $packageRoot 'cli')
    Invoke-DotNetPublish `
        -Project (Join-Path `
            $repositoryRoot `
            'src\TwinCatGateway.Mcp\TwinCatGateway.Mcp.csproj') `
        -Destination (Join-Path $packageRoot 'mcp')

    Copy-Item `
        -LiteralPath (Join-Path $repositoryRoot 'appsettings.example.json') `
        -Destination $packageRoot
    Copy-Item `
        -LiteralPath (Join-Path $repositoryRoot 'packaging\PORTABLE_README.md') `
        -Destination (Join-Path $packageRoot 'README.md')
    Copy-Item `
        -LiteralPath (Join-Path $repositoryRoot 'docs') `
        -Destination $packageRoot `
        -Recurse
    Copy-Item `
        -LiteralPath (Join-Path $repositoryRoot 'skills') `
        -Destination $packageRoot `
        -Recurse

    if (Test-Path -LiteralPath $archivePath) {
        Remove-Item -LiteralPath $archivePath -Force
    }

    Compress-Archive `
        -LiteralPath $packageRoot `
        -DestinationPath $archivePath `
        -CompressionLevel Optimal
    Write-Output $archivePath
}
finally {
    if (Test-Path -LiteralPath $stagingRoot) {
        $resolvedStaging = [IO.Path]::GetFullPath($stagingRoot)
        $resolvedArtifacts = [IO.Path]::GetFullPath($artifactsRoot)
        if (-not $resolvedStaging.StartsWith(
                $resolvedArtifacts + [IO.Path]::DirectorySeparatorChar,
                [StringComparison]::OrdinalIgnoreCase)) {
            throw "Refusing to remove staging path outside '$resolvedArtifacts'."
        }

        Remove-Item -LiteralPath $resolvedStaging -Recurse -Force
    }
}
