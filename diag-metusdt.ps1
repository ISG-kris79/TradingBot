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

$cn = New-Object System.Data.SqlClient.SqlConnection (AesDecrypt (Get-Content "$PSScriptRoot\appsettings.json" -Raw | ConvertFrom-Json).ConnectionStrings.DefaultConnection); $cn.Open()
$cm = $cn.CreateCommand(); $cm.CommandText = "SELECT BinanceApiKey, BinanceApiSecret FROM Users WHERE Id=1"
$rd = $cm.ExecuteReader()
$apiKey = ""; $apiSecret = ""
if ($rd.Read()) { $apiKey = AesDecrypt $rd["BinanceApiKey"]; $apiSecret = AesDecrypt $rd["BinanceApiSecret"] }
$rd.Close(); $cn.Close()

function Get-Sig($q, $s) {
    $h = New-Object System.Security.Cryptography.HMACSHA256
    $h.Key = [Text.Encoding]::UTF8.GetBytes($s)
    return ([BitConverter]::ToString($h.ComputeHash([Text.Encoding]::UTF8.GetBytes($q))).Replace("-","").ToLower())
}
function Get-BF($endpoint, $extra) {
    $ts = [DateTimeOffset]::UtcNow.ToUnixTimeMilliseconds()
    $qs = "timestamp=$ts"
    if ($extra) { $qs = "$extra&$qs" }
    $sig = Get-Sig $qs $apiSecret
    $url = "https://fapi.binance.com$endpoint" + "?" + "$qs&signature=$sig"
    try { return Invoke-RestMethod -Uri $url -Headers @{"X-MBX-APIKEY"=$apiKey} -Method Get -TimeoutSec 15 } catch { return $null }
}

Write-Host "=== [1] METUSDT TradeHistory (all-time) ==="
$sql1 = "SELECT TOP 40 Strategy, CAST(EntryPrice AS DECIMAL(18,8)) AS Entry, CAST(ExitPrice AS DECIMAL(18,8)) AS Exit_, CAST(Quantity AS DECIMAL(18,4)) AS Qty, CAST(EntryPrice*Quantity AS DECIMAL(18,2)) AS Notional, CAST(PnL AS DECIMAL(18,4)) AS PnL, CAST(PnLPercent AS DECIMAL(8,2)) AS RoiPct, holdingMinutes AS Hold, ExitReason, IsClosed, EntryTime, ExitTime FROM TradeHistory WHERE Symbol='METUSDT' AND UserId=1 ORDER BY EntryTime DESC"
Q $sql1 | Format-Table -AutoSize -Wrap

Write-Host "=== [2] M-something pattern ==="
$sql2 = "SELECT DISTINCT Symbol FROM TradeHistory WHERE Symbol LIKE '%MET%' OR Symbol LIKE 'M%USDT' ORDER BY Symbol"
Q $sql2 | Format-Table -AutoSize

Write-Host "=== [3] Binance positionRisk - METUSDT ==="
$pos = Get-BF "/fapi/v2/positionRisk" ""
if ($pos) { $pos | Where-Object { $_.symbol -like "*MET*" -or $_.symbol -like "M*USDT" -and [double]$_.positionAmt -ne 0 } | Select-Object symbol, positionAmt, entryPrice, markPrice, leverage, unRealizedProfit, isolatedMargin | Format-Table -AutoSize }

Write-Host "=== [4] Binance algoOrders - METUSDT ==="
$a = Get-BF "/fapi/v1/openAlgoOrders" "symbol=METUSDT"
if ($a) { $a | Select-Object algoType, orderType, side, quantity, triggerPrice, activatePrice, callbackRate, reduceOnly, algoStatus | Format-Table -AutoSize }

Write-Host "=== [5] Active positions full list ==="
if ($pos) { $pos | Where-Object { [double]$_.positionAmt -ne 0 } | Select-Object symbol, positionAmt, entryPrice, markPrice, @{N='Lev';E={$_.leverage}}, @{N='UnPnL';E={$_.unRealizedProfit}} | Format-Table -AutoSize }
