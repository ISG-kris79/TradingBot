function Get-CS {
    $json = Get-Content "$PSScriptRoot\appsettings.json" -Raw | ConvertFrom-Json
    $enc = $json.ConnectionStrings.DefaultConnection
    $k = [byte[]](0x43, 0x6F, 0x69, 0x6E, 0x46, 0x46, 0x2D, 0x54,
                  0x72, 0x61, 0x64, 0x69, 0x6E, 0x67, 0x42, 0x6F,
                  0x74, 0x2D, 0x41, 0x45, 0x53, 0x32, 0x35, 0x36,
                  0x2D, 0x4B, 0x65, 0x79, 0x2D, 0x33, 0x32, 0x42)
    $f = [Convert]::FromBase64String($enc)
    $a = [System.Security.Cryptography.Aes]::Create(); $a.Key = $k
    $iv = New-Object byte[] $a.IV.Length
    $c = New-Object byte[] ($f.Length - $a.IV.Length)
    [Buffer]::BlockCopy($f, 0, $iv, 0, $a.IV.Length)
    [Buffer]::BlockCopy($f, $a.IV.Length, $c, 0, $c.Length)
    $a.IV = $iv
    $d = $a.CreateDecryptor($a.Key, $a.IV)
    $s = [System.Text.Encoding]::UTF8.GetString($d.TransformFinalBlock($c, 0, $c.Length))
    $a.Dispose(); $d.Dispose()
    return $s
}
function Q($sql) {
    $cn = New-Object System.Data.SqlClient.SqlConnection (Get-CS); $cn.Open()
    $cm = $cn.CreateCommand(); $cm.CommandText = $sql; $cm.CommandTimeout = 60
    $ap = New-Object System.Data.SqlClient.SqlDataAdapter $cm
    $ds = New-Object System.Data.DataSet; [void]$ap.Fill($ds); $cn.Close()
    return $ds.Tables[0]
}
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8

Write-Host "=== PIEVERSEUSDT 현재 활성 포지션 + SL/TP 상태 ===" -ForegroundColor Cyan
$sql1 = @'
SELECT TOP 5 Symbol, Side, EntryPrice, Quantity,
    CAST(StopLossPrice AS DECIMAL(18,6)) AS SL,
    CAST(TakeProfitPrice AS DECIMAL(18,6)) AS TP,
    CAST(TrailingStopPrice AS DECIMAL(18,6)) AS Trail,
    StopLossOrderId, TakeProfitOrderId, TrailingOrderId,
    Strategy, Leverage, EntryTime, LastUpdatedAt
FROM PositionState
WHERE Symbol='PIEVERSEUSDT' AND UserId=1
ORDER BY EntryTime DESC
'@
Q $sql1 | Format-List

Write-Host "`n=== PIEVERSEUSDT 최근 청산/주문 로그 ===" -ForegroundColor Yellow
$sql2 = @'
SELECT TOP 30 LogType, LogLevel, Message, CreatedAt
FROM Bot_Log
WHERE Message LIKE '%PIEVERSEUSDT%' AND UserId=1
ORDER BY CreatedAt DESC
'@
Q $sql2 | Format-Table -AutoSize -Wrap

Write-Host "`n=== Order_Error 최근 PIEVERSEUSDT/SL/TP 관련 ===" -ForegroundColor Yellow
$sql3 = @'
SELECT TOP 30 Symbol, ErrorCode, ErrorMessage, OrderType, CreatedAt
FROM Order_Error
WHERE (Symbol='PIEVERSEUSDT' OR Message LIKE '%PIEVERSE%' OR Message LIKE '%STOP%' OR Message LIKE '%algoOrder%')
AND CreatedAt >= DATEADD(HOUR,-24,GETUTCDATE())
ORDER BY CreatedAt DESC
'@
try { Q $sql3 | Format-Table -AutoSize -Wrap } catch { Write-Host "Order_Error scheme mismatch: $_" -ForegroundColor Red }

Write-Host "`n=== TradeHistory PIEVERSEUSDT 청산 기록 ===" -ForegroundColor Yellow
$sql4 = @'
SELECT TOP 20 Symbol, Side, Strategy,
    CAST(EntryPrice AS DECIMAL(18,6)) AS Entry,
    CAST(ExitPrice AS DECIMAL(18,6)) AS Exit,
    CAST(PnL AS DECIMAL(18,3)) AS PnL,
    CAST(PnLPercent AS DECIMAL(8,2)) AS RoiPct,
    holdingMinutes AS HoldMin,
    ExitReason, EntryTime, ExitTime
FROM TradeHistory
WHERE Symbol='PIEVERSEUSDT' AND UserId=1
ORDER BY ExitTime DESC
'@
Q $sql4 | Format-Table -AutoSize
