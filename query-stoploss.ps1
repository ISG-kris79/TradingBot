param(
    [int]$Hours = 48
)

function Get-DecryptedConnectionString {
    $appSettingsPath = Join-Path $PSScriptRoot "appsettings.json"
    $json = Get-Content $appSettingsPath -Raw | ConvertFrom-Json
    $encryptedConnectionString = $json.ConnectionStrings.DefaultConnection
    $isEncrypted = $json.ConnectionStrings.IsEncrypted
    if (-not $isEncrypted) { return $encryptedConnectionString }

    $aesKey = [byte[]](0x43, 0x6F, 0x69, 0x6E, 0x46, 0x46, 0x2D, 0x54,
                       0x72, 0x61, 0x64, 0x69, 0x6E, 0x67, 0x42, 0x6F,
                       0x74, 0x2D, 0x41, 0x45, 0x53, 0x32, 0x35, 0x36,
                       0x2D, 0x4B, 0x65, 0x79, 0x2D, 0x33, 0x32, 0x42)
    $fullCipher = [Convert]::FromBase64String($encryptedConnectionString)
    $aes = [System.Security.Cryptography.Aes]::Create()
    $aes.Key = $aesKey
    $ivLength = $aes.IV.Length
    $iv = New-Object byte[] $ivLength
    $cipher = New-Object byte[] ($fullCipher.Length - $ivLength)
    [Buffer]::BlockCopy($fullCipher, 0, $iv, 0, $ivLength)
    [Buffer]::BlockCopy($fullCipher, $ivLength, $cipher, 0, $cipher.Length)
    $aes.IV = $iv
    $decryptor = $aes.CreateDecryptor($aes.Key, $aes.IV)
    $decryptedBytes = $decryptor.TransformFinalBlock($cipher, 0, $cipher.Length)
    $decryptedString = [System.Text.Encoding]::UTF8.GetString($decryptedBytes)
    $aes.Dispose()
    $decryptor.Dispose()
    return $decryptedString
}

function Invoke-Query($query) {
    $cs = Get-DecryptedConnectionString
    $conn = New-Object System.Data.SqlClient.SqlConnection
    $conn.ConnectionString = $cs
    $conn.Open()
    $cmd = $conn.CreateCommand()
    $cmd.CommandText = $query
    $adapter = New-Object System.Data.SqlClient.SqlDataAdapter $cmd
    $ds = New-Object System.Data.DataSet
    [void]$adapter.Fill($ds)
    $conn.Close()
    return $ds.Tables[0]
}

$cutoff = (Get-Date).AddHours(-$Hours).ToString("yyyy-MM-dd HH:mm:ss")

Write-Host "=========================================="
Write-Host "손절 내역 조회 (최근 $Hours 시간)"
Write-Host "기준: $cutoff ~ 현재"
Write-Host "=========================================="
Write-Host ""

Write-Host "[1] TradeHistory 컬럼 확인:" -ForegroundColor Yellow
$cols = Invoke-Query "SELECT COLUMN_NAME FROM INFORMATION_SCHEMA.COLUMNS WHERE TABLE_NAME='TradeHistory' ORDER BY ORDINAL_POSITION"
$cols | Format-Table -AutoSize

Write-Host ""
Write-Host "[2] 최근 $Hours 시간 종결된 거래 전체 (최신 20건):" -ForegroundColor Yellow
$all = Invoke-Query @"
SELECT TOP 20
    Id, Symbol, Side, EntryPrice, ExitPrice,
    PnL, PnLPercent, ExitReason, EntryTime, ExitTime
FROM TradeHistory
WHERE IsClosed = 1
  AND ExitTime >= '$cutoff'
ORDER BY ExitTime DESC
"@
if ($all.Rows.Count -gt 0) {
    $all | Format-Table -AutoSize
} else {
    Write-Host "데이터 없음" -ForegroundColor Red
}

Write-Host ""
Write-Host "[3] 손절(PnL<0) 내역만:" -ForegroundColor Yellow
$losses = Invoke-Query @"
SELECT TOP 30
    Symbol, Side, EntryPrice, ExitPrice,
    PnL, PnLPercent, ExitReason, EntryTime, ExitTime
FROM TradeHistory
WHERE IsClosed = 1
  AND ExitTime >= '$cutoff'
  AND PnL < 0
ORDER BY ExitTime DESC
"@
if ($losses.Rows.Count -gt 0) {
    $losses | Format-Table -AutoSize
    Write-Host "총 손절 건수: $($losses.Rows.Count)" -ForegroundColor Red
} else {
    Write-Host "손절 내역 없음" -ForegroundColor Green
}

Write-Host ""
Write-Host "[4] ExitReason 분포 (손절만):" -ForegroundColor Yellow
$reasons = Invoke-Query @"
SELECT
    ISNULL(ExitReason, '(null)') AS Reason,
    COUNT(*) AS Cnt,
    SUM(PnL) AS TotalPnL,
    AVG(PnLPercent) AS AvgPnLPct
FROM TradeHistory
WHERE IsClosed = 1
  AND ExitTime >= '$cutoff'
  AND PnL < 0
GROUP BY ExitReason
ORDER BY Cnt DESC
"@
$reasons | Format-Table -AutoSize

Write-Host ""
Write-Host "[5] 전체 통계 (최근 $Hours 시간):" -ForegroundColor Yellow
$stats = Invoke-Query @"
SELECT
    COUNT(*) AS TotalClosed,
    SUM(CASE WHEN PnL > 0 THEN 1 ELSE 0 END) AS Wins,
    SUM(CASE WHEN PnL < 0 THEN 1 ELSE 0 END) AS Losses,
    SUM(CASE WHEN PnL = 0 THEN 1 ELSE 0 END) AS Breakeven,
    SUM(PnL) AS TotalPnL,
    AVG(PnLPercent) AS AvgPnLPct,
    MIN(PnL) AS WorstLoss,
    MAX(PnL) AS BestWin
FROM TradeHistory
WHERE IsClosed = 1
  AND ExitTime >= '$cutoff'
"@
$stats | Format-Table -AutoSize

Write-Host ""
Write-Host "=========================================="
Write-Host "조회 완료"
Write-Host "=========================================="
