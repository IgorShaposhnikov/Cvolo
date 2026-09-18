param(
    [Parameter(Mandatory = $true)][string]$Tag,
    [string]$PropsPath = "src/Directory.Build.props"
)

$ErrorActionPreference = 'Stop'

$tagPattern = '^v(?<version>(?:0|[1-9]\d*)\.(?:0|[1-9]\d*)\.(?:0|[1-9]\d*)(?:-(?:alpha|beta)\.(?:0|[1-9]\d*))?)$'
$match = [regex]::Match($Tag, $tagPattern)
if (-not $match.Success) {
    throw "Release tag '$Tag' is invalid. Expected vX.Y.Z, vX.Y.Z-alpha.N, or vX.Y.Z-beta.N."
}

[xml]$props = Get-Content -LiteralPath $PropsPath
$versionNode = $props.SelectSingleNode('/Project/PropertyGroup/Version')
if ($null -eq $versionNode -or [string]::IsNullOrWhiteSpace($versionNode.InnerText)) {
    throw "<Version> not found in $PropsPath."
}

$version = $versionNode.InnerText.Trim()
$tagVersion = $match.Groups['version'].Value
if ($tagVersion -cne $version) {
    throw "Release tag version '$tagVersion' does not exactly match project <Version> '$version'."
}

$compatNode = $props.SelectSingleNode('/Project/PropertyGroup/CompilerCompatibilityLine')
if ($null -eq $compatNode -or [string]::IsNullOrWhiteSpace($compatNode.InnerText)) {
    throw "<CompilerCompatibilityLine> not found in $PropsPath."
}

$compatibilityLine = $compatNode.InnerText.Trim()
$isPrerelease = $version -match '-(?:alpha|beta)\.(?:0|[1-9]\d*)$'

if (-not [string]::IsNullOrWhiteSpace($env:GITHUB_OUTPUT)) {
    "version=$version" >> $env:GITHUB_OUTPUT
    "prerelease=$($isPrerelease.ToString().ToLowerInvariant())" >> $env:GITHUB_OUTPUT
    "compatibilityLine=$compatibilityLine" >> $env:GITHUB_OUTPUT
}

Write-Host "Validated release identity: tag=$Tag version=$version prerelease=$isPrerelease compatibilityLine=$compatibilityLine"
