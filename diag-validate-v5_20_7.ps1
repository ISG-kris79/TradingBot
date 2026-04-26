$ErrorActionPreference = "Stop"
[Console]::OutputEncoding = [Text.Encoding]::UTF8

$AesKey = [byte[]](0x43, 0x6F, 0x69, 0x6E, 0x46, 0x46, 0x2D, 0x54, 0x72, 0x61, 0x64, 0x69, 0x6E, 0x67, 0x42, 0x6F, 0x74, 0x2D, 0x41, 0x45, 0x53, 0x32, 0x35, 0x36, 0x2D, 0x4B, 0x65, 0x79, 0x2D, 0x33, 0x32, 0x42)
function AesDecrypt($enc) {
    $f = [Convert]::FromBase64String($enc)
    $a = [System.Security.Cryptography.Aes]::Create(); $a.Key = $AesKey
    $iv = New-Object byte[] $a.IV.Length
    $c = New-Object byte[] ($f.Length - $a.IV.Length)
    [Buffer]::BlockCopy($f, 0, $iv, 0, $a.IV.Length)
    [Buffer]::BlockCopy($f, $a.IV.Length, $c, 0, $c.Length)
    $a.IV = $iv
    $d = $a.CreateDecryptor($a.Key, $a.IV)
    $s = [Text.Encoding]::UTF8.GetString($d.TransformFinalBlock($c, 0, $c.Length))
    $a.Dispose(); $d.Dispose()
    return $s
}
$json = Get-Content "$PSScriptRoot\appsettings.json" -Raw | ConvertFrom-Json
$cn = New-Object System.Data.SqlClient.SqlConnection (AesDecrypt $json.ConnectionStrings.DefaultConnection)
$cn.Open()

$majors = @('BTCUSDT','ETHUSDT','SOLUSDT','XRPUSDT')

Write-Host "=========================================================="
Write-Host "  v5.20.7 가드 차트데이터 검증 (BacktestDataset 267,999 rows)"
Write-Host "  TP=1.5% / SL=0.7% / 1h window"
Write-Host "=========================================================="

# === [BASELINE] 가드 없음 ===
$cmd = $cn.CreateCommand()
$cmd.CommandTimeout = 60
$cmd.CommandText = @"
SELECT
  Symbol,
  COUNT(*) AS Total,
  SUM(CASE WHEN Label_TP_First=1 THEN 1 ELSE 0 END) AS TP,
  SUM(CASE WHEN Label_SL_First=1 THEN 1 ELSE 0 END) AS SL,
  SUM(CASE WHEN Label_TP_First IS NULL AND Label_SL_First IS NULL THEN 1 ELSE 0 END) AS NEU
FROM dbo.BacktestDataset
GROUP BY Symbol
"@
$rdr = $cmd.ExecuteReader()
$tbl = New-Object System.Data.DataTable; $tbl.Load($rdr)

$bTotal = 0; $bTP = 0; $bSL = 0
foreach ($r in $tbl.Rows) { $bTotal += $r.Total; $bTP += $r.TP; $bSL += $r.SL }
$bDecided = $bTP + $bSL
$bWR = if ($bDecided -gt 0) { $bTP / [double]$bDecided * 100.0 } else { 0 }

Write-Host ""
Write-Host "  [BASELINE — no guards]"
Write-Host ("    rows={0}, TP={1}, SL={2}, neutral={3}" -f $bTotal, $bTP, $bSL, ($bTotal - $bDecided))
Write-Host ("    TP-first win-rate = {0:N2}% (decided basis)" -f $bWR)

# === [GUARD A] ALT_RSI_FALLING_KNIFE: 알트 RSI<30 차단 ===
$majorIn = "'" + ($majors -join "','") + "'"
$cmd2 = $cn.CreateCommand()
$cmd2.CommandTimeout = 60
$cmd2.CommandText = @"
SELECT
  COUNT(*) AS Total,
  SUM(CASE WHEN Label_TP_First=1 THEN 1 ELSE 0 END) AS TP,
  SUM(CASE WHEN Label_SL_First=1 THEN 1 ELSE 0 END) AS SL
