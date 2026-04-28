$ErrorActionPreference = "Stop"
[Console]::OutputEncoding = [Text.Encoding]::UTF8
$AesKey = [byte[]](0x43, 0x6F, 0x69, 0x6E, 0x46, 0x46, 0x2D, 0x54, 0x72, 0x61, 0x64, 0x69, 0x6E, 0x67, 0x42, 0x6F, 0x74, 0x2D, 0x41, 0x45, 0x53, 0x32, 0x35, 0x36, 0x2D, 0x4B, 0x65, 0x79, 0x2D, 0x33, 0x32, 0x42)
function AesDecrypt($enc) {
    if ([string]::IsNullOrEmpty($enc)) { return "" }
    $f = [Convert]::FromBase64String($enc)
    $a = [System.Security.Cryptography.Aes]::Create(); $a.Key = $AesKey
    $iv = New-Object byte[] $a.IV.Length; $c = New-Object byte[] ($f.Length - $a.IV.Length)
    [Buffer]::BlockCopy($f, 0, $iv, 0, $a.IV.Length); [Buffer]::BlockCopy($f, $a.IV.Length, $c, 0, $c.Length)
    $a.IV = $iv; $d = $a.CreateDecryptor($a.Key, $a.IV)
    $s = [Text.Encoding]::UTF8.GetString($d.TransformFinalBlock($c, 0, $c.Length)); $a.Dispose(); $d.Dispose(); return $s
}
function Q($sql) {
    $cn = New-Object System.Data.SqlClient.SqlConnection (AesDecrypt (Get-Content "$PSScriptRoot\appsettings.json" -Raw | ConvertFrom-Json).ConnectionStrings.DefaultConnection); $cn.Open()
    $cm = $cn.CreateCommand(); $cm.CommandText = $sql; $cm.CommandTimeout = 60
    $ap = New-Object System.Data.SqlClient.SqlDataAdapter $cm
    $ds = New-Object System.Data.DataSet; [void]$ap.Fill($ds); $cn.Close()
    return $ds.Tables[0]
}

Write-Host "[1] BASEUSDT TradeHistory" -ForegroundColor Yellow
$sql1 = @'
SELECT TOP 10 Symbol, Side, Strategy,
    CAST(EntryPrice AS DECIMAL(18,8)) AS Entry,
    CAST(ExitPrice AS DECIMAL(18,8)) AS Exit_,
    CAST(Quantity AS DECIMAL(18,4)) AS Qty,
    CAST(PnL AS DECIMAL(18,4)) AS PnL,
    CAST(PnLPercent AS DECIMAL(8,2)) AS RoiPct,
    holdingMinutes AS HoldMin,
    ExitReason, EntryTime, ExitTime, IsClosed
FROM TradeHistory
WHERE Symbol='BASEUSDT' AND UserId=1
ORDER BY EntryTime DESC
'@
Q $sql1 | Format-Table -AutoSize

Write-Host "`n[2] BASEUSDT Order_Error" -ForegroundColor Yellow
$sql2 = @'
SELECT TOP 20 EventTime, Symbol, Side, OrderType, Quantity, ErrorCode, ErrorMsg
FROM Order_Error
WHERE Symbol='BASEUSDT' AND UserId=1
AND EventTime >= DATEADD(DAY,-2,GETUTCDATE())
ORDER BY EventTime DESC
'@
Q $sql2 | Format-Table -AutoSize -Wrap

Write-Host "`n[3] PositionState BASEUSDT (현재 상태)" -ForegroundColor Yellow
$sql3 = @'
SELECT * FROM PositionState WHERE Symbol='BASEUSDT' AND UserId=1
'@
Q $sql3 | Format-List

Write-Host "`n[4] FooterLogs BASEUSDT (진입/청산 흔적)" -ForegroundColor Yellow
$sql4 = @"
SELECT TABLE_NAME FROM INFORMATION_SCHEMA.TABLES WHERE TABLE_NAME='FooterLogs'
"@
$exists = Q $sql4
if ($exists.Rows.Count -gt 0) {
    $sql4b = @'
SELECT TOP 30 * FROM FooterLogs
WHERE (LogText LIKE '%BASEUSDT%' OR Message LIKE '%BASEUSDT%')
AND CreatedAt >= DATEADD(DAY,-2,GETUTCDATE())
ORDER BY CreatedAt DESC
'@
    try { Q $sql4b | Format-Table -AutoSize -Wrap }
    catch { Write-Host "FooterLogs schema mismatch" -ForegroundColor Red }
}

Write-Host "`n[5] Quantity=0 또는 PnL=0 케이스 전체 (7일)" -ForegroundColor Yellow
$sql5 = @'
SELECT TOP 30 Symbol, Side, Strategy,
    CAST(EntryPrice AS DECIMAL(18,6)) AS Entry,
    CAST(Quantity AS DECIMAL(18,4)) AS Qty,
    CAST(PnL AS DECIMAL(18,4)) AS PnL,
    CAST(PnLPercent AS DECIMAL(8,2)) AS RoiPct,
    ExitReason, EntryTime
FROM TradeHistory
WHERE UserId=1 AND IsClosed=1 AND IsSimulation=0
AND ExitTime >= DATEADD(DAY,-7,GETUTCDATE())
AND (Quantity=0 OR ABS(PnL)<0.01)
ORDER BY EntryTime DESC
'@
Q $sql5 | Format-Table -AutoSize
