# TradePatternSnapshots 테이블 확인 스크립트
# 성공/실패 진입 데이터가 제대로 수집되는지 검증

param(
    [string]$Symbol = "BTCUSDT"
)

function Get-DecryptedConnectionString {
    try {
        $appSettingsPath = Join-Path $PSScriptRoot "appsettings.json"
        if (-not (Test-Path $appSettingsPath)) {
            Write-Host "appsettings.json not found at: $appSettingsPath" -ForegroundColor Red
            return $null
        }

        $json = Get-Content $appSettingsPath -Raw | ConvertFrom-Json
        $encryptedConnectionString = $json.ConnectionStrings.DefaultConnection
        $isEncrypted = $json.ConnectionStrings.IsEncrypted

        if (-not $isEncrypted) {
            return $encryptedConnectionString
        }

        # AES-256 복호화
        $aesKey = [byte[]](0x43, 0x6F, 0x69, 0x6E, 0x46, 0x46, 0x2D, 0x54,
                           0x72, 0x61, 0x64, 0x69, 0x6E, 0x67, 0x42, 0x6F,
                           0x74, 0x2D, 0x41, 0x45, 0x53, 0x32, 0x35, 0x36,
                           0x2D, 0x4B, 0x65, 0x79, 0x2D, 0x33, 0x32, 0x42)

        $fullCipher = [Convert]::FromBase64String($encryptedConnectionString)
        
        $aes = [System.Security.Cryptography.Aes]::Create()
        $aes.Key = $aesKey
        
        $ivLength = $aes.IV.Length
        $iv = New-Object byte[] $ivLength
        $cipher = New-Object byte[] ($fullCipher.Length - $ivLength)
        
        [Buffer]::BlockCopy($fullCipher, 0, $iv, 0, $ivLength)
        [Buffer]::BlockCopy($fullCipher, $ivLength, $cipher, 0, $cipher.Length)
        
        $aes.IV = $iv
        $decryptor = $aes.CreateDecryptor($aes.Key, $aes.IV)
        $decryptedBytes = $decryptor.TransformFinalBlock($cipher, 0, $cipher.Length)
        $decryptedString = [System.Text.Encoding]::UTF8.GetString($decryptedBytes)
        
        $aes.Dispose()
        $decryptor.Dispose()
        
        return $decryptedString
    }
    catch {
        Write-Host "복호화 실패: $($_.Exception.Message)" -ForegroundColor Red
        return $null
    }
}

function Invoke-Query($query) {
    $connectionString = Get-DecryptedConnectionString
    if (-not $connectionString) {
        throw "Failed to get connection string from appsettings.json"
    }

    $conn = New-Object System.Data.SqlClient.SqlConnection
    $conn.ConnectionString = $connectionString
    $conn.Open()
    
    $cmd = $conn.CreateCommand()
    $cmd.CommandText = $query
    $cmd.CommandTimeout = 30
    
    $adapter = New-Object System.Data.SqlClient.SqlDataAdapter $cmd
    $dataset = New-Object System.Data.DataSet
    [void]$adapter.Fill($dataset)
    
    $conn.Close()
    return $dataset.Tables[0]
}

Write-Host "=============================================" -ForegroundColor Cyan
Write-Host "   TradePatternSnapshots DB 검증" -ForegroundColor Cyan
Write-Host "   성공/실패 진입 데이터 수집 확인" -ForegroundColor Cyan
Write-Host "=============================================" -ForegroundColor Cyan
Write-Host ""

