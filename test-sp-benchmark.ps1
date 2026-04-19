# v5.10.58 Stored Procedure 벤치마크
# 1. SP를 DB에 등록
# 2. Dapper (인라인 SQL) vs SP (ADO.NET) 속도 비교
# 3. PositionState MERGE / OpenTime 집계 각각 측정

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

# ════════════════════════════════════════════════════════════════════
# Step 1: SP 등록
# ════════════════════════════════════════════════════════════════════
Write-Host "=== Step 1: SP 등록 ===" -ForegroundColor Yellow

$spScripts = @(
@"
CREATE OR ALTER PROCEDURE dbo.sp_SavePositionState
    @UserId INT, @Symbol NVARCHAR(32),
    @TakeProfitStep INT, @PartialProfitStage INT,
    @BreakevenPrice DECIMAL(18,8), @HighestROE DECIMAL(18,4),
    @StairStep INT, @IsBreakEvenTriggered BIT,
    @HighestPrice DECIMAL(18,8), @LowestPrice DECIMAL(18,8),
    @IsPumpStrategy BIT
AS
BEGIN
    SET NOCOUNT ON;
    MERGE dbo.PositionState WITH (HOLDLOCK) AS target
    USING (SELECT @UserId AS UserId, @Symbol AS Symbol) AS source
    ON target.UserId = source.UserId AND target.Symbol = source.Symbol
    WHEN MATCHED THEN
        UPDATE SET
            TakeProfitStep = CASE WHEN @TakeProfitStep > 0 THEN @TakeProfitStep ELSE target.TakeProfitStep END,
            PartialProfitStage = CASE WHEN @PartialProfitStage > 0 THEN @PartialProfitStage ELSE target.PartialProfitStage END,
            BreakevenPrice = CASE WHEN @BreakevenPrice > 0 THEN @BreakevenPrice ELSE target.BreakevenPrice END,
            HighestROE = CASE WHEN @HighestROE > target.HighestROE THEN @HighestROE ELSE target.HighestROE END,
            StairStep = CASE WHEN @StairStep > target.StairStep THEN @StairStep ELSE target.StairStep END,
            IsBreakEvenTriggered = CASE WHEN @IsBreakEvenTriggered = 1 THEN 1 ELSE target.IsBreakEvenTriggered END,
            HighestPrice = CASE WHEN @HighestPrice > target.HighestPrice THEN @HighestPrice ELSE target.HighestPrice END,
            LowestPrice = CASE WHEN @LowestPrice > 0 AND (target.LowestPrice = 0 OR @LowestPrice < target.LowestPrice) THEN @LowestPrice ELSE target.LowestPrice END,
            IsPumpStrategy = @IsPumpStrategy, LastUpdatedAt = SYSDATETIME()
    WHEN NOT MATCHED THEN
        INSERT (UserId, Symbol, TakeProfitStep, PartialProfitStage, BreakevenPrice, HighestROE, StairStep, IsBreakEvenTriggered, HighestPrice, LowestPrice, IsPumpStrategy)
        VALUES (@UserId, @Symbol, @TakeProfitStep, @PartialProfitStage, @BreakevenPrice, @HighestROE, @StairStep, @IsBreakEvenTriggered, @HighestPrice, @LowestPrice, @IsPumpStrategy);
END
"@,
@"
CREATE OR ALTER PROCEDURE dbo.sp_GetOpenTimeAcrossTables
    @Symbol NVARCHAR(32), @Interval NVARCHAR(8)
AS
BEGIN
    SET NOCOUNT ON;
    SELECT
        (SELECT MAX(CAST(OpenTime AS DATETIME2(7))) FROM dbo.MarketCandles WITH (NOLOCK) WHERE Symbol = @Symbol AND OpenTime > '1800-01-01') AS mc,
        (SELECT MAX(CAST(OpenTime AS DATETIME2(7))) FROM dbo.CandleData    WITH (NOLOCK) WHERE Symbol = @Symbol AND OpenTime > '1800-01-01') AS cd,
        (SELECT MAX(CAST(OpenTime AS DATETIME2(7))) FROM dbo.CandleHistory WITH (NOLOCK) WHERE Symbol = @Symbol AND [Interval] = @Interval AND OpenTime > '1800-01-01') AS ch,
        (SELECT MAX(CAST(OpenTime AS DATETIME2(7))) FROM dbo.MarketData    WITH (NOLOCK) WHERE Symbol = @Symbol AND [Interval] = @Interval AND OpenTime > '1800-01-01') AS md;
END
"@
)

