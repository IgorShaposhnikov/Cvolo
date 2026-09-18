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

if (-not $isWindowsRid) {
    $executeMask = [int][System.IO.UnixFileMode]::UserExecute
    $compilerMode = [int][System.IO.File]::GetUnixFileMode($compiler)
    if (($compilerMode -band $executeMask) -eq 0) {
        throw "Packaged compiler entrypoint is not executable: $entrypointName"
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

    # The native clang/LLVM toolchain is an external runtime prerequisite. We prove that
    # Cvolo itself resolved a clang linker (bundled when shipped, otherwise system) through
    # the compiler's existing verbose linker-resolution output.
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

    $clangMarker = @($buildOutput -split "`r?`n" | Where-Object { $_.Trim() -match '^Linking using: (bundled-clang|clang)\.\.\.$' }).Count -gt 0
    if (-not $clangMarker) {
        throw "Smoke compile did not select a clang linker. Expected a 'Linking using: clang...' or 'Linking using: bundled-clang...' verbose marker."
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
