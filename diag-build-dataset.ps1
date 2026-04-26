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
$cs = AesDecrypt $json.ConnectionStrings.DefaultConnection

# Schema bootstrap (idempotent)
$cn = New-Object System.Data.SqlClient.SqlConnection $cs
$cn.Open()
$bootstrap = @"
IF OBJECT_ID('dbo.BacktestDataset', 'U') IS NULL
BEGIN
    CREATE TABLE dbo.BacktestDataset (
        Id          BIGINT IDENTITY(1,1) PRIMARY KEY,
        Symbol      NVARCHAR(40) NOT NULL,
        IntervalText NVARCHAR(8) NOT NULL,
        OpenTime    DATETIME2 NOT NULL,
        ClosePrice  DECIMAL(28,10) NOT NULL,
        EMA20_5m    DECIMAL(28,10) NULL,
        EMA50_15m   DECIMAL(28,10) NULL,
        RSI14       DECIMAL(10,4) NULL,
        ATR14       DECIMAL(28,10) NULL,
        VolMA20     DECIMAL(28,4) NULL,
        Volume      DECIMAL(28,4) NULL,
        Label_TP_First BIT NULL,
        Label_SL_First BIT NULL,
        TP_Pct      DECIMAL(8,4) NULL,
        SL_Pct      DECIMAL(8,4) NULL,
        WindowMinutes INT NULL,
        CreatedAt   DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME(),
        CONSTRAINT UX_BacktestDataset_Sym_Int_Time UNIQUE (Symbol, IntervalText, OpenTime)
    );
    CREATE INDEX IX_BacktestDataset_Symbol ON dbo.BacktestDataset (Symbol, OpenTime DESC);
END
"@
$cmd = $cn.CreateCommand(); $cmd.CommandText = $bootstrap; $cmd.CommandTimeout = 120; [void]$cmd.ExecuteNonQuery()
Write-Host "[OK] BacktestDataset schema ready"

# Symbols (30 — from validator)
$symbols = @(
    'BTCUSDT','ETHUSDT','SOLUSDT','XRPUSDT','BNBUSDT','DOGEUSDT','ADAUSDT','TRXUSDT','AVAXUSDT','LINKUSDT',
    'APEUSDT','API3USDT','DUSDT','DYMUSDT','DYDXUSDT','ESPORTSUSDT','SPORTFUNUSDT','KGENUSDT','PLAYUSDT','MAGMAUSDT',
    'GRIFFAINUSDT','WUSDT','PUMPBTCUSDT','ZBTUSDT','GALAUSDT','SOONUSDT','OPNUSDT','ZKPUSDT','BSBUSDT','KATUSDT'
)

$DAYS = 30
$BARS_PER_DAY = 288
$TOTAL_BARS = $DAYS * $BARS_PER_DAY  # 8640
$BATCH = 1500
$REQUESTS_PER_SYM = [int][Math]::Ceiling($TOTAL_BARS / [double]$BATCH)
$TP_PCT = 1.5
$SL_PCT = 0.7
$WINDOW_BARS = 12  # 1 hour window

function CalcEMA($values, $period) {
    if ($values.Count -lt 1) { return $null }
    $k = 2.0 / ($period + 1)
    $ema = @($values[0])
    for ($i = 1; $i -lt $values.Count; $i++) {
        $ema += ($values[$i] * $k + $ema[$i-1] * (1 - $k))
    }
    return $ema
}
function CalcRSIArr($closes, $period) {
    $out = New-Object 'double[]' $closes.Count
    if ($closes.Count -lt $period + 1) { return $out }
    $g=0.0; $l=0.0
    for ($i=1; $i -le $period; $i++) {
        $d = $closes[$i] - $closes[$i-1]
        if ($d -gt 0) { $g += $d } else { $l -= $d }
    }
    $avgG = $g/$period; $avgL = $l/$period
    for ($i = $period+1; $i -lt $closes.Count; $i++) {
        $d = $closes[$i] - $closes[$i-1]
        $gx = if ($d -gt 0) { $d } else { 0 }
        $lx = if ($d -lt 0) { -$d } else { 0 }
        $avgG = ($avgG*($period-1) + $gx)/$period
        $avgL = ($avgL*($period-1) + $lx)/$period
        if ($avgL -lt 1e-12) { $out[$i] = 100.0 }
        else { $out[$i] = 100.0 - (100.0 / (1.0 + $avgG/$avgL)) }
    }
    return $out
}
function CalcATR($highs, $lows, $closes, $period) {
    $out = New-Object 'double[]' $closes.Count
    if ($closes.Count -lt $period+1) { return $out }
    $tr = New-Object 'double[]' $closes.Count
    for ($i=1; $i -lt $closes.Count; $i++) {
        $hl = $highs[$i] - $lows[$i]
        $hc = [Math]::Abs($highs[$i] - $closes[$i-1])
        $lc = [Math]::Abs($lows[$i] - $closes[$i-1])
        $tr[$i] = [Math]::Max($hl, [Math]::Max($hc, $lc))
    }
    $sum = 0.0
    for ($i=1; $i -le $period; $i++) { $sum += $tr[$i] }
    $out[$period] = $sum / $period
    for ($i = $period+1; $i -lt $closes.Count; $i++) {
        $out[$i] = ($out[$i-1]*($period-1) + $tr[$i]) / $period
    }
    return $out
}

