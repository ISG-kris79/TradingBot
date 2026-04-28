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

Write-Host "==== [1] HUSDT Binance positionRisk (REAL) ===="
$pos = Get-BF "/fapi/v2/positionRisk" "symbol=HUSDT"
if ($pos) {
    $pos | Select-Object symbol, positionAmt, entryPrice, markPrice, leverage, unRealizedProfit, isolatedMargin | Format-List
}

Write-Host ""
Write-Host "==== [2] HUSDT Binance openOrders + algoOrders ===="
$o = Get-BF "/fapi/v1/openOrders" "symbol=HUSDT"
if ($o) { $o | Select-Object orderId, type, side, origQty, price, stopPrice, reduceOnly, status | Format-Table -AutoSize }
$algo = Get-BF "/fapi/v1/openAlgoOrders" "symbol=HUSDT"
if ($algo) { $algo | Format-Table -AutoSize } else { Write-Host "  algoOrder NONE" }

Write-Host ""
Write-Host "==== [3] HUSDT DB TradeHistory IsClosed=0 (활성) ===="
$h1 = "SELECT Id, EntryTime, Strategy, Side, EntryPrice, Quantity, PnL, PnLPercent, ExitTime, ExitReason, IsClosed FROM TradeHistory WHERE Symbol='HUSDT' AND UserId=1 AND IsClosed=0 ORDER BY EntryTime DESC"
Q $h1 | Format-Table -AutoSize -Wrap

Write-Host ""
Write-Host "==== [4] HUSDT DB TradeHistory 최근 10건 (전체) ===="
$h2 = "SELECT TOP 10 Id, EntryTime, Strategy, Side, EntryPrice, Quantity, PnL, PnLPercent, ExitTime, ExitReason, IsClosed FROM TradeHistory WHERE Symbol='HUSDT' AND UserId=1 ORDER BY EntryTime DESC"
Q $h2 | Format-Table -AutoSize -Wrap

Write-Host ""
Write-Host "==== [5] HUSDT 최근 FooterLogs (청산 관련) ===="
$f = @'
SELECT TOP 20 Timestamp AT TIME ZONE 'UTC' AT TIME ZONE 'Korea Standard Time' AS TimeKST,
  LEFT(Message,200) AS Msg
FROM FooterLogs WHERE Timestamp >= DATEADD(HOUR,-6,GETUTCDATE())
  AND Message LIKE '%HUSDT%'
  AND (Message LIKE '%청산%' OR Message LIKE '%CLOSE%' OR Message LIKE '%익절%' OR Message LIKE '%SL%' OR Message LIKE '%TP%' OR Message LIKE '%EXTERNAL%')
ORDER BY Timestamp DESC
'@
Q $f | Format-Table -AutoSize -Wrap
