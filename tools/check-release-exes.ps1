#Requires -Version 7.0
<#
.SYNOPSIS
    唯讀檢查發行的兩個 exe，確認 THIRD-PARTY-NOTICES.md 依據的事實還成立。release.yml 與 ci.yml 在 publish.ps1 之後跑。

.DESCRIPTION
    THIRD-PARTY-NOTICES.md 待確認第 1、4、5 項的判斷建立在下面這些事實上，任何一項不符就結束碼 1，要先重查聲明再發行：

    - bundle 清單剛好是 $expectedBundles 列的 9 個檔。多了檔案要先查授權再加進清單。
      license-information-windows.md 列名的檔案（coreclr.dll、單一檔案的 runtime 等）出現時另外標明。
    - e_sqlite3.dll 的 SHA-256 等於聲明檔第 5 項記的值。換套件版本時兩邊一起改。
    - host 區段（檔案開頭到 bundle 第一個檔）沒有 WerRegister、Watson、coreclr.dll、vcruntime140（只有這個字不分大小寫），ASCII 與 UTF-16 都看。
      不能掃整個 exe：內嵌的聲明檔本身就有這些字。
    - 檔案內容的 LegalCopyright 等於 csproj 的 <Copyright>，ProductVersion 以 csproj 的 <Version> 加上 + 開頭。

    另外印出 SDK 版本、apphost 的來源路徑與 host 區段裡的版本字串，給發版時對照，不判斷。

.PARAMETER Directory
    發行檔資料夾，預設 dist\。

.PARAMETER ExpectedSqliteSha256
    e_sqlite3.dll 的 SHA-256（小寫十六進位）。預設是聲明檔第 5 項的值。

.PARAMETER NoEnvironmentInfo
    不印 SDK 與 apphost 的來源（要跑 dotnet msbuild，比較慢）。單元測試用。

.EXAMPLE
    pwsh -File tools/check-release-exes.ps1 -Directory dist
#>
[CmdletBinding()]
param(
    [ValidateNotNullOrEmpty()]
    [string]$Directory = (Join-Path $PSScriptRoot '..\dist'),
    # SHA-256 是 64 個十六進位字元。ValidatePattern 不分大小寫，比對時再轉小寫。
    [ValidatePattern('\A[0-9a-f]{64}\z')]
    [string]$ExpectedSqliteSha256 = 'b7385d722c83fb52142a00477a726723745916d22a555711ee89834c1111fb2e',
    [ValidateNotNullOrEmpty()]
    [string]$Root = (Join-Path $PSScriptRoot '..'),
    [switch]$NoEnvironmentInfo
)

$ErrorActionPreference = 'Stop'
chcp 65001 > $null
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8

$rootFull = (Resolve-Path -LiteralPath $Root).Path

# exe、它的 csproj、bundle 裡應該剛好有的檔案。
$expectedBundles = @(
    [pscustomobject]@{
        Exe     = 'IsItGreenYet.exe'
        Project = 'src/IsItGreenYet.App/IsItGreenYet.App.csproj'
        Files   = @('e_sqlite3.dll', 'IsItGreenYet.Core.dll', 'IsItGreenYet.deps.json', 'IsItGreenYet.dll', 'IsItGreenYet.runtimeconfig.json',
            'Microsoft.Data.Sqlite.dll', 'SQLitePCLRaw.batteries_v2.dll', 'SQLitePCLRaw.core.dll', 'SQLitePCLRaw.provider.e_sqlite3.dll')
    }
    [pscustomobject]@{
        Exe     = 'IsItGreenYet-cli.exe'
        Project = 'src/IsItGreenYet.Cli/IsItGreenYet.Cli.csproj'
        Files   = @('e_sqlite3.dll', 'IsItGreenYet-cli.deps.json', 'IsItGreenYet-cli.dll', 'IsItGreenYet-cli.runtimeconfig.json', 'IsItGreenYet.Core.dll',
            'Microsoft.Data.Sqlite.dll', 'SQLitePCLRaw.batteries_v2.dll', 'SQLitePCLRaw.core.dll', 'SQLitePCLRaw.provider.e_sqlite3.dll')
    }
)