try {
    # 1. 전체 스냅샷 통계
    Write-Host "[1] 전체 스냅샷 통계 (최근 7일)" -ForegroundColor Yellow
    $statsQuery = "SELECT COUNT(*) AS TotalSnapshots, SUM(CASE WHEN Label = 1 THEN 1 ELSE 0 END) AS SuccessCount, SUM(CASE WHEN Label = 0 THEN 1 ELSE 0 END) AS FailCount, SUM(CASE WHEN Label IS NULL THEN 1 ELSE 0 END) AS PendingCount, AVG(CASE WHEN Label IS NOT NULL THEN CAST(PnLPercent AS FLOAT) END) AS AvgPnLPercent, MAX(EntryTime) AS LatestEntry FROM dbo.TradePatternSnapshots WHERE EntryTime >= DATEADD(DAY, -7, GETUTCDATE())"
    $stats = Invoke-Query $statsQuery
    if ($stats.Rows.Count -gt 0) {
        $stats | Format-Table -AutoSize
    } else {
        Write-Host "스냅샷 데이터 없음" -ForegroundColor Red
    }

    # 2. 최근 라벨링된 스냅샷 (성공/실패 구분)
    Write-Host ""
    Write-Host "[2] 최근 라벨링된 스냅샷 TOP 10" -ForegroundColor Yellow
    $labeledQuery = "SELECT TOP 10 Id, Symbol, Side, Strategy, EntryTime, EntryPrice, ExitTime, Label, PnL, PnLPercent, ExitReason, FinalScore, AiScore, CASE WHEN IsSuperEntry = 1 THEN 'SUPER' ELSE 'NORMAL' END AS EntryType FROM dbo.TradePatternSnapshots WHERE Label IS NOT NULL ORDER BY UpdatedAt DESC"
    $labeled = Invoke-Query $labeledQuery
    if ($labeled.Rows.Count -gt 0) {
        $labeled | Format-Table -AutoSize -Wrap
    } else {
        Write-Host "라벨링된 데이터 없음" -ForegroundColor Red
    }

    # 3. 대기 중인 스냅샷 (아직 청산 안 된 진입)
    Write-Host ""
    Write-Host "[3] 대기 중인 스냅샷 (Label IS NULL)" -ForegroundColor Yellow
    $pendingQuery = @"
SELECT TOP 5
    Id,
    Symbol,
    Side,
    Strategy,
    EntryTime,
    EntryPrice,
    FinalScore,
    DATEDIFF(MINUTE, EntryTime, GETUTCDATE()) AS MinutesSinceEntry
FROM dbo.TradePatternSnapshots
WHERE Label IS NULL
ORDER BY EntryTime DESC
"@
    $pending = Invoke-Query $pendingQuery
    if ($pending.Rows.Count -gt 0) {
        $pending | Format-Table -AutoSize
        Write-Host "  💡 이 진입들은 청산 후 Label (0 또는 1)이 업데이트됩니다." -ForegroundColor Gray
    } else {
        Write-Host ""SELECT TOP 5 Id, Symbol, Side, Strategy, EntryTime, EntryPrice, FinalScore, DATEDIFF(MINUTE, EntryTime, GETUTCDATE()) AS MinutesSinceEntry FROM dbo.TradePatternSnapshots WHERE Label IS NULL ORDER BY EntryTime DESC"  EntryPrice,
    ExitPrice,
    Label,
    PnLPercent,
    ExitReason,
    FinalScore,
    AiScore,
    ElliottScore,"SELECT TOP 5 Id, Side, Strategy, EntryTime, EntryPrice, ExitPrice, Label, PnLPercent, ExitReason, FinalScore, AiScore, ElliottScore, VolumeScore, MatchProbability, IsSuperEntry FROM dbo.TradePatternSnapshots WHERE Symbol = '$Symbol' ORDER BY EntryTime DESC"LECT 
    Symbol,
    COUNT(*) AS TotalTrades,
    SUM(CASE WHEN Label = 1 THEN 1 ELSE 0 END) AS Wins,
    SUM(CASE WHEN Label = 0 THEN 1 ELSE 0 END) AS Losses,
    CAST(100.0 * SUM(CASE WHEN Label = 1 THEN 1 ELSE 0 END) / COUNT(*) AS DECIMAL(5,2)) AS WinRate,
    AVG(CAST(PnLPercent AS FLOAT)) AS AvgPnL,
    SUM(CASE WHEN IsSuperEntry = 1 THEN 1 ELSE 0 END) AS SuperEntries
FROM dbo.TradePatternSnapshots
WHERE Label IS NOT NULL
  AND EntryTime >= DATEADD(DAY, -7, GETUTCDATE())
GROUP BY Symbol
ORDER BY TotalTrades DESC
"@
    $winRate = Invoke-Query $winRateQuery
    if ($winRate.Rows.Count -gt 0) {
        $winRate | Format-Table -AutoSize
    } else {
        Write-Host "승률 데이터 없음" -ForegroundColor Red
    }"SELECT Symbol, COUNT(*) AS TotalTrades, SUM(CASE WHEN Label = 1 THEN 1 ELSE 0 END) AS Wins, SUM(CASE WHEN Label = 0 THEN 1 ELSE 0 END) AS Losses, CAST(100.0 * SUM(CASE WHEN Label = 1 THEN 1 ELSE 0 END) / COUNT(*) AS DECIMAL(5,2)) AS WinRate, AVG(CAST(PnLPercent AS FLOAT)) AS AvgPnL, SUM(CASE WHEN IsSuperEntry = 1 THEN 1 ELSE 0 END) AS SuperEntries FROM dbo.TradePatternSnapshots WHERE Label IS NOT NULL AND EntryTime >= DATEADD(DAY, -7, GETUTCDATE()) GROUP BY Symbol ORDER BY TotalTrades DESC"  COUNT(*) AS Count,
    AVG(FinalScore) AS AvgFinalScore,
    AVG(AiScore) AS AvgAiScore,
    AVG(ElliottScore) AS AvgElliottScore,
    AVG(VolumeScore) AS AvgVolumeScore
FROM dbo.TradePatternSnapshots
WHERE EntryTime >= DATEADD(DAY, -30, GETUTCDATE())
GROUP BY Label
ORDER BY Label DESC
"@
    $quality = Invoke-Query $qualityQuery
    if ($quality.Rows.Count -gt 0) {
        $quality | Format-Table -AutoSize
    } else {
        Write-Host "데이터 없음" -ForegroundColor Red
    }

    Write-Host ""
    Write-Host "=============================================" -ForegroundColor Green
    Write-Host "   검증 완료: DB 스냅샷 정상 수집 중" -ForegroundColor Green
    Write-Host "=============================================" -ForegroundColor Green
    Write-Host ""
    Write-Host "💡 학습용 데이터 조회 예시:" -ForegroundColor Cyan
    Write-Host "   await _dbManager.GetLabeledTradePatternSnapshotsAsync(""BTCUSDT"", ""LONG"", lookbackDays: 120, maxRows: 600)" -ForegroundColor Gray
    Write-Host ""

} catch {
    Write-Host """SELECT CASE WHEN Label = 1 THEN 'SUCCESS' WHEN Label = 0 THEN 'FAIL' ELSE 'PENDING' END AS LabelType, COUNT(*) AS Count, AVG(FinalScore) AS AvgFinalScore, AVG(AiScore) AS AvgAiScore, AVG(ElliottScore) AS AvgElliottScore, AVG(VolumeScore) AS AvgVolumeScore FROM dbo.TradePatternSnapshots WHERE EntryTime >= DATEADD(DAY, -30, GETUTCDATE()) GROUP BY Label ORDER BY Label DESC"
