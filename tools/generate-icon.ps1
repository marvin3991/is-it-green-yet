#Requires -Version 7.0
<#
.SYNOPSIS
    以 docs/design-notes.md 定義的幾何路徑產生應用程式圖示 app.ico。

.DESCRIPTION
    用 WPF 把向量路徑渲染成多種尺寸的 PNG，再組成單一 .ico 檔（同 SAM 的做法）。
    圖示設計調整時只要改這支腳本裡的路徑與顏色，重跑即可，不必手動修圖。
    加上 -PreviewPath 時另外輸出一張預覽圖：左起 256 原圖、32 放大 8 倍、16 放大 16 倍，
    小尺寸用最近鄰放大，像素是什麼樣子就看到什麼樣子。

.EXAMPLE
    pwsh -File tools/generate-icon.ps1

.EXAMPLE
    pwsh -File tools/generate-icon.ps1 -PreviewPath docs/icon-preview.png
#>

[CmdletBinding()]
param(
    [string]$OutputPath = (Join-Path $PSScriptRoot '..\src\IsItGreenYet.App\Assets\app.ico'),
    [string]$PreviewPath
)

$ErrorActionPreference = 'Stop'
chcp 65001 > $null
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8

Add-Type -AssemblyName PresentationCore, PresentationFramework, WindowsBase

# 圖示設計：圓環＋打勾，也就是「綠了沒」的答案（spec §16.3）。EvenOdd 讓圓環中空。
# 圓環照 spec：外圓半徑 96、內圓半徑 70。打勾比 spec 加粗，16×16 才看得清楚：
# 兩臂平行邊的截距差由 36 加到 48（垂直於臂的寬度由 25.5 變成 48 ÷ √2 ≈ 33.9），長臂末端內縮 6，
# 打勾離內圓至少 16（16×16 時的 1 px）。推導見 docs/design-notes.md。
$pathData = 'F0 M 32,128 A 96,96 0 1 1 224,128 A 96,96 0 1 1 32,128 Z ' +
            'M 58,128 A 70,70 0 1 1 198,128 A 70,70 0 1 1 58,128 Z ' +
            'M 77,131 L 101,107 L 116,122 L 154,84 L 178,108 L 116,170 Z'
$backgroundColor = '#12171C'
$foregroundColor = '#4FC3B6'
$cornerRadius = 48.0
$designSize = 256.0

# 16 起跳涵蓋標題列與工作列，256 供 Explorer 大圖示使用（同 SAM）。
$sizes = @(16, 20, 24, 32, 48, 64, 128, 256)

# 預覽圖版面：每格 256 見方、四周留白 10、格與格之間 14，白底（同 spec §16.3 那張預覽）。
$previewSizes = @(256, 32, 16)
$previewCell = 256
$previewMargin = 10
$previewGap = 14
$previewStride = $previewCell + $previewGap
$previewWidth = (2 * $previewMargin) + (($previewSizes.Count - 1) * $previewStride) + $previewCell
$previewHeight = (2 * $previewMargin) + $previewCell

function New-IconBitmap {
    param([int]$Size)

    $geometry = [System.Windows.Media.Geometry]::Parse($pathData)
    $background = [System.Windows.Media.SolidColorBrush]::new(
        [System.Windows.Media.ColorConverter]::ConvertFromString($backgroundColor))
    $foreground = [System.Windows.Media.SolidColorBrush]::new(
        [System.Windows.Media.ColorConverter]::ConvertFromString($foregroundColor))

    $visual = [System.Windows.Media.DrawingVisual]::new()
    $context = $visual.RenderOpen()
    try {
        $rect = [System.Windows.Rect]::new(0, 0, $designSize, $designSize)
        $context.DrawRoundedRectangle($background, $null, $rect, $cornerRadius, $cornerRadius)
        $context.DrawGeometry($foreground, $null, $geometry)
    }
    finally {
        $context.Close()
    }

    # 以設計尺寸繪製後再縮放，小尺寸才不會因為重新佈局而走樣。
    $scale = $Size / $designSize
    $visual.Transform = [System.Windows.Media.ScaleTransform]::new($scale, $scale)

    $bitmap = [System.Windows.Media.Imaging.RenderTargetBitmap]::new(
        $Size, $Size, 96, 96, [System.Windows.Media.PixelFormats]::Pbgra32)
    $bitmap.Render($visual)
    return $bitmap
}

