param(
    [Parameter(Mandatory = $true)][string]$PayloadDir,
    [Parameter(Mandatory = $true)][string]$ExpectedVersion,
    [Parameter(Mandatory = $true)][ValidateSet('win-x64','linux-x64','linux-arm64','osx-arm64')][string]$Rid
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$payload = (Resolve-Path -LiteralPath $PayloadDir).Path
$isWindowsRid = $Rid -eq 'win-x64'
$entrypointName = if ($isWindowsRid) { 'Cvolo.exe' } else { 'Cvolo' }
$compiler = Join-Path $payload $entrypointName

if (-not (Test-Path -LiteralPath $compiler -PathType Leaf)) {
    throw "Missing packaged compiler entrypoint: $compiler"
}

$bundledClangName = if ($isWindowsRid) { 'clang.exe' } else { 'clang' }
$bundledClang = Join-Path $payload $bundledClangName
if (-not (Test-Path -LiteralPath $bundledClang -PathType Leaf)) {
    throw "Release payload for $Rid is missing required bundled '$bundledClangName'. System clang fallback is not accepted for a compiler release."
}

if ($isWindowsRid) {
    $bundledLinker = Join-Path $payload 'lld-link.exe'
    if (-not (Test-Path -LiteralPath $bundledLinker -PathType Leaf)) {
        throw "Release payload for win-x64 is incomplete: bundled 'lld-link.exe' is missing."
    }
}
else {
    $executeMask = [int][System.IO.UnixFileMode]::UserExecute
    $compilerMode = [int][System.IO.File]::GetUnixFileMode($compiler)
    $clangMode = [int][System.IO.File]::GetUnixFileMode($bundledClang)
    if (($compilerMode -band $executeMask) -eq 0) {
        throw "Packaged compiler entrypoint is not executable: $entrypointName"
    }
    if (($clangMode -band $executeMask) -eq 0) {
        throw "Bundled clang is not executable after archive extraction: $bundledClangName"
    }
}

# Explicit Cvolo CLI contract implemented before System.CommandLine dispatch in Program.cs.
$expectedVersionOutput = "Cvolo $ExpectedVersion"
$versionOutput = (& $compiler --version 2>&1 | Out-String).Trim()
$versionExitCode = $LASTEXITCODE
if ($versionExitCode -ne 0) {
    throw "'$entrypointName --version' failed with exit code $versionExitCode. Output: $versionOutput"
}
if ($versionOutput -cne $expectedVersionOutput) {
    throw "Packaged compiler reported '$versionOutput'; expected exact CLI output '$expectedVersionOutput'."
}

$tempRoot = Join-Path ([IO.Path]::GetTempPath()) ("cvolo-release-smoke-" + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $tempRoot | Out-Null

try {
    $source = Join-Path $tempRoot 'Smoke.cvl'
    [IO.File]::WriteAllText($source, "int Main() {`n    return 0;`n}`n", [Text.UTF8Encoding]::new($false))

    # Keep the host PATH intact. The bundled clang driver is allowed to use platform
    # linker/SDK/runtime components supplied by the OS. We prove that Cvolo itself chose
    # the bundled clang through the compiler's existing verbose linker-resolution output.
    $buildLog = Join-Path $tempRoot 'compiler-build.log'
    & $compiler build $source --configuration Release --verbose *> $buildLog
    $buildExitCode = $LASTEXITCODE
    $buildOutput = if (Test-Path -LiteralPath $buildLog) {
        Get-Content -LiteralPath $buildLog -Raw
    }
    else {
        ''
    }

    if (-not [string]::IsNullOrWhiteSpace($buildOutput)) {
        Write-Host $buildOutput.TrimEnd()
    }

    if ($buildExitCode -ne 0) {
        throw "Packaged compiler smoke compilation failed with exit code $buildExitCode."
    }

    $bundledMarker = 'Linking using: bundled-clang...'
    $usedBundledClang = @($buildOutput -split "`r?`n" | Where-Object { $_.Trim() -ceq $bundledMarker }).Count -gt 0
    if (-not $usedBundledClang) {
        throw "Smoke compile did not prove bundled clang selection. Expected verbose marker '$bundledMarker'. System clang fallback must not satisfy the release smoke."
    }

    $programRelative = if ($isWindowsRid) { 'bin/Release/Smoke.exe' } else { 'bin/Release/Smoke' }
    $program = Join-Path $tempRoot $programRelative
    if (-not (Test-Path -LiteralPath $program -PathType Leaf)) {
        throw "Expected smoke output was not produced: $program"
    }

    if (-not $isWindowsRid) {
        $programMode = [int][System.IO.File]::GetUnixFileMode($program)
        if (($programMode -band [int][System.IO.UnixFileMode]::UserExecute) -eq 0) {
            throw "Smoke output exists but is not executable: $program"
        }
    }

    & $program
    $programExitCode = $LASTEXITCODE
    if ($programExitCode -ne 0) {
        throw "Compiled smoke program failed with exit code $programExitCode."
    }
}
finally {
    Remove-Item -LiteralPath $tempRoot -Recurse -Force -ErrorAction SilentlyContinue
}
