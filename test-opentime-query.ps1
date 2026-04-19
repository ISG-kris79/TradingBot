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
$connStr = AesDecrypt $json.ConnectionStrings.DefaultConnection
$cn = New-Object System.Data.SqlClient.SqlConnection $connStr; $cn.Open()

function RunQuery($label, $sql, $params) {
    Write-Host "---- $label ----" -ForegroundColor Cyan
    try {
        $sw = [Diagnostics.Stopwatch]::StartNew()
        $cmd = $cn.CreateCommand(); $cmd.CommandText = $sql; $cmd.CommandTimeout = 30
        if ($null -ne $params) { foreach ($k in $params.Keys) { [void]$cmd.Parameters.AddWithValue($k, $params[$k]) } }
        $rdr = $cmd.ExecuteReader()
        $rowCount = 0; $nullCount = 0; $validCount = 0
        $minDt = [DateTime]::MaxValue; $maxDt = [DateTime]::MinValue
        while ($rdr.Read()) {
            $rowCount++
            $allNull = $true
            for ($i = 0; $i -lt $rdr.FieldCount; $i++) {
                if (-not $rdr.IsDBNull($i)) {
                    $allNull = $false
                    try {
                        $dt = $rdr.GetDateTime($i)
                        if ($dt -lt $minDt) { $minDt = $dt }
                        if ($dt -gt $maxDt) { $maxDt = $dt }
                        $validCount++
                    } catch { }
                }
            }
            if ($allNull) { $nullCount++ }
        }
        $rdr.Close()
        $sw.Stop()
        Write-Host ("  OK  rows={0} null={1} valid={2} min={3} max={4} ms={5}" -f $rowCount, $nullCount, $validCount, $minDt, $maxDt, $sw.ElapsedMilliseconds)
        return $true
    } catch {
        Write-Host "  FAIL: $($_.Exception.Message)" -ForegroundColor Red
        return $false
    }
}

# ═══════════════════════════════════════════════════════════════
# 테스트 1: 현재 DB 상태 확인 — 잘못된 OpenTime 행 카운트
# ═══════════════════════════════════════════════════════════════
Write-Host "`n### TEST 1: DB 에 OpenTime < 1800-01-01 행 존재 여부 (NOLOCK) ###" -ForegroundColor Yellow
$totalBad = 0
foreach ($tbl in @("MarketCandles", "CandleData", "CandleHistory", "MarketData")) {
    try {
        $c = $cn.CreateCommand()
        $c.CommandText = "SELECT COUNT(*) FROM $tbl WITH (NOLOCK) WHERE OpenTime < '1800-01-01'"
        $c.CommandTimeout = 180
        $cnt = [int]$c.ExecuteScalar()
        Write-Host "  $tbl : $cnt rows" -ForegroundColor $(if ($cnt -gt 0) { "Red" } else { "Green" })
        $totalBad += $cnt
    } catch {
        Write-Host "  $tbl : 조회 실패 - $($_.Exception.Message.Split([char]10)[0])" -ForegroundColor Red
    }
}
Write-Host "  합계 잘못된 행: $totalBad" -ForegroundColor $(if ($totalBad -gt 0) { "Red" } else { "Green" })

# ═══════════════════════════════════════════════════════════════
# 테스트 2: 수정된 BulkPreload 쿼리 (필터 적용됨)
# ═══════════════════════════════════════════════════════════════
Write-Host "`n### TEST 2: BulkPreload 쿼리 (v5.10.56) ###" -ForegroundColor Yellow
$bulkSql = @"
SELECT Symbol, MAX(CAST(OpenTime AS DATETIME2(7))) AS MaxOT FROM MarketCandles WITH (NOLOCK) WHERE Symbol IS NOT NULL AND OpenTime > '1800-01-01' GROUP BY Symbol
UNION ALL
SELECT Symbol, MAX(CAST(OpenTime AS DATETIME2(7))) AS MaxOT FROM CandleData WITH (NOLOCK) WHERE Symbol IS NOT NULL AND OpenTime > '1800-01-01' GROUP BY Symbol
UNION ALL
SELECT Symbol, MAX(CAST(OpenTime AS DATETIME2(7))) AS MaxOT FROM CandleHistory WITH (NOLOCK) WHERE [Interval] = @Interval AND OpenTime > '1800-01-01' GROUP BY Symbol
UNION ALL
SELECT Symbol, MAX(CAST(OpenTime AS DATETIME2(7))) AS MaxOT FROM MarketData WITH (NOLOCK) WHERE [Interval] = @Interval AND OpenTime > '1800-01-01' GROUP BY Symbol
"@
RunQuery "BulkPreload with filter" $bulkSql @{ "@Interval" = "5m" }

# ═══════════════════════════════════════════════════════════════
# 테스트 3: 필터 없는 쿼리 — 에러 재현 시도
# ═══════════════════════════════════════════════════════════════
Write-Host "`n### TEST 3: 필터 없는 MAX(OpenTime) 쿼리 — 에러 재현 시도 ###" -ForegroundColor Yellow
RunQuery "NO filter - MarketCandles" "SELECT Symbol, MAX(OpenTime) FROM MarketCandles WITH (NOLOCK) WHERE Symbol IS NOT NULL GROUP BY Symbol" $null
RunQuery "NO filter - CandleData"    "SELECT Symbol, MAX(OpenTime) FROM CandleData    WITH (NOLOCK) WHERE Symbol IS NOT NULL GROUP BY Symbol" $null
RunQuery "NO filter - CandleHistory" "SELECT Symbol, MAX(OpenTime) FROM CandleHistory WITH (NOLOCK) WHERE [Interval] = @Interval GROUP BY Symbol" @{ "@Interval" = "5m" }
RunQuery "NO filter - MarketData"    "SELECT Symbol, MAX(OpenTime) FROM MarketData    WITH (NOLOCK) WHERE [Interval] = @Interval GROUP BY Symbol" @{ "@Interval" = "5m" }

