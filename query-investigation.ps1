param(
    [string]$Date = ""
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
    $cmd.CommandTimeout = 60
    $adapter = New-Object System.Data.SqlClient.SqlDataAdapter $cmd
    $ds = New-Object System.Data.DataSet
    [void]$adapter.Fill($ds)
    $conn.Close()
    return $ds.Tables[0]
}

# UTF-8 출력 설정
$OutputEncoding = [System.Text.Encoding]::UTF8
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8

Write-Host "=========================================="
Write-Host "[A] SOL EMERGENCY_CIRCUIT_BREAKER 상세"
Write-Host "=========================================="
$sol = Invoke-Query @"
SELECT Id, Symbol, Side, EntryPrice, ExitPrice, Quantity, PnL, PnLPercent,
       ExitReason, EntryTime, ExitTime, Strategy, AiScore,
       DATEDIFF(MINUTE, EntryTime, ExitTime) AS HoldMin
FROM TradeHistory
WHERE Symbol='SOLUSDT' AND ExitReason LIKE '%CIRCUIT%'
ORDER BY ExitTime DESC
"@
$sol | Format-List

Write-Host "=========================================="
Write-Host "[B] ROE 손절 -40% 5건 상세"
Write-Host "=========================================="
$roe = Invoke-Query @"
SELECT Id, Symbol, Side, EntryPrice, ExitPrice, Quantity, PnL, PnLPercent,
       ExitReason, EntryTime, ExitTime, Strategy,
       DATEDIFF(MINUTE, EntryTime, ExitTime) AS HoldMin
FROM TradeHistory
WHERE IsClosed = 1
  AND ExitReason LIKE '%ROE%'
  AND PnLPercent < -30
ORDER BY ExitTime DESC
"@
$roe | Format-Table -AutoSize

Write-Host "=========================================="
Write-Host "[C] BASUSDT -63% 상세"
Write-Host "=========================================="
$bas = Invoke-Query @"
SELECT Id, Symbol, Side, EntryPrice, ExitPrice, Quantity, PnL, PnLPercent,
       ExitReason, EntryTime, ExitTime, Strategy, AiScore,
       DATEDIFF(MINUTE, EntryTime, ExitTime) AS HoldMin
FROM TradeHistory
WHERE Symbol='BASUSDT'
ORDER BY ExitTime DESC
"@
$bas | Format-List

Write-Host "=========================================="
Write-Host "[D] XRP 최근 3일 거래 내역"
Write-Host "=========================================="
$xrp = Invoke-Query @"
SELECT Id, Symbol, Side, EntryPrice, ExitPrice, PnL, PnLPercent,
       ExitReason, EntryTime, ExitTime, Strategy
FROM TradeHistory
WHERE Symbol='XRPUSDT'
  AND EntryTime >= DATEADD(DAY, -3, GETDATE())
ORDER BY EntryTime DESC
"@
if ($xrp.Rows.Count -gt 0) { $xrp | Format-Table -AutoSize } else { Write-Host "XRP 거래 없음" -ForegroundColor Red }

Write-Host ""
Write-Host "=========================================="
Write-Host "[E] XRP 최근 12:00-12:30 분봉 가격 (5분봉)"
Write-Host "=========================================="
$xrpCandles = Invoke-Query @"
SELECT TOP 30 Symbol, [Time], OpenPrice, HighPrice, LowPrice, ClosePrice, Volume
FROM CandleData
WHERE Symbol='XRPUSDT'
  AND [Time] >= DATEADD(HOUR, -6, GETDATE())
ORDER BY [Time] DESC
"@
$xrpCandles | Format-Table -AutoSize

