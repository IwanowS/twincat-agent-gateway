Set-StrictMode -Version Latest

function Get-PathWithEntry {
    [CmdletBinding()]
    param(
        [AllowNull()]
        [string]$CurrentPath,

        [Parameter(Mandatory)]
        [string]$Entry
    )

    $fullEntry = [IO.Path]::GetFullPath($Entry).TrimEnd(
        [IO.Path]::DirectorySeparatorChar,
        [IO.Path]::AltDirectorySeparatorChar)
    $entries = @(
        if (-not [string]::IsNullOrWhiteSpace($CurrentPath)) {
            $CurrentPath.Split(
                [IO.Path]::PathSeparator,
                [StringSplitOptions]::RemoveEmptyEntries)
        }
    )
    foreach ($candidate in $entries) {
        $trimmed = $candidate.Trim()
        try {
            $normalized = [IO.Path]::GetFullPath($trimmed).TrimEnd(
                [IO.Path]::DirectorySeparatorChar,
                [IO.Path]::AltDirectorySeparatorChar)
        }
        catch {
            $normalized = $trimmed.TrimEnd('\', '/')
        }

        if ([string]::Equals(
                $normalized,
                $fullEntry,
                [StringComparison]::OrdinalIgnoreCase)) {
            return [PSCustomObject]@{
                Changed = $false
                Value = $CurrentPath
            }
        }
    }

    $value = if ($entries.Count -eq 0) {
        $fullEntry
    }
    else {
        [string]::Join(
            [IO.Path]::PathSeparator.ToString(),
            @($entries + $fullEntry))
    }

    return [PSCustomObject]@{
        Changed = $true
        Value = $value
    }
}
