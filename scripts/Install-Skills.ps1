<#
.SYNOPSIS
Installs the repository's TwinCAT agent skills.

.DESCRIPTION
Copies all shipped skill directories from the repository into a user-wide,
project-local, or explicitly selected destination. Skill installation is
separate from application installation and does not start the gateway or
TwinCAT XAE.

.PARAMETER Scope
Selects the default destination. User installs into
%USERPROFILE%\.agents\skills. Project installs into .agents\skills below
ProjectPath, or below the current directory when ProjectPath is omitted.

.PARAMETER ProjectPath
Project root used when Scope is Project. It is ignored when Destination is
specified and optional when the current directory is the intended project.

.PARAMETER Destination
Explicit skills destination. When specified, it takes precedence over Scope
and ProjectPath.

.PARAMETER Help
Displays the full help for this script and exits without creating directories
or copying skills.

.EXAMPLE
.\scripts\Install-Skills.ps1 -Scope User

Installs the TwinCAT skills for the current user.

.EXAMPLE
.\scripts\Install-Skills.ps1 -Scope Project -ProjectPath C:\repos\Machine

Installs the skills into C:\repos\Machine\.agents\skills.

.EXAMPLE
.\scripts\Install-Skills.ps1 -Destination C:\AgentData\skills -Verbose

Installs into an explicit destination and shows each copied skill.

.EXAMPLE
.\scripts\Install-Skills.ps1 -Help

Displays full help and performs no file changes.

.NOTES
Existing shipped files with the same names are overwritten. Extra files
already present in a destination skill directory are not removed. The main
gateway installer intentionally does not install skills.
#>
[CmdletBinding()]
param(
    [ValidateSet('User', 'Project')]
    [string]$Scope = 'User',

    [string]$ProjectPath,

    [string]$Destination,

    [switch]$Help
)

if ($Help) {
    Get-Help $PSCommandPath -Full
    return
}

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$repositoryRoot = Split-Path -Parent $PSScriptRoot
$skillsSource = Join-Path $repositoryRoot 'skills'
Write-Verbose "Skills source: '$skillsSource'."
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
Write-Verbose "Skills destination: '$resolvedDestination'."
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
    Write-Verbose "Installing skill '$($skill.Name)'."
    Copy-Item `
        -LiteralPath $skill.FullName `
        -Destination $resolvedDestination `
        -Recurse `
        -Force
}

Write-Output "Installed $($skills.Count) TwinCAT skills to '$resolvedDestination'."
