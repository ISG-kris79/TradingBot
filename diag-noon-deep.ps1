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

$cutoff = "2026-04-23 03:00:00"
Write-Host "=== Since $cutoff UTC (= 12:00 KST) — 12 hours window ==="

Write-Host "`n[A] Bot_Log Allowed=True (entry approved) since noon"
$a = "SELECT TOP 50 EventTime, Symbol, Direction, LEFT(Reason,90) AS Reason FROM Bot_Log WHERE UserId=1 AND Allowed=1 AND EventTime >= '$cutoff' ORDER BY EventTime DESC"
Q $a | Format-Table -AutoSize -Wrap

Write-Host "`n[B] Bot_Log Allowed=False reject reason TOP 20"
$b = "SELECT TOP 20 LEFT(Reason, 80) AS RejectReason, COUNT(*) AS Cnt, COUNT(DISTINCT Symbol) AS UniqSym FROM Bot_Log WHERE UserId=1 AND Allowed=0 AND EventTime >= '$cutoff' GROUP BY LEFT(Reason, 80) ORDER BY Cnt DESC"
Q $b | Format-Table -AutoSize -Wrap

Write-Host "`n[C] Bot_Log total counts"
$c = "SELECT SUM(CASE WHEN Allowed=1 THEN 1 ELSE 0 END) AS AllowedCnt, SUM(CASE WHEN Allowed=0 THEN 1 ELSE 0 END) AS RejectedCnt, COUNT(*) AS TotalSignals FROM Bot_Log WHERE UserId=1 AND EventTime >= '$cutoff'"
Q $c | Format-List

Write-Host "`n[D] FooterLogs - ENTRY GATE BLOCK reasons since noon"
$d = "SELECT TOP 30 RIGHT(LTRIM(SUBSTRING(Message, CHARINDEX('reason=', Message)+7, 100)),100) AS Reason, COUNT(*) AS Cnt FROM FooterLogs WHERE Timestamp >= '$cutoff' AND Message LIKE '%[ENTRY][%][BLOCK]%' GROUP BY RIGHT(LTRIM(SUBSTRING(Message, CHARINDEX('reason=', Message)+7, 100)),100) ORDER BY Cnt DESC"
Q $d | Format-Table -AutoSize -Wrap

Write-Host "`n[E] FooterLogs - 진입 차단 hourly count"
$e = "SELECT DATEPART(HOUR, Timestamp) AS Hr, COUNT(*) AS BlockCnt FROM FooterLogs WHERE Timestamp >= '$cutoff' AND Message LIKE '%BLOCK%' GROUP BY DATEPART(HOUR, Timestamp) ORDER BY Hr"
Q $e | Format-Table -AutoSize

Write-Host "`n[F] FooterLogs - SLOT 차단 (슬롯 포화)"
$f = "SELECT COUNT(*) AS SlotBlock FROM FooterLogs WHERE Timestamp >= '$cutoff' AND (Message LIKE '%SLOT_BLOCK%' OR Message LIKE '%duplicatePosition%' OR Message LIKE '%슬롯%포화%' OR Message LIKE '%슬롯%부족%')"
Q $f | Format-List

Write-Host "`n[G] FooterLogs - BTC 1H downtrend filter logs (v5.10.88)"
$g = "SELECT COUNT(*) AS BtcDownBlock FROM FooterLogs WHERE Timestamp >= '$cutoff' AND Message LIKE '%BTC_1H_DOWNTREND%'"
Q $g | Format-List

Write-Host "`n[H] FooterLogs - PUMP_WATCH 진입 시도 / 차단"
$h = "SELECT TOP 20 Timestamp, LEFT(Message,180) AS Msg FROM FooterLogs WHERE Timestamp >= '$cutoff' AND (Message LIKE '%PUMP_WATCH%' OR Message LIKE '%감시진입%' OR Message LIKE '%pump_entry%') ORDER BY Timestamp DESC"
Q $h | Format-Table -AutoSize -Wrap
