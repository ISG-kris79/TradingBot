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

Write-Host "=== [1] BLA 관련 심볼 검색 ===" -ForegroundColor Cyan
Q "SELECT DISTINCT Symbol FROM TradeHistory WHERE UserId=1 AND Symbol LIKE '%BLA%' ORDER BY Symbol" | Format-Table -AutoSize

Write-Host "=== [2] BLA 최근 거래 내역 ===" -ForegroundColor Cyan
Q @"
SELECT Id, Symbol, Side, EntryPrice, ExitPrice, PnL, PnLPercent,
    LEFT(ExitReason, 60) AS Reason,
    DATEDIFF(SECOND, EntryTime, ExitTime) AS HoldSec,
    EntryTime, ExitTime, Strategy
FROM TradeHistory
WHERE UserId=1 AND Symbol LIKE '%BLA%'
ORDER BY EntryTime DESC
"@ | Format-List

Write-Host "=== [3] BLA 5분봉 진입 전후 ===" -ForegroundColor Cyan
$entry = Q "SELECT TOP 1 Symbol, EntryTime FROM TradeHistory WHERE UserId=1 AND Symbol LIKE '%BLA%' ORDER BY EntryTime DESC"
if ($entry.Rows.Count -gt 0) {
    $sym = $entry.Rows[0].Symbol
    $et = $entry.Rows[0].EntryTime
    Write-Host "Symbol: $sym, EntryTime: $et" -ForegroundColor Yellow
    Q "SELECT OpenTime, [Open], High, Low, [Close], CAST((High-Low)/[Open]*100 AS decimal(6,2)) AS RangePct FROM CandleData WHERE Symbol='$sym' AND IntervalText='5m' AND OpenTime BETWEEN DATEADD(MINUTE,-30,'$et') AND DATEADD(MINUTE,30,'$et') ORDER BY OpenTime ASC" | Format-Table -AutoSize
}
