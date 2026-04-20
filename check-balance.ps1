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
$rdr.Close(); $cn.Close()

function Sig($q, $s) { $h = New-Object System.Security.Cryptography.HMACSHA256; $h.Key = [Text.Encoding]::UTF8.GetBytes($s); [BitConverter]::ToString($h.ComputeHash([Text.Encoding]::UTF8.GetBytes($q))).Replace("-","").ToLower() }
function GetReq($ep, $p) {
    $ts = [DateTimeOffset]::UtcNow.ToUnixTimeMilliseconds()
    $qs = if ([string]::IsNullOrEmpty($p)) { "timestamp=$ts" } else { "$p&timestamp=$ts" }
    $sig = Sig $qs $apiSecret
    try { return Invoke-RestMethod -Uri ("https://fapi.binance.com$ep" + "?" + $qs + "&signature=" + $sig) -Headers @{ "X-MBX-APIKEY" = $apiKey } -Method Get -TimeoutSec 15 } catch {
        $er = $_.Exception.Response
        if ($null -ne $er) { $s2 = New-Object System.IO.StreamReader($er.GetResponseStream()); Write-Host ("ERR: " + $s2.ReadToEnd()) -ForegroundColor Red }
        return $null
    }
}

Write-Host "==== /fapi/v2/balance (Binance.Net이 쓰는 엔드포인트) ===="
$bal = GetReq "/fapi/v2/balance" ""
if ($null -ne $bal) {
    foreach ($b in @($bal)) {
        if ([double]$b.balance -ne 0 -or [double]$b.availableBalance -ne 0) {
            Write-Host ("  " + $b.asset + " balance=" + $b.balance + " available=" + $b.availableBalance + " walletBalance=" + $b.walletBalance + " crossUnPnl=" + $b.crossUnPnl)
        }
    }
}

Write-Host ""
Write-Host "==== /fapi/v2/account (전체 계좌 정보) ===="
$acc = GetReq "/fapi/v2/account" ""
if ($null -ne $acc) {
    Write-Host ("  totalWalletBalance=" + $acc.totalWalletBalance)
    Write-Host ("  totalUnrealizedProfit=" + $acc.totalUnrealizedProfit)
    Write-Host ("  totalMarginBalance=" + $acc.totalMarginBalance)
    Write-Host ("  availableBalance=" + $acc.availableBalance)
    Write-Host ("  maxWithdrawAmount=" + $acc.maxWithdrawAmount)
    Write-Host ("  canTrade=" + $acc.canTrade)
    Write-Host ("  canDeposit=" + $acc.canDeposit)
    Write-Host ("  canWithdraw=" + $acc.canWithdraw)
    Write-Host ("  feeTier=" + $acc.feeTier)
    Write-Host ("  accountType=[" + $acc.accountType + "]")
}

Write-Host ""
Write-Host "==== USDT 자산 세부 ===="
if ($null -ne $acc -and $null -ne $acc.assets) {
    $usdt = @($acc.assets | Where-Object { $_.asset -eq "USDT" })
    foreach ($u in $usdt) {
        Write-Host ("  walletBalance=" + $u.walletBalance)
        Write-Host ("  unrealizedProfit=" + $u.unrealizedProfit)
        Write-Host ("  marginBalance=" + $u.marginBalance)
        Write-Host ("  maintMargin=" + $u.maintMargin)
        Write-Host ("  initialMargin=" + $u.initialMargin)
        Write-Host ("  availableBalance=" + $u.availableBalance)
        Write-Host ("  maxWithdrawAmount=" + $u.maxWithdrawAmount)
    }
}
