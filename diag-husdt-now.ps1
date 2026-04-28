$ErrorActionPreference = "Stop"
[Console]::OutputEncoding = [Text.Encoding]::UTF8
Start-Sleep -Seconds 3
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
function E($sql) {
    $cn = New-Object System.Data.SqlClient.SqlConnection (AesDecrypt (Get-Content "$PSScriptRoot\appsettings.json" -Raw | ConvertFrom-Json).ConnectionStrings.DefaultConnection); $cn.Open()
    $cm = $cn.CreateCommand(); $cm.CommandText = $sql; $cm.CommandTimeout = 30
    $r = $cm.ExecuteNonQuery(); $cn.Close(); return $r
}

$json = Get-Content "$PSScriptRoot\appsettings.json" -Raw | ConvertFrom-Json
$connStr = AesDecrypt $json.ConnectionStrings.DefaultConnection
$cn = New-Object System.Data.SqlClient.SqlConnection $connStr; $cn.Open()
$cm = $cn.CreateCommand(); $cm.CommandText = "SELECT BinanceApiKey, BinanceApiSecret FROM Users WHERE Id=1"
$rd = $cm.ExecuteReader(); $apiKey=""; $apiSecret=""
if ($rd.Read()) { $apiKey = AesDecrypt $rd["BinanceApiKey"]; $apiSecret = AesDecrypt $rd["BinanceApiSecret"] }
$rd.Close(); $cn.Close()

function Get-Sig($q,$s) { $h=New-Object System.Security.Cryptography.HMACSHA256; $h.Key=[Text.Encoding]::UTF8.GetBytes($s); return ([BitConverter]::ToString($h.ComputeHash([Text.Encoding]::UTF8.GetBytes($q))).Replace("-","").ToLower()) }
function Get-BF($endpoint,$extra) {
    $ts=[DateTimeOffset]::UtcNow.ToUnixTimeMilliseconds()
    $qs="timestamp=$ts"; if($extra){$qs="$extra&$qs"}
    $sig=Get-Sig $qs $apiSecret
    $url="https://fapi.binance.com$endpoint"+"?"+"$qs&signature=$sig"
    try { return Invoke-RestMethod -Uri $url -Headers @{"X-MBX-APIKEY"=$apiKey} -Method Get -TimeoutSec 15 } catch { Write-Host "API err: $($_.Exception.Message)" -ForegroundColor Red; return $null }
}

Write-Host "==== HUSDT Binance 현재 상태 ===="
$pos = Get-BF "/fapi/v2/positionRisk" "symbol=HUSDT"
$binanceAmt = 0
if ($pos) {
    $binanceAmt = [double]$pos[0].positionAmt
    $pos | Select-Object symbol, positionAmt, entryPrice, markPrice, leverage, unRealizedProfit | Format-List
}

Write-Host "==== UBUSDT Binance 현재 상태 ===="
$posU = Get-BF "/fapi/v2/positionRisk" "symbol=UBUSDT"
$binanceU = 0
if ($posU) {
    $binanceU = [double]$posU[0].positionAmt
    $posU | Select-Object symbol, positionAmt, entryPrice, markPrice, leverage, unRealizedProfit | Format-List
}

Write-Host "==== DB TradeHistory IsClosed=0 (고아 가능성) ===="
$orphan = "SELECT Id, Symbol, EntryTime, Strategy, Side, Quantity, EntryPrice, PnL, ExitTime, ExitReason, IsClosed FROM TradeHistory WHERE UserId=1 AND IsClosed=0 ORDER BY EntryTime DESC"
Q $orphan | Format-Table -AutoSize

Write-Host "==== 분석 ===="
if ($binanceAmt -eq 0) {
    Write-Host "✅ HUSDT Binance 실제 포지션 = 0" -ForegroundColor Green
    Write-Host "   → DB IsClosed=0 레코드는 고아 (UI 유령). 정리 대상." -ForegroundColor Yellow
} else {
    Write-Host "⚠️ HUSDT Binance 실제 포지션 = $binanceAmt (열려있음)" -ForegroundColor Yellow
}
if ($binanceU -eq 0) {
    Write-Host "✅ UBUSDT Binance 실제 포지션 = 0 (청산됨)" -ForegroundColor Green
} else {
    Write-Host "⚠️ UBUSDT Binance 실제 포지션 = $binanceU" -ForegroundColor Yellow
}