# license-information-windows.md 列名的檔案（.NET Library License 與 Windows SDK License）。
$listedInLicenseInformation = @('coreclr.dll', 'singlefilehost*', 'Microsoft.DiaSymReader.Native.*.dll', 'PresentationNative_cor3.dll',
    'vcruntime140_cor3.dll', 'wpfgfx_cor3.dll', 'D3DCompiler_47_cor3.dll')

# host 區段不能有的字：單一檔案 runtime 與錯誤回報（Windows Error Reporting）的痕跡、動態連結的 VC runtime。
# 匯入表的 DLL 名稱是大寫（VCRUNTIME140.dll），所以 vcruntime140 不分大小寫；
# 其他字要分：apphost 從 UCRT 匯入 _invoke_watson，不分大小寫時 Watson 會誤判。
$forbiddenHostWords = @(
    [pscustomobject]@{ Word = 'WerRegister'; Comparison = [System.StringComparison]::Ordinal }
    [pscustomobject]@{ Word = 'Watson'; Comparison = [System.StringComparison]::Ordinal }
    [pscustomobject]@{ Word = 'coreclr.dll'; Comparison = [System.StringComparison]::Ordinal }
    [pscustomobject]@{ Word = 'vcruntime140'; Comparison = [System.StringComparison]::OrdinalIgnoreCase }
)

$sqliteFile = 'e_sqlite3.dll'

# .NET 單一檔案 bundle 的標記（dotnet/runtime 的 src/installer/managed/Microsoft.NET.HostModel/Bundle/HostWriter.cs），前面 8 bytes 是 header 的位置。
[byte[]]$bundleSignature = 0x8b, 0x12, 0x02, 0xb9, 0x6a, 0x61, 0x20, 0x38, 0x72, 0x7b, 0x93, 0x02, 0x14, 0xd7, 0xa0, 0x32,
0x13, 0xf5, 0xb9, 0xe6, 0xef, 0xae, 0x33, 0x18, 0xee, 0x3b, 0x2d, 0xce, 0x24, 0xb3, 0x6a, 0xae
$headerOffsetBytes = 8
# bundle header 從第 2 版起在 bundle ID 之後多 5 個 8 bytes 欄位（deps.json 與 runtimeconfig.json 的位置大小、旗標）；第 6 版起每個檔多記壓縮後大小。
$headerExtraFieldsSince = 2
$headerExtraFieldsBytes = 40
$compressedSizeSince = 6

$script:failures = 0

# GitHub Actions 的 annotation：% 與換行要跳脫。
function Format-Annotation {
    param([string]$Text)

    return $Text -replace '%', '%25' -replace "`r", '%0D' -replace "`n", '%0A'
}

function Write-Failure {
    param([string]$Message)

    $script:failures++
    Write-Host "::error::$(Format-Annotation $Message)"
}

# 位元組當 Latin-1 字串，一個 byte 一個字元，可以直接用字串搜尋。
function ConvertTo-ByteString {
    param([byte[]]$Bytes, [int]$Count = $Bytes.Length)

    return [System.Text.Encoding]::Latin1.GetString($Bytes, 0, $Count)
}

function Measure-Occurrence {
    param([string]$Haystack, [string]$Needle, [System.StringComparison]$Comparison = [System.StringComparison]::Ordinal)

    $count = 0
    $index = $Haystack.IndexOf($Needle, $Comparison)
    while ($index -ge 0) {
        $count++
        $index = $Haystack.IndexOf($Needle, $index + 1, $Comparison)
    }

    return $count
}

