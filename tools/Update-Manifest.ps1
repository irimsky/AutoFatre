[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$ManifestPath,

    [Parameter(Mandatory = $true)]
    [string]$PluginManifestPath,

    [Parameter(Mandatory = $true)]
    [ValidatePattern('^https://github\.com/[^/]+/[^/]+$')]
    [string]$RepositoryUrl,

    [Parameter(Mandatory = $true)]
    [ValidatePattern('^v?\d+\.\d+\.\d+(\.\d+)?$')]
    [string]$AssemblyVersion,

    [Parameter(Mandatory = $true)]
    [ValidatePattern('^v?\d+\.\d+\.\d+(\.\d+)?$')]
    [string]$ReleaseTag,

    [Parameter(Mandatory = $true)]
    [long]$DownloadCount,

    [Parameter(Mandatory = $true)]
    [long]$LastUpdate
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

foreach ($path in @($ManifestPath, $PluginManifestPath)) {
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
        throw "Required manifest file was not found: $path"
    }
}

$metadata = Get-Content -LiteralPath $PluginManifestPath -Raw | ConvertFrom-Json
$entries = @(Get-Content -LiteralPath $ManifestPath -Raw | ConvertFrom-Json)

$existingIndex = -1
for ($i = 0; $i -lt $entries.Count; $i++) {
    if ($entries[$i].InternalName -eq $metadata.InternalName) {
        $existingIndex = $i
        break
    }
}

$entry = [ordered]@{}
if ($existingIndex -ge 0) {
    foreach ($property in $entries[$existingIndex].PSObject.Properties) {
        $entry[$property.Name] = $property.Value
    }
}

# The plugin JSON is the source of truth for published manifest fields. Keep
# repository and local installation state owned by this script or Dalamud.
$localOnlyFields = @(
    'WorkingPluginId', 'InstalledFromUrl', 'Testing', 'Disabled',
    'ScheduledForDeletion', 'PluginDirectory', 'DownloadCount', 'LastUpdate',
    'RepoUrl', 'DownloadLinkInstall', 'DownloadLinkUpdate', 'AssemblyVersion'
)
foreach ($property in $metadata.PSObject.Properties) {
    if ($property.Name -notin $localOnlyFields) {
        $entry[$property.Name] = $property.Value
    }
}

$entry['RepoUrl'] = $RepositoryUrl
$entry['AssemblyVersion'] = $AssemblyVersion
$entry['DownloadLinkInstall'] = "$RepositoryUrl/releases/download/$ReleaseTag/latest.zip"
$entry['DownloadLinkUpdate'] = "$RepositoryUrl/releases/download/$ReleaseTag/latest.zip"
$entry['DownloadCount'] = $DownloadCount
$entry['LastUpdate'] = $LastUpdate

if ($existingIndex -ge 0) {
    $entries[$existingIndex] = [pscustomobject]$entry
}
else {
    $entries += [pscustomobject]$entry
}

$json = ConvertTo-Json -InputObject ([object[]]$entries) -Depth 20
Set-Content -LiteralPath $ManifestPath -Value $json -Encoding utf8NoBOM

Write-Host "Updated manifest entry '$($entry.InternalName)' in $ManifestPath"
Write-Host "AssemblyVersion: $AssemblyVersion; LastUpdate: $LastUpdate; DownloadCount: $DownloadCount"