FROM dbo.BacktestDataset
WHERE NOT (Symbol NOT IN ($majorIn) AND RSI14 < 30)
"@
$rdr2 = $cmd2.ExecuteReader()
$tbl2 = New-Object System.Data.DataTable; $tbl2.Load($rdr2)
$aT = $tbl2.Rows[0].Total; $aTP = $tbl2.Rows[0].TP; $aSL = $tbl2.Rows[0].SL
$aD = $aTP + $aSL
$aWR = if ($aD -gt 0) { $aTP / [double]$aD * 100.0 } else { 0 }

Write-Host ""
Write-Host "  [GUARD A — alt RSI<30 차단]"
Write-Host ("    blocked = {0} bars ({1:N1}%)" -f ($bTotal - $aT), (($bTotal - $aT) / [double]$bTotal * 100.0))
Write-Host ("    after-guard rows={0}, TP={1}, SL={2}" -f $aT, $aTP, $aSL)
Write-Host ("    TP-first win-rate = {0:N2}%  (Δ = {1:+0.00;-0.00}%)" -f $aWR, ($aWR - $bWR))

# === [GUARD B] Lorentzian proxy: BB Walk + EMA50_15m 필터 ===
# Lorentzian 본체는 C# 엔진이라 SQL로는 proxy 필요.
# 실용 proxy: (Close > EMA20_5m) AND (EMA50_15m IS NULL OR Close > EMA50_15m) → 추세 정렬
# 학습 모델 미사용 시 = 추세 추종 + 변동성 양호 조건 근사
$cmd3 = $cn.CreateCommand()
$cmd3.CommandTimeout = 60
$cmd3.CommandText = @"
SELECT
  COUNT(*) AS Total,
  SUM(CASE WHEN Label_TP_First=1 THEN 1 ELSE 0 END) AS TP,
  SUM(CASE WHEN Label_SL_First=1 THEN 1 ELSE 0 END) AS SL
FROM dbo.BacktestDataset
WHERE ClosePrice > EMA20_5m
  AND (EMA50_15m IS NULL OR ClosePrice > EMA50_15m)
  AND NOT (Symbol NOT IN ($majorIn) AND RSI14 < 30)
"@
$rdr3 = $cmd3.ExecuteReader()
$tbl3 = New-Object System.Data.DataTable; $tbl3.Load($rdr3)
$bT = $tbl3.Rows[0].Total; $bbTP = $tbl3.Rows[0].TP; $bbSL = $tbl3.Rows[0].SL
$bD = $bbTP + $bbSL
$bbWR = if ($bD -gt 0) { $bbTP / [double]$bD * 100.0 } else { 0 }

Write-Host ""
Write-Host "  [GUARD A+B — alt RSI 차단 + 5m/15m EMA 추세 정렬 (Lorentzian proxy)]"
Write-Host ("    blocked = {0} bars ({1:N1}%)" -f ($bTotal - $bT), (($bTotal - $bT) / [double]$bTotal * 100.0))
Write-Host ("    after-guard rows={0}, TP={1}, SL={2}" -f $bT, $bbTP, $bbSL)
Write-Host ("    TP-first win-rate = {0:N2}%  (Δ = {1:+0.00;-0.00}%)" -f $bbWR, ($bbWR - $bWR))

# === [GUARD C] 위 + ATR/Close < 1.5% (변동성 작은 박스 차단) ===
$cmd4 = $cn.CreateCommand()
$cmd4.CommandTimeout = 60
$cmd4.CommandText = @"
SELECT
  COUNT(*) AS Total,
  SUM(CASE WHEN Label_TP_First=1 THEN 1 ELSE 0 END) AS TP,
  SUM(CASE WHEN Label_SL_First=1 THEN 1 ELSE 0 END) AS SL
FROM dbo.BacktestDataset
WHERE ClosePrice > EMA20_5m
  AND (EMA50_15m IS NULL OR ClosePrice > EMA50_15m)
  AND NOT (Symbol NOT IN ($majorIn) AND RSI14 < 30)
  AND ATR14 / NULLIF(ClosePrice, 0) BETWEEN 0.005 AND 0.030
"@
$rdr4 = $cmd4.ExecuteReader()
$tbl4 = New-Object System.Data.DataTable; $tbl4.Load($rdr4)
$cT = $tbl4.Rows[0].Total; $cTP = $tbl4.Rows[0].TP; $cSL = $tbl4.Rows[0].SL
$cD = $cTP + $cSL
$cWR = if ($cD -gt 0) { $cTP / [double]$cD * 100.0 } else { 0 }

