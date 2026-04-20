$ErrorActionPreference = "Stop"
$AesKey = [byte[]](0x43, 0x6F, 0x69, 0x6E, 0x46, 0x46, 0x2D, 0x54, 0x72, 0x61, 0x64, 0x69, 0x6E, 0x67, 0x42, 0x6F, 0x74, 0x2D, 0x41, 0x45, 0x53, 0x32, 0x35, 0x36, 0x2D, 0x4B, 0x65, 0x79, 0x2D, 0x33, 0x32, 0x42)
function AesDecrypt($enc) {
    $f = [Convert]::FromBase64String($enc)
    $a = [System.Security.Cryptography.Aes]::Create(); $a.Key = $AesKey
    $iv = New-Object byte[] $a.IV.Length; $c = New-Object byte[] ($f.Length - $a.IV.Length)
    [Buffer]::BlockCopy($f, 0, $iv, 0, $a.IV.Length); [Buffer]::BlockCopy($f, $a.IV.Length, $c, 0, $c.Length)
    $a.IV = $iv; $d = $a.CreateDecryptor($a.Key, $a.IV)
    $s = [Text.Encoding]::UTF8.GetString($d.TransformFinalBlock($c, 0, $c.Length)); $a.Dispose(); $d.Dispose(); return $s
}
$json = Get-Content "$PSScriptRoot\appsettings.json" -Raw | ConvertFrom-Json
$cn = New-Object System.Data.SqlClient.SqlConnection (AesDecrypt $json.ConnectionStrings.DefaultConnection); $cn.Open()

Write-Host "==== Dashboard related logs in last hour ===="
$cmd = $cn.CreateCommand()
$cmd.CommandText = "SELECT TOP 30 [Timestamp], LEFT(Message, 220) AS Msg FROM FooterLogs WHERE [Timestamp] >= DATEADD(HOUR, -1, GETDATE()) AND (Message LIKE '%Dashboard%' OR Message LIKE '%대시보드%' OR Message LIKE '%총자산%' OR Message LIKE '%Wallet%' OR Message LIKE '%NaN%' OR Message LIKE '%Infinity%') ORDER BY Id DESC"
$cmd.CommandTimeout = 30
$r = $cmd.ExecuteReader()
$count = 0
while ($r.Read()) {
    $count++
    Write-Host ("  [" + $r.GetDateTime(0).ToString("HH:mm:ss") + "] " + $r.GetString(1))
}
$r.Close()
if ($count -eq 0) { Write-Host "  (no dashboard logs in last hour)" -ForegroundColor Red }
Write-Host "  Total: $count"

Write-Host ""
Write-Host "==== Engine start / main loop logs (last hour) ===="
$cmd2 = $cn.CreateCommand()
$cmd2.CommandText = "SELECT TOP 10 [Timestamp], LEFT(Message, 220) AS Msg FROM FooterLogs WHERE [Timestamp] >= DATEADD(HOUR, -1, GETDATE()) AND (Message LIKE '%엔진%' OR Message LIKE '%메인 스캔%' OR Message LIKE '%메인 루프%' OR Message LIKE '%Bot is alive%' OR Message LIKE '%Heartbeat%') ORDER BY Id DESC"
$cmd2.CommandTimeout = 30
$r2 = $cmd2.ExecuteReader()
while ($r2.Read()) {
    Write-Host ("  [" + $r2.GetDateTime(0).ToString("HH:mm:ss") + "] " + $r2.GetString(1))
}
$r2.Close()

Write-Host ""
Write-Host "==== Latest 20 logs overall ===="
$cmd3 = $cn.CreateCommand()
$cmd3.CommandText = "SELECT TOP 20 [Timestamp], LEFT(Message, 200) AS Msg FROM FooterLogs ORDER BY Id DESC"
$cmd3.CommandTimeout = 30
$r3 = $cmd3.ExecuteReader()
while ($r3.Read()) {
    Write-Host ("  [" + $r3.GetDateTime(0).ToString("HH:mm:ss") + "] " + $r3.GetString(1))
}
$r3.Close()

$cn.Close()
