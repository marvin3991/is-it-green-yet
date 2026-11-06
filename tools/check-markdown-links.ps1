#Requires -Version 7.0
<#
.SYNOPSIS
    檢查 repo 裡所有 Markdown 的相對連結與錨點都存在。

.DESCRIPTION
    掃 repo 裡 git 追蹤與尚未追蹤（但沒被忽略）的 .md 檔。程式碼區塊與行內程式碼裡的內容不算連結。
    http、https、mailto 開頭的網址（徽章、github.com）不檢查。
    錨點照 GitHub 的規則從標題算：轉小寫、去掉標點（保留字母、數字、連字號、底線）、空白換成連字號，同名標題依序加 -1、-2。

    全部存在時結束碼 0；有不存在的連結時列出每一筆（檔案:行: 連結），結束碼 1。

.PARAMETER Root
    要檢查的資料夾，預設是 repo 根目錄。
#>
[CmdletBinding()]
param(
    [string]$Root = (Split-Path -Parent $PSScriptRoot)
)

chcp 65001 > $null
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8
$ErrorActionPreference = 'Stop'

$Root = (Resolve-Path -LiteralPath $Root).Path

function Get-MarkdownFiles {
    $listed = git -C $Root ls-files --cached --others --exclude-standard -- '*.md' 2>$null
    if ($LASTEXITCODE -eq 0) {
        return @($listed | ForEach-Object { Join-Path $Root $_ } | Where-Object { Test-Path -LiteralPath $_ -PathType Leaf })
    }
    # 不是 git repo（例如測試用的暫存資料夾）：直接掃整個資料夾
    return @(Get-ChildItem -LiteralPath $Root -Recurse -File -Filter '*.md' | ForEach-Object FullName)
}

# 把一行裡的行內程式碼換成空白，保留長度；連結與標題就不會誤判
function Remove-InlineCode([string]$line) {
    return [regex]::Replace($line, '(?<ticks>`+)(?:(?!\k<ticks>).)+?\k<ticks>', { param($match) ' ' * $match.Length })
}

function Get-Slug([string]$heading) {
    $text = $heading.Trim()
    # 連結只留文字；行內程式碼的反引號拿掉、內容保留
    $text = [regex]::Replace($text, '!?\[([^\]]*)\]\([^)]*\)', '$1')
    $text = $text.Replace('`', '').ToLowerInvariant()
    $text = [regex]::Replace($text, '[^\p{L}\p{M}\p{N}\p{Pc}\- ]', '')
    return $text.Replace(' ', '-')
}

# 逐行讀檔，回傳 (Number, Text)，略過圍欄程式碼區塊
function Get-ProseLines([string]$path) {
    $fence = $null
    $number = 0
    foreach ($line in [System.IO.File]::ReadAllLines($path, [System.Text.Encoding]::UTF8)) {
        $number++
        $trimmed = $line.TrimStart()
        if ($null -eq $fence) {
            $match = [regex]::Match($trimmed, '^(`{3,}|~{3,})')
            if ($match.Success) { $fence = $match.Groups[1].Value; continue }
            [pscustomobject]@{ Number = $number; Text = $line }
        }
        elseif ($trimmed.StartsWith($fence) -and $trimmed.Trim('`', '~', ' ').Length -eq 0) {
            $fence = $null
        }
    }
}

$anchorCache = @{}
function Get-Anchors([string]$path) {
    if ($anchorCache.ContainsKey($path)) { return $anchorCache[$path] }
    $anchors = [System.Collections.Generic.HashSet[string]]::new()
    $seen = @{}
    foreach ($line in Get-ProseLines $path) {
        $match = [regex]::Match($line.Text, '^ {0,3}#{1,6}\s+(.+?)\s*#*\s*$')
        if (-not $match.Success) { continue }
        $slug = Get-Slug $match.Groups[1].Value
        if ($seen.ContainsKey($slug)) {
            [void]$anchors.Add("$slug-$($seen[$slug])")
            $seen[$slug]++
        }
        else {
            [void]$anchors.Add($slug)
            $seen[$slug] = 1
        }
    }
    $anchorCache[$path] = $anchors
    return $anchors
}

$files = Get-MarkdownFiles
$links = 0
$anchored = 0
$broken = [System.Collections.Generic.List[string]]::new()

foreach ($file in $files) {
    $relativeFile = [System.IO.Path]::GetRelativePath($Root, $file).Replace('\', '/')
    foreach ($line in Get-ProseLines $file) {
        $prose = Remove-InlineCode $line.Text
        # 行內連結 [文字](目標 "標題")、圖片 ![文字](目標)、參考式定義 [名稱]: 目標
        $targets = [System.Collections.Generic.List[string]]::new()
        foreach ($match in [regex]::Matches($prose, '\]\(\s*<?([^)\s>]+)>?(?:\s+"[^"]*")?\s*\)')) { $targets.Add($match.Groups[1].Value) }
        foreach ($match in [regex]::Matches($prose, '^\s{0,3}\[[^\]]+\]:\s+<?([^\s>]+)>?')) { $targets.Add($match.Groups[1].Value) }

        foreach ($target in $targets) {
            if ($target -match '^(https?:|mailto:)') { continue }
            $links++
            $hashIndex = $target.IndexOf('#')
            $pathPart = if ($hashIndex -ge 0) { $target.Substring(0, $hashIndex) } else { $target }
            $anchorPart = if ($hashIndex -ge 0) { $target.Substring($hashIndex + 1) } else { '' }
            $pathPart = [uri]::UnescapeDataString($pathPart)
            $anchorPart = [uri]::UnescapeDataString($anchorPart)

            $resolved = if ($pathPart) { [System.IO.Path]::GetFullPath((Join-Path (Split-Path -Parent $file) $pathPart)) } else { $file }
            if (-not (Test-Path -LiteralPath $resolved)) {
                $broken.Add("${relativeFile}:$($line.Number): $target（檔案不存在）")
                continue
            }
            if ($anchorPart) {
                $anchored++
                if (-not (Test-Path -LiteralPath $resolved -PathType Leaf) -or -not $resolved.EndsWith('.md', [StringComparison]::OrdinalIgnoreCase)) {
                    $broken.Add("${relativeFile}:$($line.Number): $target（錨點指向的不是 Markdown 檔）")
                }
                elseif (-not (Get-Anchors $resolved).Contains($anchorPart.ToLowerInvariant())) {
                    $broken.Add("${relativeFile}:$($line.Number): $target（錨點不存在）")
                }
            }
        }
    }
}

Write-Output "檢查了 $($files.Count) 個 Markdown 檔、$links 個相對連結（其中 $anchored 個帶錨點）。"
if ($broken.Count -gt 0) {
    Write-Output "不存在的連結 $($broken.Count) 筆："
    $broken | ForEach-Object { Write-Output "  $_" }
    exit 1
}
Write-Output '全部存在。'
exit 0
