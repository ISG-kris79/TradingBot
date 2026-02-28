# TradingBot 고품질 아이콘 생성
Add-Type -AssemblyName System.Drawing

$size = 256
$bmp = New-Object System.Drawing.Bitmap($size, $size)
$g = [System.Drawing.Graphics]::FromImage($bmp)
$g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias

# 색상
$bg = [System.Drawing.Color]::FromArgb(15, 23, 42)
$grad = [System.Drawing.Color]::FromArgb(30, 58, 138)
$blue = [System.Drawing.Color]::FromArgb(59, 130, 246)
$green = [System.Drawing.Color]::FromArgb(34, 197, 94)

# 배경
$g.Clear($bg)

# 배경 사각형
$margin = 16
$w = $size - ($margin * 2)
$rect = [System.Drawing.Rectangle]::new($margin, $margin, $w, $w)
$g.FillRectangle([System.Drawing.SolidBrush]::new($grad), $rect)
$g.DrawRectangle([System.Drawing.Pen]::new($blue, 2), $rect)

# 차트 막대
$g.FillRectangle([System.Drawing.SolidBrush]::new($blue), 64, 180, 16, 40)
$g.FillRectangle([System.Drawing.SolidBrush]::new($green), 96, 160, 16, 60)
$g.FillRectangle([System.Drawing.SolidBrush]::new($blue), 128, 170, 16, 50)

# 화살표 (상승 추세)
$points = @(
    [System.Drawing.Point]::new(160, 80),
    [System.Drawing.Point]::new(200, 80),
    [System.Drawing.Point]::new(180, 50)
)
$g.FillPolygon([System.Drawing.SolidBrush]::new($green), $points)

$g.Dispose()

# 저장
$ico_path = 'e:\PROJECT\CoinFF\TradingBot\TradingBot\trading_bot.ico'
$png_path = 'e:\PROJECT\CoinFF\TradingBot\TradingBot\trading_bot.png'

$bmp.Save($png_path, [System.Drawing.Imaging.ImageFormat]::Png)
$bmp.Save($ico_path, [System.Drawing.Imaging.ImageFormat]::Icon)
$bmp.Dispose()

Write-Host "✅ 아이콘 생성 완료!" -ForegroundColor Green
Write-Host "  - $ico_path"
Write-Host "  - $png_path"

