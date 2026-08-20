$AesKey = [byte[]](0x43, 0x6F, 0x69, 0x6E, 0x46, 0x46, 0x2D, 0x54, 0x72, 0x61, 0x64, 0x69, 0x6E, 0x67, 0x42, 0x6F, 0x74, 0x2D, 0x41, 0x45, 0x53, 0x32, 0x35, 0x36, 0x2D, 0x4B, 0x65, 0x79, 0x2D, 0x33, 0x32, 0x42)
function AesDecrypt($enc) {
    $f = [Convert]::FromBase64String($enc)
    $a = [System.Security.Cryptography.Aes]::Create(); $a.Key = $AesKey
    $iv = New-Object byte[] $a.IV.Length; $c = New-Object byte[] ($f.Length - $a.IV.Length)
    [Buffer]::BlockCopy($f, 0, $iv, 0, $a.IV.Length); [Buffer]::BlockCopy($f, $a.IV.Length, $c, 0, $c.Length)
    $a.IV = $iv; $d = $a.CreateDecryptor($a.Key, $a.IV)
    $s = [Text.Encoding]::UTF8.GetString($d.TransformFinalBlock($c, 0, $c.Length)); $a.Dispose(); $d.Dispose(); return $s
}
$json = Get-Content "e:\PROJECT\CoinFF\TradingBot\TradingBot\appsettings.json" -Raw | ConvertFrom-Json
$cs = AesDecrypt $json.ConnectionStrings.DefaultConnection
function Q($sql) {
    $cn = New-Object System.Data.SqlClient.SqlConnection $cs; $cn.Open()
    $cm = $cn.CreateCommand(); $cm.CommandText = $sql; $cm.CommandTimeout = 120
    $ap = New-Object System.Data.SqlClient.SqlDataAdapter $cm
    $ds = New-Object System.Data.DataSet; [void]$ap.Fill($ds); $cn.Close()
    if ($ds.Tables.Count -gt 0) { return $ds.Tables[0] } else { return $null }
}
function E($sql) {
    $cn = New-Object System.Data.SqlClient.SqlConnection $cs; $cn.Open()
    $cm = $cn.CreateCommand(); $cm.CommandText = $sql; $cm.CommandTimeout = 120
    $n = $cm.ExecuteNonQuery(); $cn.Close(); return $n
}
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8

Write-Host "=== target rows (|PnL| >= 1,000,000) ===" -ForegroundColor Cyan
Q @'
SELECT Id, UserId, Symbol, Side, Strategy,
  CONVERT(varchar(16), EntryTime, 120) Ent, CONVERT(varchar(16), ExitTime, 120) Exi,
  CAST(EntryPrice AS DECIMAL(20,6)) EnP, CAST(ExitPrice AS DECIMAL(20,8)) ExP,
  CAST(Quantity AS DECIMAL(20,2)) Qty, CAST(PnL AS DECIMAL(20,2)) PnL
FROM TradeHistory WHERE Symbol NOT LIKE '%USDT'
'@ | Format-Table -AutoSize

Write-Host "=== mid rows (1k ~ 10k) for review ===" -ForegroundColor Cyan
Q @'
SELECT Id, Symbol, Side, Strategy, CONVERT(varchar(16), EntryTime, 120) Ent,
  CAST(EntryPrice AS DECIMAL(20,6)) EnP, CAST(ExitPrice AS DECIMAL(20,6)) ExP,
  CAST(Quantity AS DECIMAL(20,4)) Qty, CAST(PnL AS DECIMAL(14,2)) PnL
FROM TradeHistory WHERE ABS(PnL) >= 1000 AND ABS(PnL) < 1000000 ORDER BY ABS(PnL) DESC
'@ | Format-Table -AutoSize

Write-Host "=== backup + delete the >=1M rows ===" -ForegroundColor Cyan
$b = Q "SELECT COUNT(*) c FROM sys.tables WHERE name='TradeHistory_NonUsdt_Backup2'"
if ($b.c -gt 0) { E "DROP TABLE TradeHistory_NonUsdt_Backup2" | Out-Null }
E "SELECT * INTO TradeHistory_NonUsdt_Backup2 FROM TradeHistory WHERE Symbol NOT LIKE '%USDT'" | Out-Null
$bc = Q "SELECT COUNT(*) c FROM TradeHistory_NonUsdt_Backup2"
Write-Host "  backed up: $($bc.c)"
$d = E "DELETE FROM TradeHistory WHERE Symbol NOT LIKE '%USDT'"
Write-Host "  deleted: $d"

Write-Host "`n=== VERIFY — all-time ===" -ForegroundColor Cyan
Q @'
SELECT COUNT(*) N, SUM(CASE WHEN PnL>0 THEN 1 ELSE 0 END) W,
  CAST(SUM(PnL) AS DECIMAL(14,2)) Net,
  CAST(MIN(PnL) AS DECIMAL(14,2)) Worst, CAST(MAX(PnL) AS DECIMAL(14,2)) Best
FROM TradeHistory WHERE IsClosed=1
'@ | Format-Table -AutoSize

Write-Host "=== VERIFY — 24h / 7d ===" -ForegroundColor Cyan
Q @'
SELECT '24h' Period, COUNT(*) N, SUM(CASE WHEN PnL>0 THEN 1 ELSE 0 END) W, CAST(SUM(PnL) AS DECIMAL(12,2)) Net
FROM TradeHistory WHERE IsClosed=1 AND EntryTime > DATEADD(HOUR,-24,DATEADD(HOUR,9,GETUTCDATE()))
UNION ALL
SELECT '7d', COUNT(*), SUM(CASE WHEN PnL>0 THEN 1 ELSE 0 END), CAST(SUM(PnL) AS DECIMAL(12,2))
FROM TradeHistory WHERE IsClosed=1 AND EntryTime > DATEADD(DAY,-7,DATEADD(HOUR,9,GETUTCDATE()))
'@ | Format-Table -AutoSize
