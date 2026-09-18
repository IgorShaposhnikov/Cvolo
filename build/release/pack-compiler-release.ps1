param(
    [Parameter(Mandatory = $true)][string]$StageDir,
    [Parameter(Mandatory = $true)][string]$OutDir,
    [Parameter(Mandatory = $true)][string]$Version,
    [Parameter(Mandatory = $true)][ValidateSet('win-x64','linux-x64','linux-arm64','osx-arm64')][string]$Rid,
    [Parameter(Mandatory = $true)][string]$SourceRevision,
    [Parameter(Mandatory = $true)][string]$CompilerCompatibilityLine,
    [string]$TargetFramework = 'net10.0'
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.Formats.Tar

$stage = (Resolve-Path -LiteralPath $StageDir).Path
$out = [IO.Path]::GetFullPath($OutDir)
[IO.Directory]::CreateDirectory($out) | Out-Null

$isWindowsRid = $Rid -eq 'win-x64'
$entrypoint = if ($isWindowsRid) { 'Cvolo.exe' } else { 'Cvolo' }
$entrypointPath = Join-Path $stage $entrypoint
$clangName = if ($isWindowsRid) { 'clang.exe' } else { 'clang' }
$clangPath = Join-Path $stage $clangName

function Get-RelativeReleasePath([string]$Path) {
    return [IO.Path]::GetRelativePath($stage, $Path).Replace('\', '/')
}

function Get-Sha256([string]$Path) {
    $stream = [IO.File]::OpenRead($Path)
    try {
        $sha = [Security.Cryptography.SHA256]::Create()
        try {
            return ([Convert]::ToHexString($sha.ComputeHash($stream))).ToLowerInvariant()
        }
        finally {
            $sha.Dispose()
        }
    }
    finally {
        $stream.Dispose()
    }
}

function Get-StreamSha256([IO.Stream]$Stream) {
    $sha = [Security.Cryptography.SHA256]::Create()
    try {
        return ([Convert]::ToHexString($sha.ComputeHash($Stream))).ToLowerInvariant()
    }
    finally {
        $sha.Dispose()
    }
}

function Test-Executable([string]$Path) {
    if ($isWindowsRid) {
        return [IO.Path]::GetExtension($Path).Equals('.exe', [StringComparison]::OrdinalIgnoreCase)
    }

    $mode = [int][IO.File]::GetUnixFileMode($Path)
    return (($mode -band [int][IO.UnixFileMode]::UserExecute) -ne 0)
}

if (-not (Test-Path -LiteralPath $entrypointPath -PathType Leaf)) {
    throw "Missing production compiler entrypoint: $entrypointPath"
}

if (-not (Test-Path -LiteralPath (Join-Path $stage 'libraries') -PathType Container)) {
    throw "Release payload is missing the Cvolo standard-library directory: libraries/"
}
if (-not (Get-ChildItem -LiteralPath (Join-Path $stage 'libraries') -File -Recurse | Select-Object -First 1)) {
    throw "Release payload contains an empty libraries/ directory."
}

# Producer contract: the compiler release carries the native clang driver Cvolo selects.
# That bundled clang may use platform linker/SDK/runtime components supplied by the OS.
# Never allow a release to become green merely because the Actions image has clang installed.
if (-not (Test-Path -LiteralPath $clangPath -PathType Leaf)) {
    throw "Release payload for $Rid is missing bundled '$clangName'. Add the native clang payload under src/Cvolo/tooling/$Rid; system clang fallback is not accepted for a compiler release."
}
if ($isWindowsRid -and -not (Test-Path -LiteralPath (Join-Path $stage 'lld-link.exe') -PathType Leaf)) {
    throw "Release payload for win-x64 is missing bundled 'lld-link.exe'."
}

if (-not $isWindowsRid) {
    if (-not (Test-Executable $entrypointPath)) {
        throw "Published compiler entrypoint is not executable before packaging: $entrypoint"
    }
    if (-not (Test-Executable $clangPath)) {
        throw "Bundled clang is not executable before packaging: $clangName"
    }
}

$allFiles = @(Get-ChildItem -LiteralPath $stage -File -Recurse | Sort-Object { Get-RelativeReleasePath $_.FullName })
if ($allFiles.Count -eq 0) {
    throw "Release staging directory is empty: $stage"
}

# NativeAOT symbol sidecars are useful for debugging but are not required to run the compiler.
# Keep the downloadable compiler payload focused on runtime/compiler dependencies.
$files = @($allFiles | Where-Object {
    $relative = Get-RelativeReleasePath $_.FullName
    -not ($relative.EndsWith('.pdb', [StringComparison]::OrdinalIgnoreCase) -or
          $relative.EndsWith('.dbg', [StringComparison]::OrdinalIgnoreCase) -or
          $relative -match '(^|/)Cvolo\.dSYM/')
})
if ($files.Count -eq 0) {
    throw "Release staging directory is empty: $stage"
}

foreach ($file in $allFiles) {
    $relative = Get-RelativeReleasePath $file.FullName
    if ($relative -match '(^|/)(bin|obj)(/|$)') {
        throw "Unexpected build-layout directory in release payload: $relative"
    }
    if ($relative.EndsWith('.deps.json', [StringComparison]::OrdinalIgnoreCase) -or
        $relative.EndsWith('.runtimeconfig.json', [StringComparison]::OrdinalIgnoreCase)) {
        throw "Managed deployment metadata found in NativeAOT release payload: $relative"
    }
}

$manifestFiles = @()
$expected = @{}
foreach ($file in $files) {
    $relative = Get-RelativeReleasePath $file.FullName
    $sha256 = Get-Sha256 $file.FullName
    $executable = Test-Executable $file.FullName
    $record = [ordered]@{
        relativePath = $relative
        byteSize = [int64]$file.Length
        sha256 = $sha256
        executable = [bool]$executable
    }
    $manifestFiles += $record
    $expected[$relative] = $record
}

$manifest = [ordered]@{
    schemaVersion = 1
    compilerVersion = $Version
    sourceRevision = $SourceRevision
    targetFramework = $TargetFramework
    rid = $Rid
    publishMode = 'native-aot-single-file'
    entrypoint = $entrypoint
    compilerCompatibilityLine = $CompilerCompatibilityLine
    files = $manifestFiles
}

$baseName = "cvolo-compiler-$Version-$Rid"
$manifestPath = Join-Path $out "$baseName.manifest.json"
$archivePath = if ($isWindowsRid) { Join-Path $out "$baseName.zip" } else { Join-Path $out "$baseName.tar.gz" }
Remove-Item -LiteralPath $manifestPath -Force -ErrorAction SilentlyContinue
Remove-Item -LiteralPath $archivePath -Force -ErrorAction SilentlyContinue

$manifestJson = $manifest | ConvertTo-Json -Depth 8
[IO.File]::WriteAllText($manifestPath, $manifestJson + "`n", [Text.UTF8Encoding]::new($false))

$fixedTime = [DateTimeOffset]::new(2000, 1, 1, 0, 0, 0, [TimeSpan]::Zero)

if ($isWindowsRid) {
    $archiveStream = [IO.File]::Create($archivePath)
    try {
        $zip = [IO.Compression.ZipArchive]::new($archiveStream, [IO.Compression.ZipArchiveMode]::Create, $false)
        try {
            foreach ($file in $files) {
                $relative = Get-RelativeReleasePath $file.FullName
                $entry = $zip.CreateEntry($relative, [IO.Compression.CompressionLevel]::Optimal)
                $entry.LastWriteTime = $fixedTime
                $input = [IO.File]::OpenRead($file.FullName)
                $output = $entry.Open()
                try { $input.CopyTo($output) }
                finally { $output.Dispose(); $input.Dispose() }
            }
        }
        finally {
            $zip.Dispose()
        }
    }
    finally {
        $archiveStream.Dispose()
    }
}
else {
    $archiveStream = [IO.File]::Create($archivePath)
    try {
        $gzip = [IO.Compression.GZipStream]::new($archiveStream, [IO.Compression.CompressionLevel]::Optimal, $false)
        try {
            $writer = [System.Formats.Tar.TarWriter]::new($gzip, [System.Formats.Tar.TarEntryFormat]::Pax, $false)
            try {
                foreach ($file in $files) {
                    $relative = Get-RelativeReleasePath $file.FullName
                    $entry = [System.Formats.Tar.PaxTarEntry]::new([System.Formats.Tar.TarEntryType]::RegularFile, $relative)
                    $entry.ModificationTime = $fixedTime
                    $entry.Uid = 0
                    $entry.Gid = 0
                    $entry.UserName = 'root'
                    $entry.GroupName = 'root'
                    $entry.Mode = [IO.File]::GetUnixFileMode($file.FullName)
                    $input = [IO.File]::OpenRead($file.FullName)
                    try {
                        $entry.DataStream = $input
                        $writer.WriteEntry($entry)
                    }
                    finally {
                        $input.Dispose()
                    }
                }
            }
            finally {
                $writer.Dispose()
            }
        }
        finally {
            $gzip.Dispose()
        }
    }
    finally {
        $archiveStream.Dispose()
    }
}

# Re-open the final archive and compare every archived byte to the producer manifest.
$seen = @{}
if ($isWindowsRid) {
    $archiveStream = [IO.File]::OpenRead($archivePath)
    try {
        $zip = [IO.Compression.ZipArchive]::new($archiveStream, [IO.Compression.ZipArchiveMode]::Read, $false)
        try {
            foreach ($entry in $zip.Entries) {
                if ([string]::IsNullOrEmpty($entry.Name)) { continue }
                $relative = $entry.FullName.Replace('\', '/')
                if (-not $expected.ContainsKey($relative)) { throw "Unexpected archive member: $relative" }
                $stream = $entry.Open()
                try { $sha = Get-StreamSha256 $stream }
                finally { $stream.Dispose() }
                $record = $expected[$relative]
                if ([int64]$entry.Length -ne [int64]$record.byteSize) { throw "Archive byte-size mismatch: $relative" }
                if ($sha -cne [string]$record.sha256) { throw "Archive SHA-256 mismatch: $relative" }
                $seen[$relative] = $true
            }
        }
        finally { $zip.Dispose() }
    }
    finally { $archiveStream.Dispose() }
}
else {
    $archiveStream = [IO.File]::OpenRead($archivePath)
    try {
        $gzip = [IO.Compression.GZipStream]::new($archiveStream, [IO.Compression.CompressionMode]::Decompress, $false)
        try {
            $reader = [System.Formats.Tar.TarReader]::new($gzip, $false)
            try {
                while ($null -ne ($entry = $reader.GetNextEntry())) {
                    if ($entry.EntryType -ne [System.Formats.Tar.TarEntryType]::RegularFile) { continue }
                    $relative = $entry.Name.Replace('\', '/')
                    if (-not $expected.ContainsKey($relative)) { throw "Unexpected archive member: $relative" }
                    if ($null -eq $entry.DataStream) { throw "Archive member has no data stream: $relative" }
                    $sha = Get-StreamSha256 $entry.DataStream
                    $record = $expected[$relative]
                    if ([int64]$entry.Length -ne [int64]$record.byteSize) { throw "Archive byte-size mismatch: $relative" }
                    if ($sha -cne [string]$record.sha256) { throw "Archive SHA-256 mismatch: $relative" }
                    $archivedExecutable = (([int]$entry.Mode -band [int][IO.UnixFileMode]::UserExecute) -ne 0)
                    if ([bool]$record.executable -ne $archivedExecutable) { throw "Archive executable-bit mismatch: $relative" }
                    $seen[$relative] = $true
                }
            }
            finally { $reader.Dispose() }
        }
        finally { $gzip.Dispose() }
    }
    finally { $archiveStream.Dispose() }
}

if ($seen.Count -ne $expected.Count) {
    $missing = @($expected.Keys | Where-Object { -not $seen.ContainsKey($_) } | Sort-Object)
    throw "Archive is missing release payload file(s): $($missing -join ', ')"
}

Write-Host "Created and validated $archivePath"
Write-Host "Created producer manifest $manifestPath"
