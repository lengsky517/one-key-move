# ============================================================================
#  一键挪动 - 应用图标生成（Lead 维护）
#  纯 System.Drawing 绘制 256px 图标，写入多尺寸 PNG 压缩的 .ico
#  用法: powershell -ExecutionPolicy Bypass -File tools\make-icon.ps1
# ============================================================================
param(
    [string]$Output = ''
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)
if ([string]::IsNullOrEmpty($Output)) {
    $Output = Join-Path $root 'resources\app.ico'
}
$resDir = Split-Path -Parent $Output
if (-not (Test-Path $resDir)) { New-Item -ItemType Directory -Force -Path $resDir | Out-Null }

Add-Type -AssemblyName System.Drawing

function New-RoundRectPath([System.Drawing.RectangleF]$r, [float]$radius) {
    $p = New-Object System.Drawing.Drawing2D.GraphicsPath
    $d = $radius * 2
    $arc = New-Object System.Drawing.RectangleF($r.X, $r.Y, $d, $d)
    $p.AddArc($arc, 180, 90)
    $arc.X = $r.Right - $d
    $p.AddArc($arc, 270, 90)
    $arc.Y = $r.Bottom - $d
    $p.AddArc($arc, 0, 90)
    $arc.X = $r.X
    $p.AddArc($arc, 90, 90)
    $p.CloseFigure()
    return $p
}

function New-AppIconBitmap([int]$size) {
    $bmp = New-Object System.Drawing.Bitmap($size, $size, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
    $g.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
    $g.Clear([System.Drawing.Color]::Transparent)

    $s = [float]$size
    $pad = $s * 0.055
    $rect = New-Object System.Drawing.RectangleF($pad, $pad, ($s - $pad * 2), ($s - $pad * 2))

    # 背景：蓝色渐变圆角方块
    $bgPath = New-RoundRectPath $rect ($s * 0.22)
    $brush = New-Object System.Drawing.Drawing2D.LinearGradientBrush(
        (New-Object System.Drawing.PointF(0, 0)),
        (New-Object System.Drawing.PointF($s, $s)),
        [System.Drawing.Color]::FromArgb(255, 59, 130, 246),
        [System.Drawing.Color]::FromArgb(255, 29, 78, 216))
    $g.FillPath($brush, $bgPath)

    # 顶部高光
    $glowRect = New-Object System.Drawing.RectangleF($pad, $pad, ($s - $pad * 2), ($s * 0.55))
    $glowPath = New-RoundRectPath $glowRect ($s * 0.20)
    $glow = New-Object System.Drawing.Drawing2D.LinearGradientBrush(
        (New-Object System.Drawing.PointF(0, $pad)),
        (New-Object System.Drawing.PointF(0, ($pad + $s * 0.55))),
        [System.Drawing.Color]::FromArgb(56, 255, 255, 255),
        [System.Drawing.Color]::FromArgb(0, 255, 255, 255))
    $g.FillPath($glow, $glowPath)

    # 白色文件夹（居中偏下）
    $fx = $s * 0.18
    $fw = $s * 0.64
    $fy = $s * 0.40
    $fh = $s * 0.40
    $folderBrush = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(242, 255, 255, 255))
    $tabPath = New-RoundRectPath (New-Object System.Drawing.RectangleF($fx, ($fy - $s * 0.075), ($fw * 0.46), ($s * 0.16))) ($s * 0.035)
    $g.FillPath($folderBrush, $tabPath)
    $bodyPath = New-RoundRectPath (New-Object System.Drawing.RectangleF($fx, $fy, $fw, $fh)) ($s * 0.05)
    $g.FillPath($folderBrush, $bodyPath)

    # 文件夹上的浅蓝内衬（增加层次）
    $inner = New-Object System.Drawing.RectangleF(($fx + $s * 0.03), ($fy + $s * 0.05), ($fw - $s * 0.06), ($fh - $s * 0.09))
    $innerPath = New-RoundRectPath $inner ($s * 0.03)
    $innerBrush = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(36, 37, 99, 235))
    $g.FillPath($innerBrush, $innerPath)

    # 白色向上右箭头（搬家/移动）
    $penWidth = $s * 0.093
    $arrowPen = New-Object System.Drawing.Pen([System.Drawing.Color]::White, $penWidth)
    $arrowPen.StartCap = [System.Drawing.Drawing2D.LineCap]::Round
    $arrowPen.EndCap = [System.Drawing.Drawing2D.LineCap]::Round
    $arrowPen.LineJoin = [System.Drawing.Drawing2D.LineJoin]::Round
    $p1 = New-Object System.Drawing.PointF(($s * 0.36), ($s * 0.68))
    $p2 = New-Object System.Drawing.PointF(($s * 0.66), ($s * 0.38))
    $g.DrawLine($arrowPen, $p1, $p2)
    $head = New-Object System.Drawing.Drawing2D.GraphicsPath
    $hx = $s * 0.66
    $hy = $s * 0.38
    $head.AddPolygon(@(
        (New-Object System.Drawing.PointF(($hx - $s * 0.155), ($hy - $s * 0.012))),
        (New-Object System.Drawing.PointF(($hx - $s * 0.012), ($hy - $s * 0.155))),
        (New-Object System.Drawing.PointF(($hx + $s * 0.012), ($hy + $s * 0.012)))
    ))
    $g.FillPath((New-Object System.Drawing.SolidBrush([System.Drawing.Color]::White)), $head)

    $g.Dispose()
    return $bmp
}

