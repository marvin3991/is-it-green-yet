#Requires -Version 7.0
<#
.SYNOPSIS
    確認鎖定模式真的開著，而且還原或發行沒有產生、改寫 NuGet lock 檔。CI 與發版會跑。

.DESCRIPTION
    lock 檔不見時，鎖定模式的還原不會失敗，而是重新產生一份，所以要看 git 的狀態才知道。
    RestoreLockedMode 由 CI 環境變數決定（Directory.Build.props）；環境變數不見的話，lock 檔和專案不符時還原也不會失敗，所以也要確認。

    都沒問題時結束碼 0；否則印出原因，結束碼 1。

.PARAMETER Root
    repo 根目錄，預設是這支腳本的上一層。
#>
[CmdletBinding()]
param(
    [string]$Root = (Split-Path -Parent $PSScriptRoot)
)

chcp 65001 > $null
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8
$ErrorActionPreference = 'Stop'

$Root = (Resolve-Path -LiteralPath $Root).Path
$probeProject = Join-Path $Root 'src/IsItGreenYet.Core/IsItGreenYet.Core.csproj'

$lockedMode = & dotnet msbuild $probeProject -nologo -getProperty:RestoreLockedMode
if ($LASTEXITCODE -ne 0) {
    throw "讀不到 RestoreLockedMode（dotnet 結束碼 $LASTEXITCODE）：$lockedMode"
}

if ("$lockedMode".Trim() -ne 'true') {
    Write-Host "鎖定模式沒有開（RestoreLockedMode=[$lockedMode]）。CI 環境變數要是 true，見 Directory.Build.props。"
    exit 1
}

# --ignored：lock 檔被拿掉又被 .gitignore 忽略時，還原重新產生的檔案才看得到。
$changed = @(& git -C $Root status --porcelain --ignored -- '*packages.lock.json')
if ($LASTEXITCODE -ne 0) {
    throw "git status 失敗（結束碼 $LASTEXITCODE）。"
}

if ($changed.Count -gt 0) {
    Write-Host '還原或發行產生、改寫了 lock 檔。缺的話要 commit，版本不該變的話要查原因：'
    $changed | ForEach-Object { Write-Host "  $_" }
    exit 1
}

Write-Host '鎖定模式開著，lock 檔沒有變動。'
