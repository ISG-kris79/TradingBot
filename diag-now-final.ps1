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
    $cm = $cn.CreateCommand(); $cm.CommandText = $sql; $cm.CommandTimeout = 15
    $ap = New-Object System.Data.SqlClient.SqlDataAdapter $cm; $ds = New-Object System.Data.DataSet; [void]$ap.Fill($ds); $cn.Close()
    return $ds.Tables[0]
}
[Console]::OutputEncoding = [Text.Encoding]::UTF8

$p = Get-Process TradingBot -ErrorAction SilentlyContinue
if (-not $p) { Write-Host "❌ BOT NOT RUNNING" -ForegroundColor Red; exit }

Write-Host "==== A. Bot 상태 ====" -ForegroundColor Cyan
$startTime = $p.StartTime
$p | Select-Object Id, @{N='StartTime';E={$_.StartTime}}, @{N='RunSec';E={[int]((Get-Date)-$_.StartTime).TotalSeconds}}, @{N='WorkMB';E={[int]($_.WorkingSet64/1MB)}}, @{N='CPU';E={[int]$_.CPU}} | Format-Table -AutoSize

$v = "$env:LOCALAPPDATA\TradingBot\current\sq.version"
if (Test-Path $v) { (Get-Content $v -Raw) -split "`n" | Where-Object { $_ -match "<version>" } | Write-Host }

Write-Host ""
Write-Host "==== B. 봇 시작 후 첫 50건 로그 ====" -ForegroundColor Cyan
$startStr = $startTime.AddSeconds(-5).ToString("yyyy-MM-dd HH:mm:ss")
Q "SELECT TOP 50 Timestamp, LEFT(Message,200) AS Msg FROM FooterLogs WHERE Timestamp >= '$startStr' ORDER BY Id ASC" | Format-Table -AutoSize -Wrap

Write-Host ""
Write-Host "==== C. WebSocket 연결 성공/실패 키워드 (시작 후) ====" -ForegroundColor Cyan
Q @"
SELECT TOP 30 Timestamp, LEFT(Message,200) AS Msg
FROM FooterLogs WHERE Timestamp >= '$startStr'
  AND (Message LIKE '%연결%' OR Message LIKE '%복구%' OR Message LIKE '%Connect%' OR Message LIKE '%API key%' OR Message LIKE '%Signature%' OR Message LIKE '%401%' OR Message LIKE '%403%' OR Message LIKE '%200%')
ORDER BY Id DESC
"@ | Format-Table -AutoSize -Wrap

Write-Host ""
Write-Host "==== D. ProcessTickerChannel + UpdateTicker 호출 흔적 ====" -ForegroundColor Cyan
Q @"
SELECT TOP 5 Timestamp, LEFT(Message,200) AS Msg
FROM FooterLogs WHERE Timestamp >= '$startStr'
  AND (Message LIKE '%Ticker%' OR Message LIKE '%실시간%' OR Message LIKE '%가격%')
ORDER BY Id DESC
"@ | Format-Table -AutoSize -Wrap
