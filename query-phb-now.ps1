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

# Binance API
$cmdU = $cn.CreateCommand(); $cmdU.CommandText = "SELECT BinanceApiKey, BinanceApiSecret FROM Users WHERE Id=1"
$rdr = $cmdU.ExecuteReader(); $apiKey=""; $apiSecret=""
if ($rdr.Read()) { $apiKey = AesDecrypt $rdr["BinanceApiKey"]; $apiSecret = AesDecrypt $rdr["BinanceApiSecret"] }
$rdr.Close()

function Sig($q, $s) { $h = New-Object System.Security.Cryptography.HMACSHA256; $h.Key = [Text.Encoding]::UTF8.GetBytes($s); [BitConverter]::ToString($h.ComputeHash([Text.Encoding]::UTF8.GetBytes($q))).Replace("-","").ToLower() }
function BnGet($ep, $p) {
    $ts = [DateTimeOffset]::UtcNow.ToUnixTimeMilliseconds()
    $qs = if ([string]::IsNullOrEmpty($p)) { "timestamp=$ts" } else { "$p&timestamp=$ts" }
    $sig = Sig $qs $apiSecret
    $url = "https://fapi.binance.com$ep" + "?" + $qs + "&signature=" + $sig
    try { return Invoke-RestMethod -Uri $url -Headers @{ "X-MBX-APIKEY" = $apiKey } -Method Get -TimeoutSec 15 } catch { Write-Host "ERR: $_" -ForegroundColor Red; return $null }
}

Write-Host "=== PHBUSDT TradeHistory (최근 5건) ===" -ForegroundColor Cyan
$cmd = $cn.CreateCommand()
$cmd.CommandText = @"
SELECT TOP 5 Id, EntryTime, ExitTime, Side, Strategy, Category, EntryPrice, Quantity, ExitPrice, PnL, PnLPercent, ExitReason, IsClosed
FROM TradeHistory WHERE Symbol='PHBUSDT' ORDER BY Id DESC
"@
$cmd.CommandTimeout = 30
$rdr = $cmd.ExecuteReader()
while ($rdr.Read()) {
    $row = @{}
    for ($i=0; $i -lt $rdr.FieldCount; $i++) {
        $val = if ($rdr.IsDBNull($i)) { "(null)" } else { $rdr.GetValue($i) }
        $row[$rdr.GetName($i)] = $val
    }
    Write-Host ("Id={0} Entry={1} Exit={2} Side={3} Strat={4} EP={5} Qty={6} XP={7} PnL={8} PnLPct={9}% Reason={10} Closed={11}" -f $row.Id, $row.EntryTime, $row.ExitTime, $row.Side, $row.Strategy, $row.EntryPrice, $row.Quantity, $row.ExitPrice, $row.PnL, $row.PnLPercent, $row.ExitReason, $row.IsClosed)
}
$rdr.Close()

Write-Host ""
Write-Host "=== PHBUSDT PositionState ===" -ForegroundColor Cyan
$cmd = $cn.CreateCommand(); $cmd.CommandText = "SELECT * FROM PositionState WHERE Symbol='PHBUSDT'"; $cmd.CommandTimeout = 10
$rdr = $cmd.ExecuteReader()
while ($rdr.Read()) {
    for ($i=0; $i -lt $rdr.FieldCount; $i++) {
        $val = if ($rdr.IsDBNull($i)) { "(null)" } else { $rdr.GetValue($i) }
        Write-Host ("  {0} = {1}" -f $rdr.GetName($i), $val)
    }
}
$rdr.Close()

Write-Host ""
Write-Host "=== Binance 현재 PHBUSDT 포지션 ===" -ForegroundColor Cyan
$pos = BnGet "/fapi/v2/positionRisk" "symbol=PHBUSDT"
if ($null -ne $pos) {
    foreach ($p in $pos) {
        if ([double]$p.positionAmt -ne 0) {
            Write-Host ("  Sym={0} Amt={1} Entry={2} Mark={3} PnL={4} Lev={5} Liq={6}" -f $p.symbol, $p.positionAmt, $p.entryPrice, $p.markPrice, $p.unRealizedProfit, $p.leverage, $p.liquidationPrice)
        }
    }
}

Write-Host ""
Write-Host "=== Binance PHBUSDT 열린 주문 (SL/TP/Trailing) ===" -ForegroundColor Cyan
$ord = BnGet "/fapi/v1/openOrders" "symbol=PHBUSDT"
if ($null -ne $ord -and $ord.Count -gt 0) {
    foreach ($o in $ord) {
        Write-Host ("  Id={0} Type={1} Side={2} Qty={3} Stop={4} Trigger={5} ReduceOnly={6}" -f $o.orderId, $o.type, $o.side, $o.origQty, $o.stopPrice, $o.activatePrice, $o.reduceOnly)
    }
} else { Write-Host "  ❌ 열린 주문 없음 (SL/TP/Trailing 모두 미등록 상태)" -ForegroundColor Red }

Write-Host ""
Write-Host "=== FooterLogs PHBUSDT 진입 직후 + 최근 (각각 30건) ===" -ForegroundColor Cyan
$cmd = $cn.CreateCommand()
$cmd.CommandText = @"
SELECT TOP 30 [Timestamp], LEFT(Message,200) AS Msg
FROM FooterLogs WHERE Message LIKE '%PHBUSDT%' ORDER BY Id DESC
"@
$cmd.CommandTimeout = 30
$rdr = $cmd.ExecuteReader()
while ($rdr.Read()) {
    Write-Host ("  [{0}] {1}" -f $rdr.GetDateTime(0).ToString("HH:mm:ss"), $rdr.GetString(1))
}
$rdr.Close()

$cn.Close()