# ═══════════════════════════════════════════════════════════════
# 테스트 4: 필터 없이 CAST (v5.10.47 방어 유지 테스트)
# ═══════════════════════════════════════════════════════════════
Write-Host "`n### TEST 4: CAST만 적용 (필터 없음) ###" -ForegroundColor Yellow
RunQuery "CAST only - MarketCandles" "SELECT Symbol, MAX(CAST(OpenTime AS DATETIME2(7))) FROM MarketCandles WITH (NOLOCK) WHERE Symbol IS NOT NULL GROUP BY Symbol" $null

# ═══════════════════════════════════════════════════════════════
# 테스트 5: 단일 심볼 쿼리 여러 심볼
# ═══════════════════════════════════════════════════════════════
Write-Host "`n### TEST 5: 단일 심볼 쿼리 ###" -ForegroundColor Yellow
$singleSql = @"
SELECT
    (SELECT MAX(CAST(OpenTime AS DATETIME2(7))) FROM MarketCandles WITH (NOLOCK) WHERE Symbol = @Symbol AND OpenTime > '1800-01-01') AS mc,
    (SELECT MAX(CAST(OpenTime AS DATETIME2(7))) FROM CandleData    WITH (NOLOCK) WHERE Symbol = @Symbol AND OpenTime > '1800-01-01') AS cd,
    (SELECT MAX(CAST(OpenTime AS DATETIME2(7))) FROM CandleHistory WITH (NOLOCK) WHERE Symbol = @Symbol AND [Interval] = @Interval AND OpenTime > '1800-01-01') AS ch,
    (SELECT MAX(CAST(OpenTime AS DATETIME2(7))) FROM MarketData    WITH (NOLOCK) WHERE Symbol = @Symbol AND [Interval] = @Interval AND OpenTime > '1800-01-01') AS md
"@
foreach ($sym in @("BTCUSDT", "ETHUSDT", "ZECUSDT", "UNKNOWN123", "DOGEUSDT")) {
    RunQuery "Symbol=$sym" $singleSql @{ "@Symbol" = $sym; "@Interval" = "5m" }
}

# ═══════════════════════════════════════════════════════════════
# 테스트 6: 에러 재현 — MinValue 행 삽입 후 쿼리 (원복)
# ═══════════════════════════════════════════════════════════════
Write-Host "`n### TEST 6: MinValue 행 삽입 → 필터 없이 쿼리 → 에러 재현 ###" -ForegroundColor Yellow
$testSymbol = "ZZTEST_OT_" + [Guid]::NewGuid().ToString("N").Substring(0, 8)
try {
    $insSql = @"
INSERT INTO CandleData (Symbol, IntervalText, OpenTime, [Open], [High], [Low], [Close], Volume)
VALUES (@Symbol, '5m', '0001-01-01 00:00:00', 100, 101, 99, 100, 1000)
"@
    $cmdI = $cn.CreateCommand(); $cmdI.CommandText = $insSql
    [void]$cmdI.Parameters.AddWithValue("@Symbol", $testSymbol)
    [void]$cmdI.ExecuteNonQuery()
    Write-Host "  [INSERT] $testSymbol OpenTime=0001-01-01 행 삽입 완료"

    Write-Host "  [필터 없이]" -NoNewline
    RunQuery "NO filter $testSymbol" "SELECT MAX(OpenTime) FROM CandleData WHERE Symbol = @Symbol" @{ "@Symbol" = $testSymbol } | Out-Null

    Write-Host "  [필터 있음 '> 1800-01-01']" -NoNewline
    RunQuery "WITH filter $testSymbol" "SELECT MAX(CAST(OpenTime AS DATETIME2(7))) FROM CandleData WHERE Symbol = @Symbol AND OpenTime > '1800-01-01'" @{ "@Symbol" = $testSymbol } | Out-Null
}
finally {
    $cmdD = $cn.CreateCommand(); $cmdD.CommandText = "DELETE FROM CandleData WHERE Symbol = @Symbol"
    [void]$cmdD.Parameters.AddWithValue("@Symbol", $testSymbol)
    $del = $cmdD.ExecuteNonQuery()
    Write-Host "  [CLEANUP] 테스트 행 $del개 삭제"
}

# ═══════════════════════════════════════════════════════════════
# 테스트 7: 모든 datetime 타입 확인 (CAST 없이 직접 DateTime으로 받을 때 문제 있는지)
# ═══════════════════════════════════════════════════════════════
Write-Host "`n### TEST 7: 테이블별 OpenTime 컬럼 타입 확인 ###" -ForegroundColor Yellow
$typeSql = @"
SELECT TABLE_NAME, DATA_TYPE, DATETIME_PRECISION
FROM INFORMATION_SCHEMA.COLUMNS
WHERE COLUMN_NAME = 'OpenTime'
  AND TABLE_NAME IN ('MarketCandles','CandleData','CandleHistory','MarketData')
ORDER BY TABLE_NAME
"@
$cmd7 = $cn.CreateCommand(); $cmd7.CommandText = $typeSql
$rdr7 = $cmd7.ExecuteReader()
while ($rdr7.Read()) {
    $name = $rdr7.GetString(0); $type = $rdr7.GetString(1)
    $prec = if ($rdr7.IsDBNull(2)) { "-" } else { $rdr7.GetInt16(2) }
    Write-Host "  $name : $type (precision=$prec)"
}
$rdr7.Close()

$cn.Close()
Write-Host "`n✅ 전체 OpenTime 테스트 완료" -ForegroundColor Green
