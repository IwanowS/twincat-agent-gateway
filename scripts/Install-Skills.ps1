[CmdletBinding()]
param(
    [ValidateSet('User', 'Project')]
    [string]$Scope = 'User',

    [string]$ProjectPath,

    [string]$Destination
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$repositoryRoot = Split-Path -Parent $PSScriptRoot
$skillsSource = Join-Path $repositoryRoot 'skills'
if (-not (Test-Path `
        -LiteralPath $skillsSource `
        -PathType Container)) {
    throw "Skills source was not found at '$skillsSource'."
}

if ([string]::IsNullOrWhiteSpace($Destination)) {
    if ($Scope -eq 'User') {
        $Destination = Join-Path `
            $env:USERPROFILE `
            '.agents\skills'
    }
    else {
        if ([string]::IsNullOrWhiteSpace($ProjectPath)) {
            $ProjectPath = (Get-Location).Path
        }

        $Destination = Join-Path `
            ([IO.Path]::GetFullPath($ProjectPath)) `
            '.agents\skills'
    }
}

$resolvedDestination = [IO.Path]::GetFullPath($Destination)
New-Item `
    -ItemType Directory `
    -Path $resolvedDestination `
    -Force |
    Out-Null

$skills = @(
    Get-ChildItem `
        -LiteralPath $skillsSource `
        -Directory |
        Sort-Object Name
)
foreach ($skill in $skills) {
    Copy-Item `
        -LiteralPath $skill.FullName `
        -Destination $resolvedDestination `
        -Recurse `
        -Force
}

Write-Output "Installed $($skills.Count) TwinCAT skills to '$resolvedDestination'."