function Read-Bundle {
    param([byte[]]$Bytes)

    $text = ConvertTo-ByteString $Bytes
    $signatureAt = $text.IndexOf((ConvertTo-ByteString $bundleSignature), [System.StringComparison]::Ordinal)
    if ($signatureAt -lt $headerOffsetBytes) {
        throw '找不到單一檔案 bundle 的標記，不是 PublishSingleFile 的輸出。'
    }

    $headerOffset = [BitConverter]::ToInt64($Bytes, $signatureAt - $headerOffsetBytes)
    if ($headerOffset -le 0 -or $headerOffset -ge $Bytes.Length) {
        throw "bundle header 的位置不對：$headerOffset。"
    }

    $stream = [System.IO.MemoryStream]::new($Bytes)
    $reader = [System.IO.BinaryReader]::new($stream, [System.Text.Encoding]::UTF8)
    try {
        $stream.Position = $headerOffset
        $major = $reader.ReadUInt32()
        $null = $reader.ReadUInt32()
        $count = $reader.ReadInt32()
        $null = $reader.ReadString()
        if ($major -ge $headerExtraFieldsSince) {
            $stream.Position += $headerExtraFieldsBytes
        }

        $entries = for ($i = 0; $i -lt $count; $i++) {
            $offset = $reader.ReadInt64()
            $size = $reader.ReadInt64()
            $compressed = if ($major -ge $compressedSizeSince) { $reader.ReadInt64() } else { 0 }
            $null = $reader.ReadByte()
            $path = $reader.ReadString()
            $stored = if ($compressed -gt 0) { $compressed } else { $size }
            if ($offset -le 0 -or $offset + $stored -gt $Bytes.Length) {
                throw "bundle 裡 $path 的位置超出檔案範圍。"
            }

            [pscustomobject]@{ Path = $path; Offset = $offset; Size = $size; Compressed = $compressed }
        }
    }
    catch [System.IO.EndOfStreamException] {
        throw 'bundle 清單讀到一半就到檔案結尾，格式不對。'
    }
    finally {
        $reader.Dispose()
    }

    return [pscustomobject]@{ Entries = @($entries); HostEnd = [int](@($entries | Measure-Object -Property Offset -Minimum).Minimum) }
}

function Get-EntryBytes {
    param([byte[]]$Bytes, $Entry)

    $content = [byte[]]::new($Entry.Size)
    if ($Entry.Compressed -gt 0) {
        $source = [System.IO.MemoryStream]::new($Bytes, [int]$Entry.Offset, [int]$Entry.Compressed)
        $deflate = [System.IO.Compression.DeflateStream]::new($source, [System.IO.Compression.CompressionMode]::Decompress)
        try {
            $deflate.ReadExactly($content, 0, $content.Length)
        }
        finally {
            $deflate.Dispose()
        }
    }
    else {
        [Array]::Copy($Bytes, [long]$Entry.Offset, $content, 0, [long]$Entry.Size)
    }

    return , $content
}

function Get-ProjectProperty {
    param([string]$RelativePath, [string]$Name)

    [xml]$project = Get-Content -LiteralPath (Join-Path $rootFull $RelativePath) -Raw -Encoding utf8
    $nodes = @($project.SelectNodes("/Project/PropertyGroup/$Name"))
    if ($nodes.Count -ne 1) {
        throw "$RelativePath 應該剛好有一個 <$Name>，實際有 $($nodes.Count) 個。"
    }

    return $nodes[0].InnerText
}

