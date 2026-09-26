[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Debug',
    [switch]$Restore,
    [switch]$LockedMode,
    [string]$DotnetPath,
    [string]$DalamudLibPath
)

$projectRoot = $PSScriptRoot
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
if ($LockedMode -and -not $Restore) {
    throw '-LockedMode 必须与 -Restore 一起使用。'
}
$cliHome = Join-Path $projectRoot '.dotnet-home'
$nugetCache = Join-Path $projectRoot '.nuget'
$projectFile = Join-Path $projectRoot 'AutoFatre\AutoFatre.csproj'

if ([string]::IsNullOrWhiteSpace($DotnetPath)) {
    $localDotnetPath = Join-Path $projectRoot '.dotnet\dotnet.exe'
    if (Test-Path -LiteralPath $localDotnetPath) {
        $DotnetPath = $localDotnetPath
    }
    elseif (Get-Command dotnet -ErrorAction SilentlyContinue) {
        $DotnetPath = 'dotnet'
    }
    else {
        throw "找不到 .NET SDK。请安装 dotnet，或通过 -DotnetPath 指定 dotnet.exe。"
    }
}

if ([string]::IsNullOrWhiteSpace($DalamudLibPath)) {
    $DalamudLibPath = $env:AUTOFATRE_DALAMUD_LIB_PATH
}

if ([string]::IsNullOrWhiteSpace($DalamudLibPath)) {
    $DalamudLibPath = $env:DALAMUD_LIB_PATH
}

if ([string]::IsNullOrWhiteSpace($DalamudLibPath)) {
    $DalamudLibPath = $env:DALAMUD_HOME
}

if ([string]::IsNullOrWhiteSpace($DalamudLibPath) -and $env:APPDATA) {
    $runtimeCandidates = @(
        (Join-Path $env:APPDATA 'XIVLauncherCN\addon\Hooks\dev'),
        (Join-Path $env:APPDATA 'XIVLauncher\addon\Hooks\dev')
    )
    $DalamudLibPath = $runtimeCandidates |
        Where-Object { Test-Path -LiteralPath (Join-Path $_ 'Dalamud.dll') } |
        Select-Object -First 1
}

if ([string]::IsNullOrWhiteSpace($DalamudLibPath)) {
    throw "找不到 Dalamud runtime。请通过 -DalamudLibPath、AUTOFATRE_DALAMUD_LIB_PATH、DALAMUD_LIB_PATH 或 DALAMUD_HOME 指定包含 Dalamud.dll 的目录。"
}

$resolvedDalamudPath = Resolve-Path -LiteralPath $DalamudLibPath -ErrorAction Stop
$DalamudLibPath = $resolvedDalamudPath.Path
if (-not (Test-Path -LiteralPath (Join-Path $DalamudLibPath 'Dalamud.dll'))) {
    throw "Dalamud runtime 目录中缺少 Dalamud.dll：$DalamudLibPath"
}

New-Item -ItemType Directory -Force -Path $cliHome, $nugetCache | Out-Null
$env:DOTNET_CLI_HOME = $cliHome
$env:NUGET_PACKAGES = $nugetCache
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'

$buildArguments = @(
    'build',
    $projectFile,
    '--no-restore',
    '--configuration',
    $Configuration,
    "-p:DalamudLibPath=$DalamudLibPath"
)

if ($Restore) {
    $restoreArguments = @('restore', $projectFile, "-p:DalamudLibPath=$DalamudLibPath")
    if ($LockedMode) { $restoreArguments += '--locked-mode' }
    & $DotnetPath @restoreArguments
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
}

& $DotnetPath @buildArguments
exit $LASTEXITCODE
