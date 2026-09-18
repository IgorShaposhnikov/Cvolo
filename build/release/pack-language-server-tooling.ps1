param(
    [Parameter(Mandatory = $true)][string]$ArtifactDir,
    [Parameter(Mandatory = $true)][string]$Version,
    [Parameter(Mandatory = $true)][string]$OutputDir
)

$ErrorActionPreference = 'Stop'

$artifact = (Resolve-Path -LiteralPath $ArtifactDir).Path
$required = @(
    'Cvolo.Compiler.Tooling.dll',
    'tooling.manifest.json',
    'SHA256SUMS.txt'
)

foreach ($file in $required) {
    $path = Join-Path $artifact $file
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
        throw "Required tooling artifact file is missing: $path"
    }
}

$manifest = Get-Content -LiteralPath (Join-Path $artifact 'tooling.manifest.json') -Raw | ConvertFrom-Json
if ($manifest.ToolingVersion -ne $Version) {
    throw "Manifest ToolingVersion '$($manifest.ToolingVersion)' does not match requested version '$Version'."
}

New-Item -ItemType Directory -Force -Path $OutputDir | Out-Null
$archive = Join-Path $OutputDir "CvoloLanguageServerTooling-$Version.zip"
if (Test-Path -LiteralPath $archive) {
    Remove-Item -LiteralPath $archive -Force
}

Compress-Archive -Path (Join-Path $artifact '*') -DestinationPath $archive -CompressionLevel Optimal

$hash = (Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash.ToLowerInvariant()
[IO.File]::WriteAllText(
    (Join-Path $OutputDir "CvoloLanguageServerTooling-$Version.zip.sha256"),
    "$hash  CvoloLanguageServerTooling-$Version.zip`n",
    [Text.UTF8Encoding]::new($false)
)

Write-Host "Created $archive"