Write-Host ""
Write-Host "=========================================="
Write-Host "[F] XRP 관련 FooterLogs (최근 6시간)"
Write-Host "=========================================="
# FooterLogs 테이블 존재 여부 체크
$footerExists = Invoke-Query "SELECT COUNT(*) AS Cnt FROM INFORMATION_SCHEMA.TABLES WHERE TABLE_NAME='FooterLogs'"
if ($footerExists.Rows[0].Cnt -gt 0) {
    $xrpLogs = Invoke-Query @"
SELECT TOP 50 Id, Timestamp, Message
FROM FooterLogs
WHERE Message LIKE '%XRPUSDT%'
  AND Timestamp >= DATEADD(HOUR, -6, GETDATE())
ORDER BY Id DESC
"@
    if ($xrpLogs.Rows.Count -gt 0) {
        $xrpLogs | Format-Table -AutoSize -Wrap
    } else {
        Write-Host "XRP 관련 로그 없음" -ForegroundColor Yellow
    }
} else {
    Write-Host "FooterLogs 테이블 없음" -ForegroundColor Yellow
}

Write-Host ""
Write-Host "=========================================="
Write-Host "[G] AiSignalLogs — XRP (최근 12시간)"
Write-Host "=========================================="
$aiExists = Invoke-Query "SELECT COUNT(*) AS Cnt FROM INFORMATION_SCHEMA.TABLES WHERE TABLE_NAME='AiSignalLogs'"
if ($aiExists.Rows[0].Cnt -gt 0) {
    $xrpAi = Invoke-Query @"
SELECT TOP 30 Symbol, Decision, CoinType, AllowEntry, Reason,
       ML_Confidence, TF_Confidence, Trend, Timestamp
FROM AiSignalLogs
WHERE Symbol='XRPUSDT'
  AND Timestamp >= DATEADD(HOUR, -12, GETDATE())
ORDER BY Id DESC
"@
    if ($xrpAi.Rows.Count -gt 0) {
        $xrpAi | Format-Table -AutoSize
    } else {
        Write-Host "XRP AI 신호 로그 없음 (AI Gate 평가 자체가 없었음)" -ForegroundColor Yellow
    }
} else {
    Write-Host "AiSignalLogs 테이블 없음" -ForegroundColor Yellow
}

Write-Host ""
Write-Host "=========================================="
Write-Host "[H] EntryDecisions — XRP 최근 12시간 (전체 판단)"
Write-Host "=========================================="
$edExists = Invoke-Query "SELECT COUNT(*) AS Cnt FROM INFORMATION_SCHEMA.TABLES WHERE TABLE_NAME='EntryDecisions'"
if ($edExists.Rows[0].Cnt -gt 0) {
    $cols = Invoke-Query "SELECT TOP 1 * FROM EntryDecisions"
    $colNames = $cols.Columns | ForEach-Object { $_.ColumnName }
    Write-Host "EntryDecisions 컬럼: $($colNames -join ', ')" -ForegroundColor Gray

    $xrpEd = Invoke-Query @"
SELECT TOP 30 * FROM EntryDecisions
WHERE Symbol='XRPUSDT'
  AND CreatedAt >= DATEADD(HOUR, -12, GETDATE())
ORDER BY Id DESC
"@
    if ($xrpEd.Rows.Count -gt 0) {
        $xrpEd | Format-Table -AutoSize
    } else {
        Write-Host "XRP EntryDecisions 없음" -ForegroundColor Yellow
    }
} else {
    Write-Host "EntryDecisions 테이블 없음" -ForegroundColor Yellow
}

Write-Host ""
Write-Host "=========================================="
Write-Host "[I] 메이저 진입 내역 최근 24h"
Write-Host "=========================================="
$majorEntries = Invoke-Query @"
SELECT Symbol, Side, Strategy, EntryTime, EntryPrice, ExitPrice, PnL, ExitReason
FROM TradeHistory
WHERE Symbol IN ('BTCUSDT','ETHUSDT','SOLUSDT','XRPUSDT','BNBUSDT','ADAUSDT','DOGEUSDT','AVAXUSDT','LINKUSDT')
  AND EntryTime >= DATEADD(HOUR, -24, GETDATE())
ORDER BY EntryTime DESC
"@
if ($majorEntries.Rows.Count -gt 0) {
    $majorEntries | Format-Table -AutoSize
} else {
    Write-Host "메이저 진입 없음" -ForegroundColor Red
}

Write-Host ""
Write-Host "=========================================="
Write-Host "완료"
Write-Host "=========================================="
