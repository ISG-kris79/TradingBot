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
    $cm = $cn.CreateCommand(); $cm.CommandText = $sql; $cm.CommandTimeout = 60
    $ap = New-Object System.Data.SqlClient.SqlDataAdapter $cm; $ds = New-Object System.Data.DataSet; [void]$ap.Fill($ds); $cn.Close()
    return $ds.Tables[0]
}
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8

Write-Host "==== A. Bot process ====" -ForegroundColor Cyan
Get-Process TradingBot -ErrorAction SilentlyContinue | Select-Object Id, StartTime, @{N='RunMin';E={[int]((Get-Date) - $_.StartTime).TotalMinutes}}, @{N='WorkMB';E={[int]($_.WorkingSet64/1MB)}} | Format-Table -AutoSize

Write-Host ""
Write-Host "==== B. Bot_Log last 5 rows (any time) ====" -ForegroundColor Cyan
Q "SELECT TOP 5 EventTime, Symbol, Allowed, Reason FROM Bot_Log ORDER BY EventTime DESC" | Format-Table -AutoSize -Wrap

Write-Host ""
Write-Host "==== C. FooterLogs last 30 rows ====" -ForegroundColor Cyan
Q "SELECT TOP 30 Timestamp, LEFT(Message,200) AS Msg FROM FooterLogs ORDER BY Id DESC" | Format-Table -AutoSize -Wrap

Write-Host ""
Write-Host "==== D. Open positions now ====" -ForegroundColor Cyan
Q "SELECT UserId, Symbol, EntryPrice, EntryTime, OpenStatus FROM PositionState WHERE OpenStatus=1" | Format-Table -AutoSize -Wrap

Write-Host ""
Write-Host "==== E. Last TradeHistory entries (today) ====" -ForegroundColor Cyan
Q "SELECT TOP 10 UserId, Symbol, Category, EntryTime, ExitTime, PnLUsd FROM TradeHistory WHERE EntryTime >= CAST(GETDATE() AS DATE) OR ExitTime >= CAST(GETDATE() AS DATE) ORDER BY ISNULL(ExitTime, EntryTime) DESC" | Format-Table -AutoSize -Wrap

Write-Host ""
Write-Host "==== F. FooterLogs ENTRY/GATE/SIGNAL last 3h ====" -ForegroundColor Cyan
Q @"
SELECT TOP 30 Timestamp, LEFT(Message,250) AS Msg
FROM FooterLogs
WHERE Timestamp >= DATEADD(HOUR,-3,GETDATE())
  AND (Message LIKE '%[GATE]%' OR Message LIKE '%ENTRY%' OR Message LIKE '%SIGNAL%' OR Message LIKE '%PUMP%' OR Message LIKE '%MAJOR%' OR Message LIKE '%SQUEEZE%')
ORDER BY Id DESC
"@ | Format-Table -AutoSize -Wrap
