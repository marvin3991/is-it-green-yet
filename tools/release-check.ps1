#Requires -Version 7.0
<#
.SYNOPSIS
    發版前檢查：版本號、tag 在不在 main 上、第三方聲明還有沒有待辦。由 .github/workflows/release.yml 呼叫。

.DESCRIPTION
    正式發版（有 -Tag）：tag 去掉 v 之後要等於 App 與命令列兩個 csproj 的 <Version>，tag 指向的 commit 要在 main 上，
    THIRD-PARTY-NOTICES.md 不能有待辦標記。任一項不符就結束碼 1，所有問題一次列出來。
    dry-run（沒有 -Tag）：版本讀不到或是空的、兩個 csproj 彼此不一致、找不到聲明檔才算失敗；版本不是 SemVer、有待辦標記都只顯示警告。

    同名 release 已存在的檢查不在這裡：draft 要有 contents: write 才看得到，所以放在建立 release 的 job。

.EXAMPLE
    pwsh -File tools/release-check.ps1

.EXAMPLE
    pwsh -File tools/release-check.ps1 -Tag v0.3.0
#>

[CmdletBinding()]
param(
    [string]$Tag,
    [ValidateNotNullOrEmpty()]
    [string]$Root = (Join-Path $PSScriptRoot '..'),
    [ValidateNotNullOrEmpty()]
    [string]$MainRef = 'refs/remotes/origin/main'
)

$ErrorActionPreference = 'Stop'
chcp 65001 > $null
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8

$projects = @(
    'src/IsItGreenYet.App/IsItGreenYet.App.csproj'
    'src/IsItGreenYet.Cli/IsItGreenYet.Cli.csproj'
)
$noticesFile = 'THIRD-PARTY-NOTICES.md'
$noticesMarker = 'RELEASE-BLOCKER'
$tagPrefix = 'v'

# https://semver.org/ 的規則，不含 build metadata（SDK 會自己接上 +<commit>）。App 用同樣的規則解析 release 的 tag，解析不了就不會通知新版本。
$identifier = '(?:0|[1-9][0-9]*|[0-9]*[a-zA-Z-][0-9a-zA-Z-]*)'
$semVerPattern = "\A(?:0|[1-9][0-9]*)\.(?:0|[1-9][0-9]*)\.(?:0|[1-9][0-9]*)(?:-$identifier(?:\.$identifier)*)?\z"

$isRelease = -not [string]::IsNullOrEmpty($Tag)
$rootFull = (Resolve-Path -LiteralPath $Root).Path
$script:failures = 0

# GitHub Actions 的 annotation：% 與換行要跳脫，內容才不會被當成指令的一部分。
function Format-Annotation {
    param([string]$Text)

    return $Text -replace '%', '%25' -replace "`r", '%0D' -replace "`n", '%0A'
}

function Write-Failure {
    param([string]$Message)

    $script:failures++
    Write-Host "::error::$(Format-Annotation $Message)"
}

function Write-DryRunWarning {
    param([string]$Message)

    Write-Host "::warning::$(Format-Annotation $Message)"
}

# 正式發版時是失敗，dry-run 只警告。
function Write-Problem {
    param([string]$Message)

    if ($isRelease) {
        Write-Failure $Message
    }
    else {
        Write-DryRunWarning $Message
    }
}

function Get-ProjectVersion {
    param([string]$RelativePath)

    $path = Join-Path $rootFull $RelativePath
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
        Write-Failure "找不到 $RelativePath。"
        return $null
    }

    try {
        [xml]$project = Get-Content -LiteralPath $path -Raw -Encoding utf8
    }
    catch {
        Write-Failure "$RelativePath 不是合法的 XML：$($_.Exception.Message)"
        return $null
    }

    $nodes = @($project.SelectNodes('/Project/PropertyGroup/Version'))
    if ($nodes.Count -ne 1) {
        Write-Failure "$RelativePath 應該剛好有一個 <Version>，實際有 $($nodes.Count) 個。"
        return $null
    }

    # 不去空白：MSBuild 會把空白原樣編進 exe，App 解析不了。留給後面的比對與 SemVer 檢查擋下。
    $value = $nodes[0].InnerText
    if ([string]::IsNullOrWhiteSpace($value)) {
        Write-Failure "$RelativePath 的 <Version> 是空的。"
        return $null
    }

    return $value
}

$versions = @($projects | ForEach-Object { Get-ProjectVersion $_ })
$found = @($versions | Where-Object { $_ })
$distinct = @($found | Sort-Object -Unique -CaseSensitive)
$version = $null

if ($found.Count -eq $projects.Count) {
    if ($distinct.Count -ne 1) {
        Write-Failure "兩個 csproj 的 <Version> 不一致：$($projects[0]) 是 $($versions[0])，$($projects[1]) 是 $($versions[1])。"
    }
    else {
        $version = $distinct[0]
        Write-Host "兩個 csproj 的版本都是 $version。"
        if ($version -cnotmatch $semVerPattern) {
            Write-Problem "<Version> 不是 SemVer：$version。"
        }
    }
}

if ($isRelease) {
    if ($version -and -not ($Tag -ceq "$tagPrefix$version")) {
        Write-Failure "tag $Tag 和 csproj 的版本 $version 不符，應該是 $tagPrefix$version。"
    }

    $commit = & git -C $rootFull rev-parse --verify --quiet "refs/tags/$Tag^{commit}"
    if ($LASTEXITCODE -ne 0) {
        Write-Failure "找不到 tag $Tag。"
    }
    else {
        $null = & git -C $rootFull rev-parse --verify --quiet "$MainRef^{commit}"
        if ($LASTEXITCODE -ne 0) {
            Write-Failure "找不到 $MainRef（workflow 的 checkout 要 fetch-depth: 0）。"
        }
        else {
            & git -C $rootFull merge-base --is-ancestor $commit $MainRef
            switch ($LASTEXITCODE) {
                0 { Write-Host "tag $Tag 指向的 commit $commit 在 $MainRef 上。" }
                1 { Write-Failure "tag $Tag 指向的 commit $commit 不在 $MainRef 上。" }
                default { Write-Failure "git merge-base 失敗（結束碼 $LASTEXITCODE）。" }
            }
        }
    }
}

$noticesPath = Join-Path $rootFull $noticesFile
if (-not (Test-Path -LiteralPath $noticesPath -PathType Leaf)) {
    Write-Failure "找不到 $noticesFile。"
}
else {
    $markerLines = @(Select-String -LiteralPath $noticesPath -SimpleMatch -CaseSensitive -Pattern $noticesMarker -Encoding utf8)
    if ($markerLines.Count -gt 0) {
        Write-Problem "$noticesFile 第 $($markerLines[0].LineNumber) 行有 $noticesMarker：第三方聲明還有待辦。"
    }
}

if ($script:failures -gt 0) {
    Write-Host "發版前檢查失敗：$($script:failures) 個問題。"
    exit 1
}

Write-Host ($(if ($isRelease) { "發版前檢查通過：$Tag。" } else { 'dry-run 的檢查通過。' }))
exit 0
