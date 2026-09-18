param(
    [Parameter(Mandatory = $true)][string]$ArtifactDir,
    [Parameter(Mandatory = $true)][string]$Version,
    [Parameter(Mandatory = $true)][string]$OutputDir
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

. (Join-Path $PSScriptRoot 'tooling-contract.ps1')

$artifact = (Resolve-Path -LiteralPath $ArtifactDir).Path

foreach ($file in Get-ToolingRequiredFiles) {
    $path = Join-Path $artifact $file
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
        throw "Required tooling artifact file is missing: $path"
    }
}

$manifest = Assert-ToolingManifest -ManifestPath (Join-Path $artifact 'tooling.manifest.json') -ExpectedVersion $Version
$entries = Assert-ToolingChecksums -BundleDir $artifact

New-Item -ItemType Directory -Force -Path $OutputDir | Out-Null
$outputRoot = (Resolve-Path -LiteralPath $OutputDir).Path
$assetName = "CvoloLanguageServerTooling-$Version.zip"
$archive = Join-Path $outputRoot $assetName
if (Test-Path -LiteralPath $archive) {
    Remove-Item -LiteralPath $archive -Force
}

Compress-Archive -Path (Join-Path $artifact '*') -DestinationPath $archive -CompressionLevel Optimal

if (-not ('System.IO.Compression.ZipFile' -as [type])) {
    Add-Type -AssemblyName System.IO.Compression.FileSystem
}

$sourceRelative = @(Get-ToolingBundleFiles -BundleDir $artifact)
[Array]::Sort($sourceRelative, [System.StringComparer]::Ordinal)

$zip = [System.IO.Compression.ZipFile]::OpenRead($archive)
try {
    $archiveRelative = @($zip.Entries | Where-Object { -not $_.FullName.EndsWith('/') } | ForEach-Object { $_.FullName.Replace('\', '/') })
}
finally {
    $zip.Dispose()
}
[Array]::Sort($archiveRelative, [System.StringComparer]::Ordinal)

if ($sourceRelative.Count -ne $archiveRelative.Count) {
    throw "Packaged archive file count ($($archiveRelative.Count)) does not match the verified artifact ($($sourceRelative.Count))."
}
for ($i = 0; $i -lt $sourceRelative.Count; $i++) {
    if ($sourceRelative[$i] -cne $archiveRelative[$i]) {
        throw "Packaged archive file set mismatch at index ${i}: expected '$($sourceRelative[$i])', archive '$($archiveRelative[$i])'."
    }
}

$hash = (Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash.ToLowerInvariant()
[IO.File]::WriteAllText(
    (Join-Path $outputRoot "$assetName.sha256"),
    "$hash  $assetName`n",
    [Text.UTF8Encoding]::new($false)
)

Write-Host "Created $archive"
Write-Host "  ToolingVersion: $($manifest.ToolingVersion)"
Write-Host "  CompilerCompatibilityLine: $($manifest.CompilerCompatibilityLine)"
Write-Host "  BuiltFromCompilerVersion: $($manifest.BuiltFromCompilerVersion)"
Write-Host "  Commit: $($manifest.Commit)"
Write-Host "  Verified files: $($entries.Count)"