function Test-Exe {
    param($Expected)

    $path = Join-Path $Directory $Expected.Exe
    Write-Host "=== $($Expected.Exe)"
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
        Write-Failure "找不到 $path。"
        return
    }

    $bytes = [System.IO.File]::ReadAllBytes($path)
    Write-Host "大小 $($bytes.Length) bytes，SHA-256 $([Convert]::ToHexString([System.Security.Cryptography.SHA256]::HashData($bytes)).ToLowerInvariant())"

    try {
        $bundle = Read-Bundle $bytes
    }
    catch {
        Write-Failure "$($Expected.Exe)：$($_.Exception.Message)"
        return
    }

    $names = @($bundle.Entries | ForEach-Object { $_.Path })
    Write-Host "bundle 有 $($names.Count) 個檔：$(($names | Sort-Object) -join '、')"

    foreach ($name in $names) {
        if ($Expected.Files -notcontains $name) {
            $listed = @($listedInLicenseInformation | Where-Object { $name -like $_ })
            if ($listed.Count -gt 0) {
                Write-Failure "$($Expected.Exe) 的 bundle 有 license-information-windows.md 列名的檔案 $name，聲明檔第 1 項的前提不成立。"
            }
            else {
                Write-Failure "$($Expected.Exe) 的 bundle 有清單以外的檔案 $name：先查它的授權，再加進 tools/check-release-exes.ps1 的清單。"
            }
        }
    }

    foreach ($name in $Expected.Files) {
        if ($names -notcontains $name) {
            Write-Failure "$($Expected.Exe) 的 bundle 缺 $name。"
        }
    }

    $sqlite = @($bundle.Entries | Where-Object { $_.Path -eq $sqliteFile })
    if ($sqlite.Count -eq 1) {
        $hash = [Convert]::ToHexString([System.Security.Cryptography.SHA256]::HashData((Get-EntryBytes $bytes $sqlite[0]))).ToLowerInvariant()
        Write-Host "$sqliteFile 的 SHA-256：$hash"
        if ($hash -cne $ExpectedSqliteSha256.ToLowerInvariant()) {
            Write-Failure "$($Expected.Exe) 裡的 $sqliteFile 和聲明檔記的不同（$ExpectedSqliteSha256）：換了套件版本的話，聲明檔第 5 項要重查，新的雜湊要同時寫進聲明檔與這個腳本的 -ExpectedSqliteSha256 預設值。"
        }
    }

    $hostText = ConvertTo-ByteString $bytes $bundle.HostEnd
    Write-Host "host 區段 $($bundle.HostEnd) bytes"
    foreach ($forbidden in $forbiddenHostWords) {
        $word = $forbidden.Word
        $ascii = Measure-Occurrence $hostText $word $forbidden.Comparison
        $utf16 = Measure-Occurrence $hostText (ConvertTo-ByteString ([System.Text.Encoding]::Unicode.GetBytes($word))) $forbidden.Comparison
        Write-Host "  $word：ASCII $ascii 次，UTF-16 $utf16 次"
        if ($ascii + $utf16 -gt 0) {
            Write-Failure "$($Expected.Exe) 的 host 區段有 $word，聲明檔第 4 項的前提（apphost，不是單一檔案的 runtime）要重查。"
        }
    }

    # 例如「10.0.14 @Commit: 0427b9a…」。版本後面可能有預覽版的後綴（-rc.2 之類），最多抓 40 個字；
    # commit 是 7～40 個十六進位字元（縮寫到完整的 SHA-1）。只印出來給人對照，不判斷。
    $hostVersion = [regex]::Match([System.Text.Encoding]::Unicode.GetString([System.Text.Encoding]::Latin1.GetBytes($hostText)), '\d+\.\d+\.\d+[^\x00]{0,40} @Commit: [0-9a-f]{7,40}')
    Write-Host "host 的版本字串：$(if ($hostVersion.Success) { $hostVersion.Value } else { '找不到' })"

    $info = [System.Diagnostics.FileVersionInfo]::GetVersionInfo($path)
    $copyright = Get-ProjectProperty $Expected.Project 'Copyright'
    $version = Get-ProjectProperty $Expected.Project 'Version'
    Write-Host "LegalCopyright：$($info.LegalCopyright)；ProductVersion：$($info.ProductVersion)"
    if ($info.LegalCopyright -cne $copyright) {
        Write-Failure "$($Expected.Exe) 的 LegalCopyright 是「$($info.LegalCopyright)」，csproj 是「$copyright」。"
    }

    if (-not "$($info.ProductVersion)".StartsWith("$version+", [System.StringComparison]::Ordinal)) {
        Write-Failure "$($Expected.Exe) 的 ProductVersion 是「$($info.ProductVersion)」，應該以「$version+」開頭。"
    }
}

foreach ($expected in $expectedBundles) {
    Test-Exe $expected
}

# 給發版時對照：SDK 與 apphost 的來源。讀不到只提醒，不算失敗。
if (-not $NoEnvironmentInfo) {
    Write-Host '=== 建置環境'
    $sdk = & dotnet --version 2>&1
    Write-Host "SDK：$sdk"
    $appHost = & dotnet msbuild (Join-Path $rootFull $expectedBundles[0].Project) -nologo -t:ResolveFrameworkReferences -p:RuntimeIdentifier=win-x64 -p:SelfContained=false -getProperty:AppHostSourcePath 2>&1
    if ($LASTEXITCODE -eq 0) {
        Write-Host "apphost 來源：$appHost"
    }
    else {
        Write-Host "::warning::$(Format-Annotation "讀不到 apphost 的來源：$appHost")"
    }
}

if ($script:failures -gt 0) {
    Write-Host "發行檔檢查失敗：$($script:failures) 個問題。"
    exit 1
}

Write-Host '發行檔檢查通過。'
exit 0
