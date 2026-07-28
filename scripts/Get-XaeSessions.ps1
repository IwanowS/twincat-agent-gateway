[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Debug',

    [switch]$AsJson
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

if ([Environment]::Is64BitProcess) {
    $x86PowerShell = Join-Path `
        $env:WINDIR `
        'SysWOW64\WindowsPowerShell\v1.0\powershell.exe'
    if (-not (Test-Path -LiteralPath $x86PowerShell)) {
        throw "x86 Windows PowerShell was not found at '$x86PowerShell'."
    }

    $childArguments = @(
        '-NoProfile'
        '-ExecutionPolicy'
        'Bypass'
        '-File'
        $PSCommandPath
        '-Configuration'
        $Configuration
    )
    if ($AsJson) {
        $childArguments += '-AsJson'
    }

    & $x86PowerShell @childArguments
    exit $LASTEXITCODE
}

$repositoryRoot = Split-Path -Parent $PSScriptRoot
$xaeAssemblyPath = Join-Path `
    $repositoryRoot `
    "src\TwinCatGateway.Xae\bin\$Configuration\net48\TwinCatGateway.Xae.dll"

if (-not (Test-Path -LiteralPath $xaeAssemblyPath)) {
    throw @"
TwinCatGateway.Xae is not built at '$xaeAssemblyPath'.
Run:
  dotnet build src\TwinCatGateway.Xae\TwinCatGateway.Xae.csproj --configuration $Configuration
"@
}

$xaeAssembly = [Reflection.Assembly]::LoadFrom($xaeAssemblyPath)
$scannerType = $xaeAssembly.GetType(
    'TwinCatGateway.Xae.RunningObjectTableScanner',
    $true)
$scanMethod = $scannerType.GetMethod(
    'Scan',
    [Reflection.BindingFlags]'Public, Static')
if ($null -eq $scanMethod) {
    throw 'RunningObjectTableScanner.Scan was not found.'
}

$processes = @(
    Get-Process -Name 'TcXaeShell', 'devenv' -ErrorAction SilentlyContinue
)
$processById = @{}
foreach ($process in $processes) {
    $processById[$process.Id] = $process
}

$sessionsByProcessId = @{}
$scan = $null
try {
    $scan = $scanMethod.Invoke($null, @($null, $null))
    $candidatesProperty = $scan.GetType().GetProperty('Candidates')
    foreach ($candidate in $candidatesProperty.GetValue($scan)) {
        $infoProperty = $candidate.GetType().GetProperty('Info')
        $info = $infoProperty.GetValue($candidate)
        $processId = $info.ProcessId
        $process = if ($null -ne $processId) {
            $processById[$processId]
        }
        else {
            $null
        }

        $session = [pscustomobject][ordered]@{
            ProcessId = $processId
            ProcessName = if ($null -ne $process) {
                $process.ProcessName
            }
            else {
                $null
            }
            MainWindowHandle = if ($null -ne $process) {
                [long]$process.MainWindowHandle
            }
            else {
                0
            }
            MainWindowTitle = if ($null -ne $process) {
                $process.MainWindowTitle
            }
            else {
                $null
            }
            StartTime = if ($null -ne $process) {
                $process.StartTime.ToString('o')
            }
            else {
                $null
            }
            ProgId = $info.ProgId
            DteVersion = $info.Version
            Solution = $info.Solution
            SolutionLoaded = $info.SolutionLoaded
            RotVisible = $true
            InspectionError = $info.InspectionError
            InspectionHResult = $info.InspectionHResult
        }

        if ($null -ne $processId) {
            $sessionsByProcessId[[int]$processId] = $session
        }
        else {
            $sessionsByProcessId["rot:$($info.Moniker)"] = $session
        }
    }
}
finally {
    if ($null -ne $scan) {
        $scan.Dispose()
    }
}

foreach ($process in $processes) {
    if ($sessionsByProcessId.ContainsKey($process.Id)) {
        continue
    }

    $sessionsByProcessId[$process.Id] =
        [pscustomobject][ordered]@{
            ProcessId = $process.Id
            ProcessName = $process.ProcessName
            MainWindowHandle = [long]$process.MainWindowHandle
            MainWindowTitle = $process.MainWindowTitle
            StartTime = $process.StartTime.ToString('o')
            ProgId = $null
            DteVersion = $null
            Solution = $null
            SolutionLoaded = $false
            RotVisible = $false
            InspectionError =
                'Process is visible but its DTE is absent from this ROT.'
            InspectionHResult = $null
        }
}

$sessions = @(
    $sessionsByProcessId.Values |
        Sort-Object ProcessId, ProgId
)
$result = [pscustomobject][ordered]@{
    ProcessCount = $processes.Count
    RotSessionCount = @(
        $sessions | Where-Object RotVisible
    ).Count
    LoadedSolutionCount = @(
        $sessions | Where-Object SolutionLoaded
    ).Count
    Sessions = $sessions
}

if ($AsJson) {
    $result | ConvertTo-Json -Depth 5
    exit 0
}

Write-Output (
    'XAE/Visual Studio processes: {0}; ROT sessions: {1}; loaded solutions: {2}' `
        -f $result.ProcessCount,
        $result.RotSessionCount,
        $result.LoadedSolutionCount)

if ($sessions.Count -eq 0) {
    Write-Output 'No XAE or Visual Studio processes were found.'
    exit 0
}

$sessions |
    Select-Object `
        ProcessId,
        ProcessName,
        MainWindowHandle,
        MainWindowTitle,
        StartTime,
        ProgId,
        DteVersion,
        Solution,
        RotVisible,
        InspectionError |
    Format-Table -AutoSize -Wrap
