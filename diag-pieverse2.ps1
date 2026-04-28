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

Write-Host "=== PositionState PIEVERSEUSDT (실제 컬럼만) ===" -ForegroundColor Cyan
Q @'
SELECT * FROM PositionState
WHERE Symbol='PIEVERSEUSDT' AND UserId=1
'@ | Format-List

Write-Host "`n=== Order_Error 최근 24h SL/TP/algoOrder/PIEVERSE 관련 ===" -ForegroundColor Yellow
Q @'
SELECT TOP 50 EventTime, Symbol, Side, OrderType, Quantity, ErrorCode, ErrorMsg, Resolved, RetryCount, Resolution
FROM Order_Error
WHERE UserId=1 AND EventTime >= DATEADD(HOUR,-24,GETUTCDATE())
AND (Symbol='PIEVERSEUSDT' OR ErrorMsg LIKE '%STOP%' OR ErrorMsg LIKE '%algo%' OR ErrorMsg LIKE '%4120%' OR ErrorMsg LIKE '%-2021%' OR ErrorMsg LIKE '%-1022%')
ORDER BY EventTime DESC
'@ | Format-Table -AutoSize -Wrap

Write-Host "`n=== TradeHistory PIEVERSEUSDT ===" -ForegroundColor Yellow
Q @'
SELECT TOP 30 Symbol, Side, Strategy,
    CAST(EntryPrice AS DECIMAL(18,8)) AS Entry,
    CAST(ExitPrice AS DECIMAL(18,8)) AS Px_Exit,
    CAST(PnL AS DECIMAL(18,3)) AS PnL,
    CAST(PnLPercent AS DECIMAL(8,2)) AS RoiPct,
    holdingMinutes AS HoldMin,
    ExitReason, EntryTime, ExitTime
FROM TradeHistory
WHERE Symbol='PIEVERSEUSDT' AND UserId=1
ORDER BY ExitTime DESC
'@ | Format-Table -AutoSize

Write-Host "`n=== Order_Error 전체 24h 통계 (top errors) ===" -ForegroundColor Yellow
Q @'
SELECT ErrorCode, COUNT(*) AS Cnt, MIN(LEFT(ErrorMsg,90)) AS Sample
FROM Order_Error
WHERE UserId=1 AND EventTime >= DATEADD(HOUR,-24,GETUTCDATE())
GROUP BY ErrorCode ORDER BY Cnt DESC
'@ | Format-Table -AutoSize -Wrap