$totalRows = 0
$symIdx = 0
foreach ($sym in $symbols) {
    $symIdx++
    Write-Host ("[{0}/{1}] {2}" -f $symIdx, $symbols.Count, $sym) -NoNewline

    # Fetch all 5m bars (paginated)
    $endMs = [int64](([DateTime]::UtcNow - [DateTime]'1970-01-01').TotalMilliseconds)
    $allBars = @()
    for ($r=0; $r -lt $REQUESTS_PER_SYM; $r++) {
        $startMs = $endMs - ($BATCH * 5 * 60 * 1000)
        $u = "https://fapi.binance.com/fapi/v1/klines?symbol=$sym&interval=5m&limit=$BATCH&endTime=$endMs"
        $resp = $null
        for ($try=1; $try -le 5; $try++) {
            try {
                Start-Sleep -Milliseconds 800
                $resp = Invoke-RestMethod -Uri $u -Method Get -TimeoutSec 25
                break
            } catch {
                if ($_.Exception.Message -like '*429*' -or $_.Exception.Message -like '*1003*') {
                    Start-Sleep -Seconds ($try * 5); continue
                }
                break
            }
        }
        if (-not $resp -or $resp.Count -eq 0) { break }
        $allBars = ,$resp + $allBars
        $endMs = [int64]$resp[0][0] - 1
        if ($resp.Count -lt $BATCH) { break }
    }
    $flat = @()
    foreach ($chunk in $allBars) { foreach ($b in $chunk) { $flat += ,$b } }
    if ($flat.Count -lt 200) { Write-Host " skip ($($flat.Count) bars)"; continue }

    $opens   = @($flat | ForEach-Object { [double]$_[1] })
    $highs   = @($flat | ForEach-Object { [double]$_[2] })
    $lows    = @($flat | ForEach-Object { [double]$_[3] })
    $closes  = @($flat | ForEach-Object { [double]$_[4] })
    $vols    = @($flat | ForEach-Object { [double]$_[5] })
    $opentm  = @($flat | ForEach-Object { [int64]$_[0] })

    $ema20  = CalcEMA $closes 20
    $rsi14  = CalcRSIArr $closes 14
    $atr14  = CalcATR $highs $lows $closes 14

    # 15m EMA50 — resample
    $closes15 = @()
    $time15 = @()
    for ($i = 2; $i -lt $closes.Count; $i += 3) {
        $closes15 += $closes[$i]
        $time15 += $opentm[$i]
    }
    $ema50_15Arr = CalcEMA $closes15 50
    $ema50ByTime = @{}
    for ($i=0; $i -lt $time15.Count; $i++) { $ema50ByTime[$time15[$i]] = $ema50_15Arr[$i] }

    # VolMA20
    $volMA = New-Object 'double[]' $vols.Count
    for ($i=19; $i -lt $vols.Count; $i++) {
        $s=0.0; for ($j=$i-19; $j -le $i; $j++) { $s += $vols[$j] }
        $volMA[$i] = $s/20.0
    }

    # Label each candle (TP/SL first within 12 bars)
    $bulk = New-Object System.Data.DataTable
    [void]$bulk.Columns.Add("Symbol", [string])
    [void]$bulk.Columns.Add("IntervalText", [string])
    [void]$bulk.Columns.Add("OpenTime", [DateTime])
    [void]$bulk.Columns.Add("ClosePrice", [decimal])
    [void]$bulk.Columns.Add("EMA20_5m", [object])
    [void]$bulk.Columns.Add("EMA50_15m", [object])
    [void]$bulk.Columns.Add("RSI14", [object])
    [void]$bulk.Columns.Add("ATR14", [object])
    [void]$bulk.Columns.Add("VolMA20", [object])
    [void]$bulk.Columns.Add("Volume", [decimal])
    [void]$bulk.Columns.Add("Label_TP_First", [object])
    [void]$bulk.Columns.Add("Label_SL_First", [object])
    [void]$bulk.Columns.Add("TP_Pct", [decimal])
    [void]$bulk.Columns.Add("SL_Pct", [decimal])
    [void]$bulk.Columns.Add("WindowMinutes", [int])

    $tpHits = 0; $slHits = 0; $rows = 0
    for ($i = 50; $i -lt $closes.Count - $WINDOW_BARS; $i++) {
        $entry = $closes[$i]
        $tpPx  = $entry * (1 + $TP_PCT/100.0)
        $slPx  = $entry * (1 - $SL_PCT/100.0)
        $tpFirst = $false; $slFirst = $false
        for ($k=1; $k -le $WINDOW_BARS; $k++) {
            $h = $highs[$i+$k]; $l = $lows[$i+$k]
            if ($h -ge $tpPx -and $l -le $slPx) {
                # Both hit in same bar — assume worst (SL first) for safety
                $slFirst = $true; break
            }
            if ($h -ge $tpPx) { $tpFirst = $true; break }
            if ($l -le $slPx) { $slFirst = $true; break }
        }
        if ($tpFirst) { $tpHits++ }
        elseif ($slFirst) { $slHits++ }

        $r = $bulk.NewRow()
        $r["Symbol"] = $sym
        $r["IntervalText"] = "5m"
        $r["OpenTime"] = [DateTimeOffset]::FromUnixTimeMilliseconds($opentm[$i]).UtcDateTime
        $r["ClosePrice"] = [decimal]$entry
        $r["EMA20_5m"] = [decimal]$ema20[$i]
        $e15 = $null
        # nearest prior 15m bar
        for ($q = $i; $q -ge [Math]::Max(0,$i-3); $q--) {
            if ($ema50ByTime.ContainsKey($opentm[$q])) { $e15 = $ema50ByTime[$opentm[$q]]; break }
        }
        if ($null -ne $e15) { $r["EMA50_15m"] = [decimal]$e15 } else { $r["EMA50_15m"] = [DBNull]::Value }
        $r["RSI14"] = [decimal]$rsi14[$i]
        $r["ATR14"] = [decimal]$atr14[$i]
        $r["VolMA20"] = [decimal]$volMA[$i]
        $r["Volume"] = [decimal]$vols[$i]
        $r["Label_TP_First"] = if ($tpFirst) { $true } elseif ($slFirst) { $false } else { [DBNull]::Value }
        $r["Label_SL_First"] = if ($slFirst) { $true } elseif ($tpFirst) { $false } else { [DBNull]::Value }
        $r["TP_Pct"] = [decimal]$TP_PCT
        $r["SL_Pct"] = [decimal]$SL_PCT
        $r["WindowMinutes"] = $WINDOW_BARS * 5
        [void]$bulk.Rows.Add($r)
        $rows++
    }

    # Delete prior rows for this symbol/interval
    $del = $cn.CreateCommand()
    $del.CommandText = "DELETE FROM dbo.BacktestDataset WHERE Symbol=@s AND IntervalText='5m'"
    [void]$del.Parameters.AddWithValue("@s", $sym)
    $del.CommandTimeout = 60
    [void]$del.ExecuteNonQuery()

    # SqlBulkCopy
    $bcp = New-Object System.Data.SqlClient.SqlBulkCopy($cn)
    $bcp.DestinationTableName = "dbo.BacktestDataset"
    $bcp.BulkCopyTimeout = 120
    foreach ($c in $bulk.Columns) { [void]$bcp.ColumnMappings.Add($c.ColumnName, $c.ColumnName) }
    $bcp.WriteToServer($bulk)
    $bcp.Close()

    $totalRows += $rows
    $tpRate = if (($tpHits+$slHits) -gt 0) { $tpHits / [double]($tpHits+$slHits) * 100.0 } else { 0 }
    Write-Host (" {0} bars / TP {1} SL {2} (TP-first {3:N1}%)" -f $rows, $tpHits, $slHits, $tpRate)
}

