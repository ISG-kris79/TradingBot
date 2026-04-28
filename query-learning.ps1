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

Write-Host "=== [1] 최근 학습 로그 ===" -ForegroundColor Cyan
Q "SELECT TOP 15 Timestamp, LEFT(Message, 250) AS Msg FROM FooterLogs WHERE Message LIKE '%학습%' OR Message LIKE '%Train%' OR Message LIKE '%PumpML%' OR Message LIKE '%Forecaster%' ORDER BY Id DESC" | Format-Table -AutoSize

Write-Host "=== [2] 최근 30분 로그 (아무거나) ===" -ForegroundColor Cyan
Q "SELECT TOP 10 Timestamp, LEFT(Message, 150) AS Msg FROM FooterLogs WHERE Timestamp >= DATEADD(MINUTE, -30, GETDATE()) ORDER BY Id DESC" | Format-Table -AutoSize

Write-Host "=== [3] 마지막 로그 시각 ===" -ForegroundColor Cyan
Q "SELECT TOP 1 Timestamp FROM FooterLogs ORDER BY Id DESC" | Format-Table -AutoSize

Write-Host "=== [4] initial_training_ready.flag 확인 ===" -ForegroundColor Cyan
if (Test-Path "$env:LOCALAPPDATA\TradingBot\Models\initial_training_ready.flag") {
    Write-Host "  FLAG 존재 (학습 완료 상태)" -ForegroundColor Green
} else {
    Write-Host "  FLAG 없음 (초기학습 필요)" -ForegroundColor Yellow
}

Write-Host "=== [5] 모델 파일 목록 ===" -ForegroundColor Cyan
Get-ChildItem "$env:LOCALAPPDATA\TradingBot\Models\" -ErrorAction SilentlyContinue | Select-Object Name, Length, LastWriteTime | Format-Table -AutoSize
