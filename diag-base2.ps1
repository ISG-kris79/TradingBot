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

Write-Host "[1] BASE 패턴 검색 (TradeHistory)" -ForegroundColor Yellow
$sql1 = @'
SELECT DISTINCT Symbol FROM TradeHistory WHERE Symbol LIKE '%BASE%'
'@
Q $sql1 | Format-Table -AutoSize

Write-Host "`n[2] BASE 패턴 PositionState" -ForegroundColor Yellow
$sql2 = @'
SELECT * FROM PositionState WHERE Symbol LIKE '%BASE%'
'@
Q $sql2 | Format-Table -AutoSize

Write-Host "`n[3] 최근 24h Quantity=0 진입 케이스 (margin 0 의심)" -ForegroundColor Yellow
$sql3 = @'
SELECT TOP 30 Symbol, Side, Strategy,
    CAST(EntryPrice AS DECIMAL(18,8)) AS Entry,
    CAST(ExitPrice AS DECIMAL(18,8)) AS Exit_,
    CAST(Quantity AS DECIMAL(18,4)) AS Qty,
    CAST(PnL AS DECIMAL(18,4)) AS PnL,
    CAST(PnLPercent AS DECIMAL(8,2)) AS RoiPct,
    holdingMinutes AS HoldMin,
    ExitReason, EntryTime
FROM TradeHistory
WHERE UserId=1 AND IsClosed=1
AND ExitTime >= DATEADD(HOUR,-24,GETUTCDATE())
AND Quantity < 0.001
ORDER BY EntryTime DESC
'@
Q $sql3 | Format-Table -AutoSize

Write-Host "`n[4] Order_Error 24h - margin/quantity 관련" -ForegroundColor Yellow
$sql4 = @'
SELECT TOP 30 EventTime, Symbol, OrderType, Quantity, ErrorCode, ErrorMsg
FROM Order_Error
WHERE UserId=1
AND EventTime >= DATEADD(HOUR,-24,GETUTCDATE())
AND (ErrorMsg LIKE '%margin%' OR ErrorMsg LIKE '%quantity%' OR ErrorMsg LIKE '%notional%' OR ErrorMsg LIKE '%qty%' OR ErrorMsg LIKE '%PROTECT%' OR ErrorCode IN (-2019, -4131, -4111, -1102))
ORDER BY EventTime DESC
'@
Q $sql4 | Format-Table -AutoSize -Wrap

Write-Host "`n[5] 24h all entries - Qty/Notional" -ForegroundColor Yellow
$sql5 = @'
SELECT TOP 50 Symbol,
    CAST(Quantity AS DECIMAL(18,4)) AS Qty,
    CAST(EntryPrice AS DECIMAL(18,8)) AS Entry,
    CAST(EntryPrice * Quantity AS DECIMAL(18,2)) AS NotionalUSDT,
    Strategy, EntryTime
FROM TradeHistory
WHERE UserId=1 AND IsSimulation=0
AND EntryTime >= DATEADD(HOUR,-24,GETUTCDATE())
ORDER BY EntryTime DESC
'@
Q $sql5 | Format-Table -AutoSize
