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

$cmdU = $cn.CreateCommand(); $cmdU.CommandText = "SELECT BinanceApiKey, BinanceApiSecret FROM Users WHERE Id=1"
$rdr = $cmdU.ExecuteReader(); $apiKey=""; $apiSecret=""
if ($rdr.Read()) { $apiKey = AesDecrypt $rdr["BinanceApiKey"]; $apiSecret = AesDecrypt $rdr["BinanceApiSecret"] }
$rdr.Close()

function Sig($q, $s) { $h = New-Object System.Security.Cryptography.HMACSHA256; $h.Key = [Text.Encoding]::UTF8.GetBytes($s); [BitConverter]::ToString($h.ComputeHash([Text.Encoding]::UTF8.GetBytes($q))).Replace("-","").ToLower() }
function BnGet($ep, $p) {
    $ts = [DateTimeOffset]::UtcNow.ToUnixTimeMilliseconds()
    $qs = if ([string]::IsNullOrEmpty($p)) { "timestamp=$ts" } else { "$p&timestamp=$ts" }
    $sig = Sig $qs $apiSecret
    try { return Invoke-RestMethod -Uri ("https://fapi.binance.com$ep" + "?" + $qs + "&signature=" + $sig) -Headers @{ "X-MBX-APIKEY" = $apiKey } -Method Get -TimeoutSec 15 } catch { return $null }
}

Write-Host "==== [1] TradeHistory closed since 12:00 today ===="
$c = $cn.CreateCommand()
$c.CommandText = "SELECT TOP 30 Symbol, Side, Strategy, PnL, PnLPercent, ExitReason, ExitTime FROM TradeHistory WHERE UserId=1 AND IsClosed=1 AND ExitTime >= '2026-04-20 12:00:00' ORDER BY ExitTime DESC"
$c.CommandTimeout = 30
$r = $c.ExecuteReader()
$closeCount = 0
while ($r.Read()) {
    $closeCount++
    $sym = $r.GetString(0)
    $reason = if ($r.IsDBNull(5)) { "(null)" } else { $r.GetString(5) }
    $pnl = if ($r.IsDBNull(3)) { 0 } else { [decimal]$r.GetValue(3) }
    $et = if ($r.IsDBNull(6)) { "?" } else { $r.GetDateTime(6).ToString("HH:mm:ss") }
    Write-Host ("  [" + $et + "] " + $sym + " PnL=" + $pnl + " reason=" + $reason)
}
$r.Close()
Write-Host ("  Total closed since 12:00 = " + $closeCount)

Write-Host ""
Write-Host "==== [2] Binance active positions ===="
$pos = BnGet "/fapi/v2/positionRisk" ""
$activeList = @()
if ($null -ne $pos) {
    $activeList = @($pos | Where-Object { [double]$_.positionAmt -ne 0 })
    if ($activeList.Count -eq 0) { Write-Host "  (no active positions)" }
    foreach ($p in $activeList) {
        Write-Host ("  " + $p.symbol + " amt=" + $p.positionAmt + " entry=" + $p.entryPrice + " mark=" + $p.markPrice + " pnl=" + $p.unRealizedProfit + " lev=" + $p.leverage + " liq=" + $p.liquidationPrice)
    }
}

Write-Host ""
Write-Host "==== [3] Binance open conditional orders ===="
$ord = BnGet "/fapi/v1/openOrders" ""
$ordList = @()
if ($null -ne $ord) {
    $ordList = @($ord)
    if ($ordList.Count -eq 0) { Write-Host "  XXXX No open conditional orders (SL/TP/Trailing)" -ForegroundColor Red }
    foreach ($o in $ordList) {
        Write-Host ("  " + $o.symbol + " type=" + $o.type + " side=" + $o.side + " qty=" + $o.origQty + " stopPrice=" + $o.stopPrice + " reduceOnly=" + $o.reduceOnly)
    }
}

Write-Host ""
Write-Host "==== [4] Position vs Orders match ===="
foreach ($p in $activeList) {
    $sym = [string]$p.symbol
    $perSym = @($ordList | Where-Object { $_.symbol -eq $sym })
    $sl = @($perSym | Where-Object { $_.type -eq "STOP_MARKET" -or $_.type -eq "STOP" }).Count
    $tp = @($perSym | Where-Object { $_.type -eq "TAKE_PROFIT_MARKET" -or $_.type -eq "TAKE_PROFIT" }).Count
    $tr = @($perSym | Where-Object { $_.type -eq "TRAILING_STOP_MARKET" }).Count
    $status = if ($sl -eq 0 -and $tp -eq 0 -and $tr -eq 0) { "NONE" }
              elseif ($sl -gt 0 -and ($tp -gt 0 -or $tr -gt 0)) { "OK" }
              else { "PARTIAL" }
    Write-Host ("  " + $sym + " SL=" + $sl + " TP=" + $tp + " TR=" + $tr + " [" + $status + "]")
}

Write-Host ""
Write-Host "==== [5] FooterLogs telegram/API-fill logs since 12:00 ===="
$c = $cn.CreateCommand()
$c.CommandText = "SELECT TOP 20 [Timestamp], LEFT(Message, 220) AS Msg FROM FooterLogs WHERE [Timestamp] >= '2026-04-20 12:00:00' AND (Message LIKE '%ORDER_FILL%' OR Message LIKE '%API 체결%' OR Message LIKE '%텔레그램%' OR Message LIKE '%Telegram%' OR Message LIKE '%[OrderLifecycle]%' OR Message LIKE '%외부포지션%') ORDER BY Id DESC"
$c.CommandTimeout = 30
$r = $c.ExecuteReader()
$count = 0
while ($r.Read()) {
    $count++
    Write-Host ("  [" + $r.GetDateTime(0).ToString("HH:mm:ss") + "] " + $r.GetString(1))
}
$r.Close()
if ($count -eq 0) { Write-Host "  (no such logs found)" -ForegroundColor Red }

$cn.Close()