function ConvertTo-PngBytes {
    param([System.Windows.Media.Imaging.BitmapSource]$Bitmap)

    $encoder = [System.Windows.Media.Imaging.PngBitmapEncoder]::new()
    $encoder.Frames.Add([System.Windows.Media.Imaging.BitmapFrame]::Create($Bitmap))

    $stream = [System.IO.MemoryStream]::new()
    try {
        $encoder.Save($stream)
        # 前置逗號避免 PowerShell 把位元組陣列展開成一串個別位元組
        return , $stream.ToArray()
    }
    finally {
        $stream.Dispose()
    }
}

function Get-FullPathWithDirectory {
    param([string]$Path)

    # 相對路徑以 PowerShell 目前位置為準（.NET 的目前目錄不一定跟著 Set-Location 改變）。
    # 轉成絕對路徑後再用 GetFullPath 收掉其中的 ..。
    $full = [System.IO.Path]::GetFullPath(
        $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($Path))
    $directory = [System.IO.Path]::GetDirectoryName($full)
    if (-not (Test-Path -LiteralPath $directory)) {
        New-Item -ItemType Directory -Path $directory -Force | Out-Null
    }

    return $full
}

$frames = foreach ($size in $sizes) {
    [byte[]]$data = ConvertTo-PngBytes -Bitmap (New-IconBitmap -Size $size)
    if ($data.Length -lt 64) {
        throw "尺寸 $size 的 PNG 資料異常（只有 $($data.Length) bytes）"
    }

    [pscustomobject]@{ Size = $size; Data = $data }
}

$outputFull = Get-FullPathWithDirectory -Path $OutputPath

# ICO 檔結構：ICONDIR(6) + ICONDIRENTRY(16 × N) + 影像資料
# Vista 之後的 Windows 支援 ICO 內直接放 PNG，省下 BMP+AND mask 的處理。
$fileStream = [System.IO.File]::Create($outputFull)
try {
    $writer = [System.IO.BinaryWriter]::new($fileStream)

    $writer.Write([uint16]0)                 # Reserved
    $writer.Write([uint16]1)                 # Type: 1 = icon
    $writer.Write([uint16]$frames.Count)     # 影像數量

    $offset = 6 + (16 * $frames.Count)
    foreach ($frame in $frames) {
        # 256 在 1 byte 欄位中以 0 表示
        $dimension = if ($frame.Size -ge 256) { 0 } else { $frame.Size }
        $writer.Write([byte]$dimension)      # 寬
        $writer.Write([byte]$dimension)      # 高
        $writer.Write([byte]0)               # 調色盤色數：0 = 不使用調色盤
        $writer.Write([byte]0)               # Reserved
        $writer.Write([uint16]1)             # 色彩平面
        $writer.Write([uint16]32)            # 每像素位元數
        $writer.Write([uint32]$frame.Data.Length)
        $writer.Write([uint32]$offset)
        $offset += $frame.Data.Length
    }

    foreach ($frame in $frames) {
        $writer.Write($frame.Data)
    }

    $writer.Flush()
}
finally {
    $fileStream.Dispose()
}

$totalBytes = (Get-Item -LiteralPath $outputFull).Length
Write-Host "已產生 $outputFull（$($frames.Count) 種尺寸，$totalBytes bytes）"

if ($PreviewPath) {
    $canvas = [System.Windows.Media.DrawingVisual]::new()
    $context = $canvas.RenderOpen()
    try {
        $context.DrawRectangle([System.Windows.Media.Brushes]::White, $null,
            [System.Windows.Rect]::new(0, 0, $previewWidth, $previewHeight))

        for ($i = 0; $i -lt $previewSizes.Count; $i++) {
            $bitmap = New-IconBitmap -Size $previewSizes[$i]
            $cell = [System.Windows.Rect]::new(
                $previewMargin + ($i * $previewStride), $previewMargin, $previewCell, $previewCell)

            # 最近鄰放大：不內插，每個像素放大成方塊。
            $group = [System.Windows.Media.DrawingGroup]::new()
            [System.Windows.Media.RenderOptions]::SetBitmapScalingMode(
                $group, [System.Windows.Media.BitmapScalingMode]::NearestNeighbor)
            $group.Children.Add([System.Windows.Media.ImageDrawing]::new($bitmap, $cell))
            $context.DrawDrawing($group)
        }
    }
    finally {
        $context.Close()
    }

    $sheet = [System.Windows.Media.Imaging.RenderTargetBitmap]::new(
        $previewWidth, $previewHeight, 96, 96, [System.Windows.Media.PixelFormats]::Pbgra32)
    $sheet.Render($canvas)

    $previewFull = Get-FullPathWithDirectory -Path $PreviewPath
    [System.IO.File]::WriteAllBytes($previewFull, (ConvertTo-PngBytes -Bitmap $sheet))
    Write-Host "已產生預覽 $previewFull（左起 256、32 放大、16 放大）"
}
