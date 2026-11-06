#Requires -Version 7.0
<#
.SYNOPSIS
    建置 App 與命令列兩個可直接執行的發行檔，輸出到 dist\。

.DESCRIPTION
    會先跑完整單元測試，測試沒過就不產生輸出，避免把壞掉的版本交出去；接著重新產生圖示，
    最後一次輸出兩個相依框架（framework-dependent）的單一執行檔（第一版不發自封式，見 docs/spec-deviations.md）：

      IsItGreenYet.exe       App，目標電腦要已安裝 .NET 10 Desktop Runtime
      IsItGreenYet-cli.exe   命令列，要有 .NET 10 Runtime（裝了 .NET 10 Desktop Runtime 就有）

    兩者都設 PublishSingleFile 與 IncludeNativeLibrariesForSelfExtract：需要的原生 DLL（SQLite 的
    e_sqlite3.dll）會包進 exe，只複製 exe 就能執行
    （spec §18、第二階段 spec §10.7）。每一個先發行到各自的暫存資料夾，確認只產生一個 exe 後才複製進 dist\；
    暫存資料夾用完就刪掉。

    -OutputDirectory 指定的資料夾會整個刪掉重建（不經過資源回收桶），所以它不能是磁碟根目錄、repo 根目錄或 repo 的上層資料夾；
    路徑是檔案，或資料夾裡有上面兩個 exe 以外的項目（包括隱藏檔與子資料夾，舊版留下的 -selfcontained exe 也算）時，
    跑測試之前就中止，什麼都不刪。

.EXAMPLE
    pwsh -File tools/publish.ps1
#>

[CmdletBinding()]
param(
    [switch]$SkipTests,
    [ValidateNotNullOrEmpty()]
    [string]$OutputDirectory = (Join-Path $PSScriptRoot '..\dist')
)

$ErrorActionPreference = 'Stop'
chcp 65001 > $null
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8

$appName = 'IsItGreenYet'
$testProject = 'tests/IsItGreenYet.Core.Tests'
$runtimeIdentifier = 'win-x64'

# 兩個發行檔：暫存子資料夾名稱、專案、dotnet publish 產生的 exe 檔名（AssemblyName，也是在 dist\ 裡的檔名）。
$variants = @(
    [pscustomobject]@{ Name = 'app-framework-dependent'; Project = 'src/IsItGreenYet.App'; FileName = 'IsItGreenYet.exe' }
    [pscustomobject]@{ Name = 'cli-framework-dependent'; Project = 'src/IsItGreenYet.Cli'; FileName = 'IsItGreenYet-cli.exe' }
)

# dist\ 不進版控；單一檔案超過 50 MiB 時另外提醒，避免被誤放進 git（GitHub 拒收超過 100 MiB 的單一檔案）。
$largeFileWarningBytes = 50MB

function Invoke-Dotnet {
    param(
        [string]$Step,
        [string[]]$Arguments
    )

    & dotnet @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "${Step}失敗（dotnet 結束碼 $LASTEXITCODE），已中止發行。原因請看上方 dotnet 的輸出。"
    }
}

$repoRoot = Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '..')

# 相對路徑以呼叫端的 PowerShell 目前位置為準（在 Push-Location 之前解析），再用 GetFullPath 收掉其中的 ..。
$outputFull = [System.IO.Path]::GetFullPath(
    $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($OutputDirectory))

# 清空輸出資料夾是整個刪掉重建，不能指到磁碟根目錄、repo 根目錄或 repo 的上層資料夾。
$separator = [System.IO.Path]::DirectorySeparatorChar
$outputTrimmed = $outputFull.TrimEnd($separator)
$repoTrimmed = $repoRoot.Path.TrimEnd($separator)
$isPathRoot = $outputTrimmed -ieq [System.IO.Path]::GetPathRoot($outputFull).TrimEnd($separator)
$containsRepo = ($outputTrimmed -ieq $repoTrimmed) -or
    $repoTrimmed.StartsWith($outputTrimmed + $separator, [System.StringComparison]::OrdinalIgnoreCase)
if ($isPathRoot -or $containsRepo) {
    throw "輸出資料夾不能是磁碟根目錄、repo 根目錄或 repo 的上層資料夾：$outputFull"
}

# 上面只比對字串，擋不住其他資料夾、junction、\\localhost\c$\ 與 \\?\ 別名，所以再看實際內容：
# 只有發行檔的資料夾才能刪。和發行檔同名的資料夾也不算，裡面可能有別的東西。
if (Test-Path -LiteralPath $outputFull -PathType Leaf) {
    throw "輸出路徑是檔案，不是資料夾，沒有刪任何東西：$outputFull"
}

if (Test-Path -LiteralPath $outputFull -PathType Container) {
    $publishedNames = @($variants | ForEach-Object { $_.FileName })
    $unexpected = @(Get-ChildItem -LiteralPath $outputFull -Force |
        Where-Object { $_.PSIsContainer -or $publishedNames -notcontains $_.Name })
    if ($unexpected.Count -gt 0) {
        throw "輸出資料夾會整個刪掉重建，但裡面有發行檔以外的項目「$($unexpected[0].Name)」，沒有刪任何東西：$outputFull"
    }
}

$stagingRoot = Join-Path ([System.IO.Path]::GetTempPath()) "$appName-publish-$([guid]::NewGuid().ToString('N'))"
$completed = $false

