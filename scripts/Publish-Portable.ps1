[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release',

    [string]$OutputDirectory
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$repositoryRoot = Split-Path -Parent $PSScriptRoot
$artifactsRoot = Join-Path $repositoryRoot 'artifacts'
$desktopProject = Join-Path `
    $repositoryRoot `
    'src\TwinCatGateway.Desktop\TwinCatGateway.Desktop.csproj'
if ([string]::IsNullOrWhiteSpace($OutputDirectory)) {
    $OutputDirectory = $artifactsRoot
}

function Get-PackageVersion {
    $versionOutput = @(
        & dotnet msbuild `
            $desktopProject `
            '-nologo' `
            '-target:GetBuildVersion' `
            '-getProperty:NuGetPackageVersion'
    )
    if ($LASTEXITCODE -ne 0) {
        throw "Could not determine the Git-derived package version."
    }

    $resolvedVersion = $versionOutput |
        Where-Object { -not [string]::IsNullOrWhiteSpace($_) } |
        Select-Object -Last 1
    if ([string]::IsNullOrWhiteSpace($resolvedVersion)) {
        throw "Git-derived package version is empty."
    }

    return $resolvedVersion.Trim()
}

$Version = Get-PackageVersion
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
        '--output' $Destination
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet publish failed for '$Project' with exit code $LASTEXITCODE."
    }
}

function Compress-PortableArchive {
    param(
        [Parameter(Mandatory)]
        [string]$Source,

        [Parameter(Mandatory)]
        [string]$Destination
    )

    $maxAttempts = 5
    for ($attempt = 1; $attempt -le $maxAttempts; $attempt++) {
        try {
            if (Test-Path -LiteralPath $Destination) {
                Remove-Item -LiteralPath $Destination -Force
            }

            Compress-Archive `
                -LiteralPath $Source `
                -DestinationPath $Destination `
                -CompressionLevel Optimal
            return
        }
        catch {
            if ($attempt -ge $maxAttempts) {
                throw
            }

            Start-Sleep -Milliseconds 500
        }
    }
}

try {
    New-Item -ItemType Directory -Path $packageRoot -Force |
        Out-Null
    New-Item -ItemType Directory -Path $outputRoot -Force |
        Out-Null

    Invoke-DotNetPublish `
        -Project $desktopProject `
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
        -LiteralPath (Join-Path `
            $repositoryRoot `
            'examples\twincat-gateway.json') `
        -Destination (Join-Path `
            $packageRoot `
            'twincat-gateway.example.json')
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

    Compress-PortableArchive `
        -Source $packageRoot `
        -Destination $archivePath
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