Write-Host ""
Write-Host ("[DONE] {0} symbols, {1} total rows inserted" -f $symbols.Count, $totalRows)

# Quick summary
$summary = $cn.CreateCommand()
$summary.CommandText = @"
SELECT
  COUNT(*) AS TotalRows,
  SUM(CASE WHEN Label_TP_First=1 THEN 1 ELSE 0 END) AS TP_Hits,
  SUM(CASE WHEN Label_SL_First=1 THEN 1 ELSE 0 END) AS SL_Hits,
  SUM(CASE WHEN Label_TP_First IS NULL AND Label_SL_First IS NULL THEN 1 ELSE 0 END) AS Neutral
FROM dbo.BacktestDataset
"@
$rdr = $summary.ExecuteReader()
if ($rdr.Read()) {
    $tot = $rdr["TotalRows"]; $tp = $rdr["TP_Hits"]; $sl = $rdr["SL_Hits"]; $nu = $rdr["Neutral"]
    Write-Host ""
    Write-Host "=========================================================="
    Write-Host ("  Dataset: {0} rows | TP {1} | SL {2} | Neutral {3}" -f $tot, $tp, $sl, $nu)
    if (($tp+$sl) -gt 0) {
        $wr = $tp / [double]($tp+$sl) * 100.0
        Write-Host ("  Base TP-first rate: {0:N2}%  (1.5%TP / 0.7%SL / 1h window)" -f $wr)
    }
    Write-Host "=========================================================="
}
$rdr.Close()
$cn.Close()