Write-Host ""
Write-Host "  [GUARD A+B+C — + ATR/Close 0.5~3% 변동성 적정 필터]"
Write-Host ("    blocked = {0} bars ({1:N1}%)" -f ($bTotal - $cT), (($bTotal - $cT) / [double]$bTotal * 100.0))
Write-Host ("    after-guard rows={0}, TP={1}, SL={2}" -f $cT, $cTP, $cSL)
Write-Host ("    TP-first win-rate = {0:N2}%  (Δ = {1:+0.00;-0.00}%)" -f $cWR, ($cWR - $bWR))

# === per-symbol 가드 효과 (메이저 + 알트 BOTTOM) ===
Write-Host ""
Write-Host "=========================================================="
Write-Host "  [심볼별 baseline vs A+B+C 가드 효과]"
Write-Host "=========================================================="
$cmd5 = $cn.CreateCommand()
$cmd5.CommandTimeout = 90
$cmd5.CommandText = @"
WITH B AS (
  SELECT Symbol,
    SUM(CASE WHEN Label_TP_First=1 THEN 1 ELSE 0 END) AS TP_b,
    SUM(CASE WHEN Label_SL_First=1 THEN 1 ELSE 0 END) AS SL_b
  FROM dbo.BacktestDataset GROUP BY Symbol
),
G AS (
  SELECT Symbol,
    COUNT(*) AS After_n,
    SUM(CASE WHEN Label_TP_First=1 THEN 1 ELSE 0 END) AS TP_g,
    SUM(CASE WHEN Label_SL_First=1 THEN 1 ELSE 0 END) AS SL_g
  FROM dbo.BacktestDataset
  WHERE ClosePrice > EMA20_5m
    AND (EMA50_15m IS NULL OR ClosePrice > EMA50_15m)
    AND NOT (Symbol NOT IN ($majorIn) AND RSI14 < 30)
    AND ATR14 / NULLIF(ClosePrice, 0) BETWEEN 0.005 AND 0.030
  GROUP BY Symbol
)
SELECT
  B.Symbol,
  B.TP_b + B.SL_b AS Bsig,
  CAST(CASE WHEN B.TP_b+B.SL_b>0 THEN 100.0*B.TP_b/(B.TP_b+B.SL_b) ELSE 0 END AS DECIMAL(5,2)) AS Base_WR,
  ISNULL(G.TP_g,0) + ISNULL(G.SL_g,0) AS Gsig,
  CAST(CASE WHEN ISNULL(G.TP_g,0)+ISNULL(G.SL_g,0)>0 THEN 100.0*G.TP_g/(G.TP_g+G.SL_g) ELSE 0 END AS DECIMAL(5,2)) AS Guard_WR,
  CAST(CASE WHEN ISNULL(G.TP_g,0)+ISNULL(G.SL_g,0)>0
       THEN 100.0*G.TP_g/(G.TP_g+G.SL_g) - 100.0*B.TP_b/NULLIF(B.TP_b+B.SL_b,0) ELSE 0 END AS DECIMAL(5,2)) AS Delta
FROM B LEFT JOIN G ON B.Symbol=G.Symbol
ORDER BY Delta DESC
"@
$rdr5 = $cmd5.ExecuteReader()
$tbl5 = New-Object System.Data.DataTable; $tbl5.Load($rdr5)
$tbl5 | Format-Table -AutoSize | Out-String -Width 200 | Write-Host

Write-Host ""
Write-Host "=========================================================="
Write-Host "  결론:"
Write-Host ("    Baseline:        {0:N2}%" -f $bWR)
Write-Host ("    + Alt RSI<30 차단: {0:N2}%  (Δ {1:+0.00;-0.00})" -f $aWR,  ($aWR  - $bWR))
Write-Host ("    + Lorentzian proxy: {0:N2}%  (Δ {1:+0.00;-0.00})" -f $bbWR, ($bbWR - $bWR))
Write-Host ("    + ATR 변동성:     {0:N2}%  (Δ {1:+0.00;-0.00})" -f $cWR,  ($cWR  - $bWR))
Write-Host ""
Write-Host "  -> Δ > 0 이면 가드가 win-rate 향상에 기여"
Write-Host "  -> Δ < 0 이면 가드가 오히려 좋은 진입을 차단 (rollback 검토 필요)"
Write-Host "=========================================================="

$cn.Close()
