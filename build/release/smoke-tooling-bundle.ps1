param(
    [Parameter(Mandatory = $true)][string]$ArchivePath,
    [Parameter(Mandatory = $true)][string]$ExpectedVersion,
    [string]$TestProject,
    [string]$Configuration = 'Release',
    [string]$RepoRoot
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

. (Join-Path $PSScriptRoot 'tooling-contract.ps1')

$archive = (Resolve-Path -LiteralPath $ArchivePath).Path
if (-not (Test-Path -LiteralPath $archive -PathType Leaf)) {
    throw "Tooling archive not found: $archive"
}

$tempRoot = Join-Path ([IO.Path]::GetTempPath()) ("cvolo-tooling-smoke-" + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Force -Path $tempRoot | Out-Null

try {
    Expand-Archive -LiteralPath $archive -DestinationPath $tempRoot -Force

    foreach ($file in Get-ToolingRequiredFiles) {
        $path = Join-Path $tempRoot $file
        if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
            throw "Final tooling bundle is missing required file '$file'."
        }
    }

    $manifest = Assert-ToolingManifest -ManifestPath (Join-Path $tempRoot 'tooling.manifest.json') -ExpectedVersion $ExpectedVersion
    $entries = Assert-ToolingChecksums -BundleDir $tempRoot

    Write-Host "Final bundle verified: $($entries.Count) files, ToolingVersion=$($manifest.ToolingVersion), CompilerCompatibilityLine=$($manifest.CompilerCompatibilityLine), Commit=$($manifest.Commit)."

    if (-not [string]::IsNullOrWhiteSpace($TestProject)) {
        $testPath = if (Test-Path -LiteralPath $TestProject -PathType Leaf) {
            (Resolve-Path -LiteralPath $TestProject).Path
        }
        else {
            $TestProject
        }

        $workingDirectory = if ([string]::IsNullOrWhiteSpace($RepoRoot)) {
            (Get-Location).Path
        }
        else {
            (Resolve-Path -LiteralPath $RepoRoot).Path
        }

        $previousOverride = $env:CVOLO_TOOLING_ARTIFACT_DIR
        $env:CVOLO_TOOLING_ARTIFACT_DIR = $tempRoot
        try {
            Push-Location -LiteralPath $workingDirectory
            try {
                & dotnet test $testPath -c $Configuration --no-build --nologo -- RunConfiguration.TestSessionTimeout=1800000
                if ($LASTEXITCODE -ne 0) {
                    throw "Tooling contract/consumer tests against the final bundle failed with exit code $LASTEXITCODE."
                }
            }
            finally {
                Pop-Location
            }
        }
        finally {
            if ($null -eq $previousOverride) {
                Remove-Item Env:CVOLO_TOOLING_ARTIFACT_DIR -ErrorAction SilentlyContinue
            }
            else {
                $env:CVOLO_TOOLING_ARTIFACT_DIR = $previousOverride
            }
        }
    }
}
finally {
    Remove-Item -LiteralPath $tempRoot -Recurse -Force -ErrorAction SilentlyContinue
}
