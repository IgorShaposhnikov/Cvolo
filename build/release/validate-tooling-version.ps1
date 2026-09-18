param(
    [Parameter(Mandatory = $true)][string]$Tag,
    [string]$PropsPath = "src/Directory.Build.props"
)

$ErrorActionPreference = 'Stop'

$tagPattern = '^tooling-(?<version>(?:0|[1-9]\d*)\.(?:0|[1-9]\d*)\.(?:0|[1-9]\d*))$'
$match = [regex]::Match($Tag, $tagPattern)
if (-not $match.Success) {
    throw "Tooling release tag '$Tag' is invalid. Expected tooling-X.Y.Z (for example tooling-0.0.2)."
}

[xml]$props = Get-Content -LiteralPath $PropsPath
$toolingNode = $props.SelectSingleNode('/Project/PropertyGroup/ToolingVersion')
if ($null -eq $toolingNode -or [string]::IsNullOrWhiteSpace($toolingNode.InnerText)) {
    throw "<ToolingVersion> not found in $PropsPath."
}

$toolingVersion = $toolingNode.InnerText.Trim()
$tagVersion = $match.Groups['version'].Value
if ($tagVersion -cne $toolingVersion) {
    throw "Tooling tag version '$tagVersion' does not exactly match <ToolingVersion> '$toolingVersion'."
}

$compatNode = $props.SelectSingleNode('/Project/PropertyGroup/CompilerCompatibilityLine')
if ($null -eq $compatNode -or [string]::IsNullOrWhiteSpace($compatNode.InnerText)) {
    throw "<CompilerCompatibilityLine> not found in $PropsPath."
}

$compatibilityLine = $compatNode.InnerText.Trim()
if ($compatibilityLine -notmatch '^\d+\.\d+(\.\d+)*$') {
    throw "<CompilerCompatibilityLine> '$compatibilityLine' is not a valid compatibility line."
}

$compilerNode = $props.SelectSingleNode('/Project/PropertyGroup/CvoloCompilerVersion')
$compilerVersion = if ($null -ne $compilerNode) { $compilerNode.InnerText.Trim() } else { '' }
if ([string]::IsNullOrWhiteSpace($compilerVersion) -or $compilerVersion -eq '$(Version)') {
    $versionNode = $props.SelectSingleNode('/Project/PropertyGroup/Version')
    if ($null -eq $versionNode -or [string]::IsNullOrWhiteSpace($versionNode.InnerText)) {
        throw "Neither <CvoloCompilerVersion> nor <Version> is declared in $PropsPath."
    }

    $compilerVersion = $versionNode.InnerText.Trim()
}

if (-not [string]::IsNullOrWhiteSpace($env:GITHUB_OUTPUT)) {
    "version=$toolingVersion" >> $env:GITHUB_OUTPUT
    "compatibilityLine=$compatibilityLine" >> $env:GITHUB_OUTPUT
    "compilerVersion=$compilerVersion" >> $env:GITHUB_OUTPUT
}

Write-Host "Validated tooling release identity: tag=$Tag version=$toolingVersion compatibilityLine=$compatibilityLine compilerVersion=$compilerVersion"