$cn = New-Object System.Data.SqlClient.SqlConnection $connStr; $cn.Open()
foreach ($body in $spScripts) {
    try {
        $c = $cn.CreateCommand(); $c.CommandText = $body; $c.CommandTimeout = 30
        [void]$c.ExecuteNonQuery()
        $name = ([regex]::Match($body, 'PROCEDURE\s+dbo\.(\w+)').Groups[1].Value)
        Write-Host "  ✅ $name 등록 완료" -ForegroundColor Green
    } catch {
        Write-Host "  ❌ 등록 실패: $($_.Exception.Message)" -ForegroundColor Red
    }
}

# ════════════════════════════════════════════════════════════════════
# Step 2: PositionState MERGE 벤치마크 (Dapper vs SP, 100회)
# ════════════════════════════════════════════════════════════════════
Write-Host "`n=== Step 2: PositionState MERGE 벤치마크 (100회 반복) ===" -ForegroundColor Yellow
$testUserId = 999999
$testSymbol = "ZZBENCH"

# 2-a. 인라인 Dapper 스타일 MERGE (parameterized SQL)
$inlineSql = @"
MERGE dbo.PositionState WITH (HOLDLOCK) AS target
USING (SELECT @UserId AS UserId, @Symbol AS Symbol) AS source
ON target.UserId = source.UserId AND target.Symbol = source.Symbol
WHEN MATCHED THEN UPDATE SET HighestROE = @HighestROE, LastUpdatedAt = SYSDATETIME()
WHEN NOT MATCHED THEN INSERT (UserId, Symbol, TakeProfitStep, PartialProfitStage, BreakevenPrice, HighestROE, StairStep, IsBreakEvenTriggered, HighestPrice, LowestPrice, IsPumpStrategy)
VALUES (@UserId, @Symbol, 0, 0, 0, @HighestROE, 0, 0, 0, 0, 0);
"@

$sw = [Diagnostics.Stopwatch]::StartNew()
for ($i = 0; $i -lt 100; $i++) {
    $c = $cn.CreateCommand(); $c.CommandText = $inlineSql; $c.CommandTimeout = 8
    [void]$c.Parameters.AddWithValue("@UserId", $testUserId)
    [void]$c.Parameters.AddWithValue("@Symbol", $testSymbol)
    [void]$c.Parameters.AddWithValue("@HighestROE", [decimal]($i + 1))
    [void]$c.ExecuteNonQuery()
}
$sw.Stop()
$inlineMs = $sw.ElapsedMilliseconds
Write-Host "  Dapper 스타일 inline SQL: 100회 $inlineMs ms (평균 $([Math]::Round($inlineMs/100.0, 2)) ms/호출)" -ForegroundColor Cyan

# 2-b. SP 호출
$sw2 = [Diagnostics.Stopwatch]::StartNew()
for ($i = 0; $i -lt 100; $i++) {
    $c = $cn.CreateCommand(); $c.CommandText = "dbo.sp_SavePositionState"
    $c.CommandType = [System.Data.CommandType]::StoredProcedure; $c.CommandTimeout = 8
    [void]$c.Parameters.AddWithValue("@UserId", $testUserId)
    [void]$c.Parameters.AddWithValue("@Symbol", $testSymbol)
    [void]$c.Parameters.AddWithValue("@TakeProfitStep", 0)
    [void]$c.Parameters.AddWithValue("@PartialProfitStage", 0)
    [void]$c.Parameters.AddWithValue("@BreakevenPrice", [decimal]0)
    [void]$c.Parameters.AddWithValue("@HighestROE", [decimal]($i + 1))
    [void]$c.Parameters.AddWithValue("@StairStep", 0)
    [void]$c.Parameters.AddWithValue("@IsBreakEvenTriggered", $false)
    [void]$c.Parameters.AddWithValue("@HighestPrice", [decimal]0)
    [void]$c.Parameters.AddWithValue("@LowestPrice", [decimal]0)
    [void]$c.Parameters.AddWithValue("@IsPumpStrategy", $false)
    [void]$c.ExecuteNonQuery()
}
$sw2.Stop()
$spMs = $sw2.ElapsedMilliseconds
Write-Host "  SP 호출 (sp_SavePositionState): 100회 $spMs ms (평균 $([Math]::Round($spMs/100.0, 2)) ms/호출)" -ForegroundColor Cyan

