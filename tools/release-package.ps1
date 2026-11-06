#Requires -Version 7.0
<#
.SYNOPSIS
    把 dist\ 的兩個 exe、SHA256SUMS.txt、LICENSE、THIRD-PARTY-NOTICES.md 放進一個資料夾。由 .github/workflows/release.yml 呼叫。

.DESCRIPTION
    SHA256SUMS.txt 只算兩個 exe，每行是「小寫雜湊、兩個空白、檔名」（sha256sum 的格式，README 的驗證指令也讀這個格式）。
    它和另外兩個檔案不能放進 dist\：publish.ps1 會檢查 dist\ 只能有兩個 exe。
    輸出資料夾要是不存在或空的，這支腳本不刪任何東西。寫完會用 README 的驗證邏輯重算一次。

.EXAMPLE
    pwsh -File tools/release-package.ps1 -OutputDirectory $env:RUNNER_TEMP\release-files
#>

[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidateNotNullOrEmpty()]
    [string]$OutputDirectory,
    [ValidateNotNullOrEmpty()]
    [string]$DistDirectory = (Join-Path $PSScriptRoot '..\dist'),
    [ValidateNotNullOrEmpty()]
    [string]$Root = (Join-Path $PSScriptRoot '..')
)

$ErrorActionPreference = 'Stop'
chcp 65001 > $null
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8

$exeNames = @(
    'IsItGreenYet.exe'
    'IsItGreenYet-cli.exe'
)
$extraFiles = @('LICENSE', 'THIRD-PARTY-NOTICES.md')
$checksumFile = 'SHA256SUMS.txt'
$checksumAlgorithm = 'SHA256'

$distFull = (Resolve-Path -LiteralPath $DistDirectory).Path
$rootFull = (Resolve-Path -LiteralPath $Root).Path
$outputFull = [System.IO.Path]::GetFullPath(
    $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($OutputDirectory))

$separator = [System.IO.Path]::DirectorySeparatorChar
$distTrimmed = $distFull.TrimEnd($separator)
$outputTrimmed = $outputFull.TrimEnd($separator)
if (($outputTrimmed -ieq $distTrimmed) -or
    $outputTrimmed.StartsWith($distTrimmed + $separator, [System.StringComparison]::OrdinalIgnoreCase)) {
    throw "輸出資料夾不能是 dist 或它裡面的資料夾，沒有動任何東西：$outputFull"
}

$expected = @($exeNames | Sort-Object)
$actual = @(Get-ChildItem -LiteralPath $distFull -Force | ForEach-Object { $_.Name } | Sort-Object)
if (($actual -join '|') -cne ($expected -join '|')) {
    throw "dist 內容不符預期：應該只有 $($expected -join '、')，實際是 $(if ($actual.Count) { $actual -join '、' } else { '空的' })。"
}

foreach ($name in $extraFiles) {
    if (-not (Test-Path -LiteralPath (Join-Path $rootFull $name) -PathType Leaf)) {
        throw "找不到要附加的檔案：$name。"
    }
}

if (Test-Path -LiteralPath $outputFull) {
    if (Test-Path -LiteralPath $outputFull -PathType Leaf) {
        throw "輸出路徑是檔案，不是資料夾：$outputFull"
    }

    if (@(Get-ChildItem -LiteralPath $outputFull -Force).Count -gt 0) {
        throw "輸出資料夾不是空的，沒有動任何東西：$outputFull"
    }
}

New-Item -ItemType Directory -Path $outputFull -Force | Out-Null

# 雜湊從 dist\ 算，下面再對複製過去的檔案驗一次，複製出錯就會發現。
$lines = foreach ($name in ($exeNames | Sort-Object)) {
    $source = Join-Path $distFull $name
    $hash = (Get-FileHash -LiteralPath $source -Algorithm $checksumAlgorithm).Hash.ToLowerInvariant()
    Copy-Item -LiteralPath $source -Destination (Join-Path $outputFull $name)
    "$hash  $name"
}

$checksumPath = Join-Path $outputFull $checksumFile
[System.IO.File]::WriteAllText($checksumPath, (($lines -join "`n") + "`n"), [System.Text.UTF8Encoding]::new($false))

foreach ($name in $extraFiles) {
    Copy-Item -LiteralPath (Join-Path $rootFull $name) -Destination (Join-Path $outputFull $name)
}

$verified = 0
foreach ($line in Get-Content -LiteralPath $checksumPath -Encoding utf8) {
    $hash, $name = $line -split '\s+', 2
    $copy = Join-Path $outputFull $name
    if (-not (Test-Path -LiteralPath $copy -PathType Leaf) -or
        (Get-FileHash -LiteralPath $copy -Algorithm $checksumAlgorithm).Hash -ne $hash) {
        throw "$checksumFile 驗證失敗：$name。"
    }

    $verified++
}

if ($verified -ne $exeNames.Count) {
    throw "$checksumFile 應該有 $($exeNames.Count) 行，實際驗證了 $verified 行。"
}

Write-Host "✓ 完成：$outputFull"
Get-ChildItem -LiteralPath $outputFull -File | Sort-Object Name | Format-Table -AutoSize -Property Name, @{ N = 'Bytes'; E = { $_.Length } } | Out-Host
Get-Content -LiteralPath $checksumPath -Encoding utf8 | Out-Host