# 生成各尺寸 PNG 字节
$sizes = @(16, 24, 32, 48, 64, 128, 256)
$entries = @()
foreach ($sz in $sizes) {
    $bmp = New-AppIconBitmap $sz
    $ms = New-Object System.IO.MemoryStream
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    $bytes = $ms.ToArray()
    $ms.Dispose()
    $bmp.Dispose()
    $entries += [pscustomobject]@{ Size = $sz; Data = $bytes }
}

# 组装 ICO：ICONDIR(6) + ICONDIRENTRY(16*N) + 图片数据
$ms = New-Object System.IO.MemoryStream
$bw = New-Object System.IO.BinaryWriter($ms)
$bw.Write([UInt16]0)          # reserved
$bw.Write([UInt16]1)          # type = icon
$bw.Write([UInt16]$entries.Count)

$offset = 6 + 16 * $entries.Count
foreach ($e in $entries) {
    $dim = if ($e.Size -ge 256) { 0 } else { $e.Size }
    $bw.Write([byte]$dim)     # width
    $bw.Write([byte]$dim)     # height
    $bw.Write([byte]0)        # color count
    $bw.Write([byte]0)        # reserved
    $bw.Write([UInt16]1)      # planes
    $bw.Write([UInt16]32)     # bit count
    $bw.Write([UInt32]$e.Data.Length)
    $bw.Write([UInt32]$offset)
    $offset += $e.Data.Length
}
foreach ($e in $entries) { $bw.Write($e.Data) }
$bw.Flush()
$icoBytes = $ms.ToArray()
$bw.Dispose()
$ms.Dispose()

[System.IO.File]::WriteAllBytes($Output, $icoBytes)
Write-Host ("图标已生成: {0}  ({1:N0} 字节, {2} 个尺寸)" -f $Output, $icoBytes.Length, $entries.Count) -ForegroundColor Green

# 另存 256px PNG 供预览
$preview = New-AppIconBitmap 256
$previewPath = Join-Path (Split-Path -Parent $Output) 'app-icon-256.png'
$preview.Save($previewPath, [System.Drawing.Imaging.ImageFormat]::Png)
$preview.Dispose()
Write-Host "预览图: $previewPath" -ForegroundColor DarkGray