if ($inlineMs -gt 0) {
    $diff = $inlineMs - $spMs
    $pct = [Math]::Round(($diff / $inlineMs) * 100, 1)
    Write-Host "  → SP가 $pct% 빠름 ($diff ms 단축)" -ForegroundColor $(if ($pct -gt 0) { "Green" } else { "Yellow" })
}

# 테스트 데이터 정리
$cDel = $cn.CreateCommand(); $cDel.CommandText = "DELETE FROM dbo.PositionState WHERE UserId = @U AND Symbol = @S"
[void]$cDel.Parameters.AddWithValue("@U", $testUserId)
[void]$cDel.Parameters.AddWithValue("@S", $testSymbol)
[void]$cDel.ExecuteNonQuery()

# ════════════════════════════════════════════════════════════════════
# Step 3: OpenTime 집계 벤치마크 (20회 반복, BTCUSDT)
# ════════════════════════════════════════════════════════════════════
Write-Host "`n=== Step 3: OpenTime 집계 (BTCUSDT, 20회) ===" -ForegroundColor Yellow

$inlineOtSql = @"
SELECT
    (SELECT MAX(CAST(OpenTime AS DATETIME2(7))) FROM dbo.MarketCandles WITH (NOLOCK) WHERE Symbol = @Symbol AND OpenTime > '1800-01-01') AS mc,
    (SELECT MAX(CAST(OpenTime AS DATETIME2(7))) FROM dbo.CandleData    WITH (NOLOCK) WHERE Symbol = @Symbol AND OpenTime > '1800-01-01') AS cd,
    (SELECT MAX(CAST(OpenTime AS DATETIME2(7))) FROM dbo.CandleHistory WITH (NOLOCK) WHERE Symbol = @Symbol AND [Interval] = @Interval AND OpenTime > '1800-01-01') AS ch,
    (SELECT MAX(CAST(OpenTime AS DATETIME2(7))) FROM dbo.MarketData    WITH (NOLOCK) WHERE Symbol = @Symbol AND [Interval] = @Interval AND OpenTime > '1800-01-01') AS md;
"@

$sw3 = [Diagnostics.Stopwatch]::StartNew()
for ($i = 0; $i -lt 20; $i++) {
    $c = $cn.CreateCommand(); $c.CommandText = $inlineOtSql; $c.CommandTimeout = 15
    [void]$c.Parameters.AddWithValue("@Symbol", "BTCUSDT")
    [void]$c.Parameters.AddWithValue("@Interval", "5m")
    $rdr = $c.ExecuteReader()
    if ($rdr.Read()) { } # consume
    $rdr.Close()
}
$sw3.Stop()
$inlineOtMs = $sw3.ElapsedMilliseconds
Write-Host "  Inline SQL: 20회 $inlineOtMs ms (평균 $([Math]::Round($inlineOtMs/20.0, 2)) ms/호출)" -ForegroundColor Cyan

$sw4 = [Diagnostics.Stopwatch]::StartNew()
for ($i = 0; $i -lt 20; $i++) {
    $c = $cn.CreateCommand(); $c.CommandText = "dbo.sp_GetOpenTimeAcrossTables"
    $c.CommandType = [System.Data.CommandType]::StoredProcedure; $c.CommandTimeout = 15
    [void]$c.Parameters.AddWithValue("@Symbol", "BTCUSDT")
    [void]$c.Parameters.AddWithValue("@Interval", "5m")
    $rdr = $c.ExecuteReader()
    if ($rdr.Read()) { } # consume
    $rdr.Close()
}
$sw4.Stop()
$spOtMs = $sw4.ElapsedMilliseconds
Write-Host "  SP (sp_GetOpenTimeAcrossTables): 20회 $spOtMs ms (평균 $([Math]::Round($spOtMs/20.0, 2)) ms/호출)" -ForegroundColor Cyan

if ($inlineOtMs -gt 0) {
    $diff2 = $inlineOtMs - $spOtMs
    $pct2 = [Math]::Round(($diff2 / $inlineOtMs) * 100, 1)
    Write-Host "  → SP가 $pct2% 빠름 ($diff2 ms 단축)" -ForegroundColor $(if ($pct2 -gt 0) { "Green" } else { "Yellow" })
}

$cn.Close()
Write-Host "`n✅ 벤치마크 완료" -ForegroundColor Green
