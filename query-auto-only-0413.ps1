function Get-CS {
    $json = Get-Content "$PSScriptRoot\appsettings.json" -Raw | ConvertFrom-Json
    $enc = $json.ConnectionStrings.DefaultConnection
    $k = [byte[]](0x43, 0x6F, 0x69, 0x6E, 0x46, 0x46, 0x2D, 0x54, 0x72, 0x61, 0x64, 0x69, 0x6E, 0x67, 0x42, 0x6F, 0x74, 0x2D, 0x41, 0x45, 0x53, 0x32, 0x35, 0x36, 0x2D, 0x4B, 0x65, 0x79, 0x2D, 0x33, 0x32, 0x42)
    $f = [Convert]::FromBase64String($enc)
    $a = [System.Security.Cryptography.Aes]::Create(); $a.Key = $k
    $iv = New-Object byte[] $a.IV.Length; $c = New-Object byte[] ($f.Length - $a.IV.Length)
    [Buffer]::BlockCopy($f, 0, $iv, 0, $a.IV.Length); [Buffer]::BlockCopy($f, $a.IV.Length, $c, 0, $c.Length)
    $a.IV = $iv; $d = $a.CreateDecryptor($a.Key, $a.IV)
    $s = [Text.Encoding]::UTF8.GetString($d.TransformFinalBlock($c, 0, $c.Length)); $a.Dispose(); $d.Dispose(); return $s
}
function Q($sql) {
    $cn = New-Object System.Data.SqlClient.SqlConnection (Get-CS); $cn.Open()
    $cm = $cn.CreateCommand(); $cm.CommandText = $sql; $cm.CommandTimeout = 30
    $ap = New-Object System.Data.SqlClient.SqlDataAdapter $cm; $ds = New-Object System.Data.DataSet; [void]$ap.Fill($ds); $cn.Close()
    return $ds.Tables[0]
}
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8

$autoFilter = "UserId=1 AND IsClosed=1 AND Strategy NOT IN ('MANUAL','MarketClose') AND Strategy NOT LIKE 'EXTERNAL%'"

Write-Host "################################################################"
Write-Host "# UserId=1 04-13 자동매매만 (MANUAL/EXTERNAL 제외)"
Write-Host "################################################################"

Write-Host "=== [1] 전체 통계 ===" -ForegroundColor Cyan
Q "SELECT COUNT(*) AS Total, SUM(CASE WHEN PnL>0 THEN 1 ELSE 0 END) AS W, SUM(CASE WHEN PnL<0 THEN 1 ELSE 0 END) AS L, SUM(PnL) AS Net, AVG(PnLPercent) AS AvgPct FROM TradeHistory WHERE $autoFilter AND EntryTime >= '2026-04-13 00:00:00'" | Format-Table -AutoSize

Write-Host "=== [2] 전체 거래 리스트 ===" -ForegroundColor Cyan
Q "SELECT Symbol, Side, PnL, PnLPercent, LEFT(ExitReason,50) AS Reason, DATEDIFF(SECOND,EntryTime,ExitTime) AS Sec, EntryTime, Strategy FROM TradeHistory WHERE $autoFilter AND EntryTime >= '2026-04-13 00:00:00' ORDER BY EntryTime ASC" | Format-Table -AutoSize

Write-Host "=== [3] 활성 포지션 (자동매매) ===" -ForegroundColor Cyan
Q "SELECT Symbol, Side, EntryPrice, Quantity, EntryTime, Strategy FROM TradeHistory WHERE UserId=1 AND IsClosed=0 AND Strategy NOT IN ('MANUAL') ORDER BY EntryTime DESC" | Format-Table -AutoSize

Write-Host "=== [4] 04-12~13 자동매매 일별 비교 ===" -ForegroundColor Cyan
Q @"
SELECT
    CAST(EntryTime AS DATE) AS Day,
    COUNT(*) AS Total,
    SUM(CASE WHEN PnL>0 THEN 1 ELSE 0 END) AS W,
    SUM(CASE WHEN PnL<0 THEN 1 ELSE 0 END) AS L,
    SUM(PnL) AS Net,
    CAST(100.0*SUM(CASE WHEN PnL>0 THEN 1 ELSE 0 END)/NULLIF(COUNT(*),0) AS decimal(5,1)) AS WinR
FROM TradeHistory
WHERE $autoFilter AND EntryTime >= '2026-04-12 00:00:00'
GROUP BY CAST(EntryTime AS DATE)
ORDER BY Day
"@ | Format-Table -AutoSize

Write-Host "=== [5] 서킷 브레이커 발동/해제 로그 ===" -ForegroundColor Cyan
Q "SELECT TOP 10 Timestamp, LEFT(Message,150) AS Msg FROM FooterLogs WHERE (Message LIKE '%서킷%발동%' OR Message LIKE '%circuit%tripped%' OR Message LIKE '%Reset%' OR Message LIKE '%서킷%해제%') ORDER BY Id DESC" | Format-Table -AutoSize
