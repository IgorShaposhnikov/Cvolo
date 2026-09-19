Set-StrictMode -Version Latest

function Get-ToolingRequiredFiles {
    @(
        'Cvolo.Compiler.Tooling.dll',
        'tooling.manifest.json',
        'SHA256SUMS.txt'
    )
}

function Get-ToolingBundleFiles {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)][string]$BundleDir
    )

    $root = (Resolve-Path -LiteralPath $BundleDir).Path
    $files = Get-ChildItem -LiteralPath $root -Recurse -File | ForEach-Object {
        $_.FullName.Substring($root.Length).TrimStart([char[]]@('\', '/')).Replace('\', '/')
    }

    return @($files)
}

function Assert-ToolingManifest {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)][string]$ManifestPath,
        [Parameter(Mandatory = $true)][string]$ExpectedVersion
    )

    if (-not (Test-Path -LiteralPath $ManifestPath -PathType Leaf)) {
        throw "Tooling manifest is missing: $ManifestPath"
    }

    $manifest = Get-Content -LiteralPath $ManifestPath -Raw | ConvertFrom-Json

    if ($manifest.ToolingVersion -ne $ExpectedVersion) {
        throw "Manifest ToolingVersion '$($manifest.ToolingVersion)' does not match expected version '$ExpectedVersion'."
    }
    if ($manifest.CompilerCompatibilityLine -notmatch '^\d+\.\d+(\.\d+)*$') {
        throw "Manifest CompilerCompatibilityLine '$($manifest.CompilerCompatibilityLine)' is not a valid compatibility line."
    }
    if ([string]::IsNullOrWhiteSpace([string]$manifest.BuiltFromCompilerVersion)) {
        throw 'Manifest BuiltFromCompilerVersion is empty.'
    }
    if ([string]::IsNullOrWhiteSpace([string]$manifest.TargetFramework)) {
        throw 'Manifest TargetFramework is empty.'
    }
    if ($null -ne $manifest.RuntimeIdentifier) {
        throw "Manifest RuntimeIdentifier must be null for RID-neutral managed tooling, got '$($manifest.RuntimeIdentifier)'."
    }
    if ([string]::IsNullOrWhiteSpace([string]$manifest.Commit)) {
        throw 'Manifest Commit is empty.'
    }

    return $manifest
}

function Assert-ToolingChecksums {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)][string]$BundleDir
    )

    $root = (Resolve-Path -LiteralPath $BundleDir).Path
    $sumsPath = Join-Path $root 'SHA256SUMS.txt'
    if (-not (Test-Path -LiteralPath $sumsPath -PathType Leaf)) {
        throw "SHA256SUMS.txt is missing from the tooling bundle: $root"
    }

    $bytes = [IO.File]::ReadAllBytes($sumsPath)
    if ($bytes.Length -gt 0 -and $bytes[0] -eq 0xEF) {
        throw 'SHA256SUMS.txt must be UTF-8 without a BOM.'
    }
    if ($bytes -contains 0x0D) {
        throw 'SHA256SUMS.txt must use LF line endings only.'
    }

    $text = [Text.Encoding]::UTF8.GetString($bytes)
    $lines = $text -split "`n"
    if ($lines.Count -gt 0 -and $lines[$lines.Count - 1] -eq '') {
        $lines = @($lines[0..($lines.Count - 2)])
    }

    $entries = [System.Collections.Generic.List[object]]::new()
    $previous = $null
    foreach ($line in $lines) {
        if ($line -notmatch '^([0-9a-f]{64})  (.+)$') {
            throw "Malformed SHA256SUMS.txt line: '$line'"
        }

        $hash = $Matches[1]
        $relative = $Matches[2]
        if ($relative.Contains('\')) {
            throw "SHA256SUMS.txt path must use forward slashes: '$relative'"
        }
        if ($relative.StartsWith('/') -or $relative -match '^[A-Za-z]:') {
            throw "SHA256SUMS.txt path must be relative: '$relative'"
        }
        if ($null -ne $previous -and [string]::CompareOrdinal($previous, $relative) -ge 0) {
            throw "SHA256SUMS.txt entries must be unique and sorted Ordinal: '$previous' then '$relative'."
        }

        $entries.Add([pscustomobject]@{ Hash = $hash; RelativePath = $relative })
        $previous = $relative
    }

    foreach ($entry in $entries) {
        $filePath = Join-Path $root ($entry.RelativePath.Replace('/', [IO.Path]::DirectorySeparatorChar))
        if (-not (Test-Path -LiteralPath $filePath -PathType Leaf)) {
            throw "SHA256SUMS.txt lists a missing file: $($entry.RelativePath)"
        }

        $actual = (Get-FileHash -LiteralPath $filePath -Algorithm SHA256).Hash.ToLowerInvariant()
        if ($actual -ne $entry.Hash) {
            throw "Checksum mismatch for $($entry.RelativePath): expected $($entry.Hash), actual $actual."
        }
    }

    [string[]]$expected = @(Get-ToolingBundleFiles -BundleDir $root | Where-Object { $_ -ne 'SHA256SUMS.txt' })
    [Array]::Sort($expected, [System.StringComparer]::Ordinal)
    [string[]]$listed = @($entries | ForEach-Object { $_.RelativePath })

    if ($expected.Count -ne $listed.Count) {
        throw "SHA256SUMS.txt must list every bundle file exactly once (expected $($expected.Count), listed $($listed.Count))."
    }
    for ($i = 0; $i -lt $expected.Count; $i++) {
        if ($expected[$i] -cne $listed[$i]) {
            throw "SHA256SUMS.txt file set mismatch at index ${i}: expected '$($expected[$i])', listed '$($listed[$i])'."
        }
    }

    return $entries
}