Push-Location $repoRoot
try {
    if (-not $SkipTests) {
        Write-Host '→ 執行單元測試' -ForegroundColor Cyan
        Invoke-Dotnet -Step '單元測試' -Arguments @('test', $testProject, '--nologo')
    }
    else {
        Write-Warning '已略過單元測試'
    }

    Write-Host '→ 重新產生應用程式圖示' -ForegroundColor Cyan
    try {
        & (Join-Path $PSScriptRoot 'generate-icon.ps1')
    }
    catch {
        throw "產生圖示失敗，已中止發行：$($_.Exception.Message)"
    }

    # Core 和一般的 Release 建置共用輸出資料夾（Core 的 csproj 設了 IsRidAgnostic），而只有 DebugType 不同時不會重新編譯：
    # 先建過 Release 的話，exe 裡的 Core.dll 會帶本機的 pdb 路徑，所以先刪掉舊的輸出。放在清空輸出資料夾之前，刪不掉時舊的發行檔還在。
    try {
        foreach ($staleCoreOutput in 'src/IsItGreenYet.Core/bin/Release', 'src/IsItGreenYet.Core/obj/Release') {
            if (Test-Path -LiteralPath $staleCoreOutput) {
                Remove-Item -LiteralPath $staleCoreOutput -Recurse -Force
            }
        }
    }
    catch {
        throw "無法刪除 Core 舊的 Release 輸出（裡面的檔案可能正被使用），已中止發行：$($_.Exception.Message)"
    }

    Write-Host "→ 清空 $outputFull" -ForegroundColor Cyan
    try {
        if (Test-Path -LiteralPath $outputFull) {
            Remove-Item -LiteralPath $outputFull -Recurse -Force
        }

        New-Item -ItemType Directory -Path $outputFull -Force | Out-Null
    }
    catch {
        throw "無法清空輸出資料夾 $outputFull（裡面的 exe 可能正在執行）：$($_.Exception.Message)"
    }

    foreach ($variant in $variants) {
        $staging = Join-Path $stagingRoot $variant.Name
        Write-Host "→ 發行 $($variant.Name)" -ForegroundColor Cyan
        Invoke-Dotnet -Step "發行 $($variant.Name) " -Arguments @(
            'publish', $variant.Project,
            '-c', 'Release',
            '-r', $runtimeIdentifier,
            '--self-contained', 'false',
            '-o', $staging,
            '--nologo',
            '-p:PublishSingleFile=true',
            # 原生 DLL（e_sqlite3.dll）不夾帶進單一執行檔的話，只複製 exe 會缺檔
            '-p:IncludeNativeLibrariesForSelfExtract=true',
            '-p:DebugType=none'
        )

        # 只複製 exe：旁邊還有其他檔案，代表有東西沒包進單一執行檔，單獨複製 exe 可能無法執行。
        $published = @(Get-ChildItem -LiteralPath $staging -Force -Recurse -File)
        $extraFiles = @($published | Where-Object { $_.Name -ne $variant.FileName })
        if ($published.Count -eq 0 -or $extraFiles.Count -gt 0) {
            $listing = ($published | ForEach-Object { $_.FullName.Substring($staging.Length + 1) }) -join ', '
            throw "$($variant.Name) 的發行輸出不是單一個 $($variant.FileName)（實際：$(if ($listing) { $listing } else { '沒有檔案' })）。"
        }

        Copy-Item -LiteralPath $published[0].FullName -Destination (Join-Path $outputFull $variant.FileName)
    }

    # dist\ 只能有這兩個 exe。
    $expectedNames = @($variants | ForEach-Object { $_.FileName } | Sort-Object)
    $actualFiles = @(Get-ChildItem -LiteralPath $outputFull -Force | Sort-Object Name)
    $actualNames = @($actualFiles | ForEach-Object { $_.Name })
    if (($actualNames -join '|') -cne ($expectedNames -join '|')) {
        throw "dist 內容不符預期：應該只有 $($expectedNames -join '、')，實際是 $(if ($actualNames.Count) { $actualNames -join '、' } else { '空的' })。"
    }

    Write-Host ''
    Write-Host "✓ 完成：$outputFull" -ForegroundColor Green
    $actualFiles | Format-Table -AutoSize -Property Name,
        @{ N = 'Bytes'; E = { $_.Length } },
        @{ N = 'MiB'; E = { [math]::Round($_.Length / 1MB, 1) } } | Out-Host
    Write-Host "  $appName.exe 需要目標電腦已安裝 .NET 10 Desktop Runtime；$appName-cli.exe 需要 .NET 10 Runtime（裝了 Desktop Runtime 就有）。"
    foreach ($file in $actualFiles | Where-Object { $_.Length -gt $largeFileWarningBytes }) {
        Write-Warning "$($file.Name) 超過 $($largeFileWarningBytes / 1MB) MiB（$([math]::Round($file.Length / 1MB, 1)) MiB）：不要放進 git；GitHub 單檔上限 100 MiB。"
    }

    $completed = $true
}
finally {
    Pop-Location

    if (Test-Path -LiteralPath $stagingRoot) {
        try {
            Remove-Item -LiteralPath $stagingRoot -Recurse -Force
        }
        catch {
            $message = "暫存資料夾刪不掉，請手動刪除 ${stagingRoot}：$($_.Exception.Message)"
            # 前面已經失敗時只提醒，不要蓋掉真正的失敗原因。
            if ($completed) {
                throw $message
            }

            Write-Warning $message
        }
    }
}
