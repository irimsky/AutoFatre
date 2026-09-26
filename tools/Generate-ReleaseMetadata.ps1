[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$PackagePath,

    [Parameter(Mandatory = $true)]
    [string]$OutputDirectory,

    [Parameter(Mandatory = $true)]
    [ValidatePattern('^v?\d+\.\d+\.\d+(\.\d+)?$')]
    [string]$ReleaseVersion,

    [Parameter(Mandatory = $true)]
    [ValidatePattern('^\d+\.\d+\.\d+(\.\d+)?$')]
    [string]$AssemblyVersion,

    [string]$Repository = '',

    [string]$Commit = ''
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

function Get-StreamHash {
    param(
        [Parameter(Mandatory = $true)]
        [System.IO.Stream]$Stream
    )

    return (Get-FileHash -InputStream $Stream -Algorithm SHA256).Hash.ToLowerInvariant()
}

if (-not (Test-Path -LiteralPath $PackagePath -PathType Leaf)) {
    throw "Release package was not found: $PackagePath"
}

$resolvedPackagePath = (Resolve-Path -LiteralPath $PackagePath).Path
$resolvedOutputDirectory = [System.IO.Path]::GetFullPath($OutputDirectory)
New-Item -ItemType Directory -Path $resolvedOutputDirectory -Force | Out-Null

$checksumPath = Join-Path $resolvedOutputDirectory 'SHA256SUMS.txt'
$inventoryPath = Join-Path $resolvedOutputDirectory 'RELEASE-MANIFEST.json'
$checksum = (Get-FileHash -LiteralPath $resolvedPackagePath -Algorithm SHA256).Hash.ToLowerInvariant()
$packageInfo = Get-Item -LiteralPath $resolvedPackagePath

Add-Type -AssemblyName System.IO.Compression.FileSystem
$archive = [System.IO.Compression.ZipFile]::OpenRead($resolvedPackagePath)
try {
    $packageEntries = foreach ($entry in $archive.Entries | Sort-Object FullName) {
        $entryStream = $entry.Open()
        try {
            [ordered]@{
                Name = $entry.FullName
                Size = [long]$entry.Length
                CompressedSize = [long]$entry.CompressedLength
                Sha256 = Get-StreamHash $entryStream
            }
        }
        finally {
            $entryStream.Dispose()
        }
    }
}
finally {
    $archive.Dispose()
}

$generatedAt = [DateTimeOffset]::UtcNow.ToString('O')
$inventory = [ordered]@{
    SchemaVersion = 1
    Product = 'AutoFatre'
    ReleaseVersion = $ReleaseVersion
    AssemblyVersion = $AssemblyVersion
    Repository = $Repository
    Commit = $Commit
    GeneratedAtUtc = $generatedAt
    Artifacts = @(
        [ordered]@{
            Name = $packageInfo.Name
            Size = [long]$packageInfo.Length
            Sha256 = $checksum
            MediaType = 'application/zip'
        }
    )
    PackageEntries = @($packageEntries)
}

Set-Content -LiteralPath $checksumPath -Encoding utf8NoBOM -Value "$checksum  $($packageInfo.Name)"
Set-Content -LiteralPath $inventoryPath -Encoding utf8NoBOM -Value (ConvertTo-Json $inventory -Depth 10)

Write-Host "Release metadata generated: $resolvedOutputDirectory"
Write-Host "SHA-256: $checksum  $($packageInfo.Name)"
Write-Host "Package entries: $($packageEntries.Count)"
