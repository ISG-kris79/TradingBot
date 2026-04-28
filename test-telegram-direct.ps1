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
$json = Get-Content "$PSScriptRoot\appsettings.json" -Raw | ConvertFrom-Json
$connStr = AesDecrypt $json.ConnectionStrings.DefaultConnection
$cn = New-Object System.Data.SqlClient.SqlConnection $connStr; $cn.Open()
$cm = $cn.CreateCommand(); $cm.CommandText = "SELECT TelegramBotToken, TelegramChatId FROM Users WHERE Id=1"
$rd = $cm.ExecuteReader()
$token = ""; $chatId = ""
if ($rd.Read()) { $token = AesDecrypt $rd["TelegramBotToken"]; $chatId = AesDecrypt $rd["TelegramChatId"] }
$rd.Close(); $cn.Close()

Write-Host "Token: $($token.Substring(0,15))..."
Write-Host "ChatId: $chatId"

$msg = "[v5.10.91 TEST] Direct API call at " + (Get-Date -Format 'HH:mm:ss')
$url = "https://api.telegram.org/bot$token/sendMessage"
$body = @{ chat_id = $chatId; text = $msg } | ConvertTo-Json
try {
    $resp = Invoke-RestMethod -Uri $url -Method Post -Body $body -ContentType "application/json; charset=utf-8" -TimeoutSec 20
    Write-Host "OK: ok=$($resp.ok) msgId=$($resp.result.message_id)"
}
catch {
    Write-Host "FAIL: $($_.Exception.Message)"
    if ($_.ErrorDetails) { Write-Host $_.ErrorDetails.Message }
}
