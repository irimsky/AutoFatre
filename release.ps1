[CmdletBinding()]
param(
    [Parameter(Mandatory = $true, Position = 0)]
    [ValidatePattern('^v?\d+\.\d+\.\d+(\.\d+)?$')]
    [string]$Version,

    [switch]$Restore,

    [switch]$SkipRemoteTagCheck,

    [switch]$RequireMainBranch,

    [switch]$RequireCleanWorkspace
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

function Invoke-Checked {
    param(
        [Parameter(Mandatory = $true)]
        [string]$FilePath,

        [string[]]$ArgumentList = @(),

        [string]$WorkingDirectory
    )

    $oldLocation = Get-Location
    try {
        if ($WorkingDirectory) {
            Set-Location -LiteralPath $WorkingDirectory
        }

        & $FilePath @ArgumentList
        if ($LASTEXITCODE -ne 0) {
            throw "命令失败（退出码 $LASTEXITCODE）：$FilePath $($ArgumentList -join ' ')"
        }
    }
    finally {
        Set-Location -LiteralPath $oldLocation
    }
}

function Get-CommandOutput {
    param(
        [Parameter(Mandatory = $true)]
        [string]$FilePath,

        [string[]]$ArgumentList = @(),

        [string]$WorkingDirectory
    )

    $oldLocation = Get-Location
    try {
        if ($WorkingDirectory) {
            Set-Location -LiteralPath $WorkingDirectory
        }

        $output = & $FilePath @ArgumentList 2>&1
        if ($LASTEXITCODE -ne 0) {
            throw "命令失败（退出码 $LASTEXITCODE）：$FilePath $($ArgumentList -join ' ')`n$($output -join [Environment]::NewLine)"
        }

        return ($output -join [Environment]::NewLine).Trim()
    }
    finally {
        Set-Location -LiteralPath $oldLocation
    }
}

function Assert-ReleaseVersionMatchesProject {
    param(
        [Parameter(Mandatory = $true)]
        [string]$RequestedVersion,

        [Parameter(Mandatory = $true)]
        [string]$ProjectVersion
    )

    $requested = ConvertTo-NormalizedVersion $RequestedVersion
    $project = ConvertTo-NormalizedVersion $ProjectVersion
    if ($requested -ne $project) {
        throw "发布版本 $RequestedVersion 与 AutoFatre/AutoFatre.csproj 版本 $ProjectVersion 不一致。请先更新项目版本并提交。"
    }
}

function ConvertTo-NormalizedVersion {
    param([Parameter(Mandatory = $true)][string]$Value)
    $normalized = $Value.Trim()
    if ($normalized.StartsWith('v', [StringComparison]::OrdinalIgnoreCase)) {
        $normalized = $normalized.Substring(1)
    }
    if ($normalized -notmatch '^\d+\.\d+\.\d+(\.\d+)?$') {
        throw "版本必须是三段或四段数字，可带 v 前缀：$Value"
    }
    $parts = @($normalized.Split('.') | ForEach-Object { [int]$_ })
    while ($parts.Count -lt 4) { $parts += 0 }
    return ($parts -join '.')
}

function Assert-AutoFatreWorkspaceClean {
    param(
        [Parameter(Mandatory = $true)]
        [string]$WorkingDirectory
    )

    $status = Get-CommandOutput 'git' @('status', '--porcelain', '--untracked-files=all') $WorkingDirectory
    if (-not [string]::IsNullOrWhiteSpace($status)) {
        throw "AutoFatre 工作区存在未提交或未跟踪的文件：`n$status"
    }
}

$projectRoot = (Resolve-Path -LiteralPath $PSScriptRoot).Path
$projectFile = Join-Path $projectRoot 'AutoFatre\AutoFatre.csproj'
$buildScript = Join-Path $projectRoot 'build.ps1'
$pluginZip = Join-Path $projectRoot 'AutoFatre\bin\Release\AutoFatre\latest.zip'

foreach ($path in @($projectFile, $buildScript)) {
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
        throw "找不到发布前检查所需文件：$path"
    }
}

$branch = Get-CommandOutput 'git' @('branch', '--show-current') $projectRoot
if ($branch -ne 'main') {
    if ($RequireMainBranch) {
        throw "发布前检查要求从 main 分支执行，当前分支为：$branch"
    }
    Write-Warning "当前分支为 '$branch'；未启用 -RequireMainBranch，继续执行发布前检查。"
}

if ($RequireCleanWorkspace) {
    Assert-AutoFatreWorkspaceClean $projectRoot
}
else {
    $status = Get-CommandOutput 'git' @('status', '--porcelain') $projectRoot
    if (-not [string]::IsNullOrWhiteSpace($status)) {
        Write-Warning '工作区存在改动；未启用 -RequireCleanWorkspace，继续执行发布前检查。'
    }
}

$projectContent = Get-Content -Raw -LiteralPath $projectFile
$versionMatch = [regex]::Match($projectContent, '<Version>(?<version>[^<]+)</Version>')
if (-not $versionMatch.Success) {
    throw "无法从 $projectFile 读取 Version。"
}

$projectVersion = $versionMatch.Groups['version'].Value
Assert-ReleaseVersionMatchesProject $Version $projectVersion

foreach ($tag in @($Version)) {
    $localTag = Get-CommandOutput 'git' @('tag', '--list', $tag) $projectRoot
    if (-not [string]::IsNullOrWhiteSpace($localTag)) {
        throw "本地已经存在版本标签：$tag"
    }

    if (-not $SkipRemoteTagCheck) {
        $remoteTag = Get-CommandOutput 'git' @('ls-remote', '--tags', 'origin', "refs/tags/$tag") $projectRoot
        if (-not [string]::IsNullOrWhiteSpace($remoteTag)) {
            throw "远程已经存在版本标签：$tag"
        }
    }
}

if ($SkipRemoteTagCheck) {
    Write-Host '已跳过远程 tag 检查（离线发布前检查模式）。'
}

Write-Host '开始执行发布前 Release 构建...'
if ($Restore) {
    & $buildScript -Configuration Release -Restore
}
else {
    & $buildScript -Configuration Release
}
if ($LASTEXITCODE -ne 0) {
    throw "Release 构建失败（退出码 $LASTEXITCODE）。"
}

if (-not (Test-Path -LiteralPath $pluginZip -PathType Leaf)) {
    throw "Release 编译完成，但没有找到插件包：$pluginZip"
}

Write-Host "发布前检查完成。未修改、提交或推送任何仓库。"
Write-Host "已确认插件包存在：$pluginZip"
Write-Host "下一步请将版本提交后创建 $Version tag，交由 .github/workflows/release.yml 发布。"
