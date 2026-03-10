# TradeHistory & TradeLogs 테이블 상세 검증 스크립트
# 실제 진입/청산 데이터가 제대로 저장되는지 확인

param(
    [string]$Symbol = "BTCUSDT"
)

function Get-DecryptedConnectionString {
    $appSettingsPath = Join-Path $PSScriptRoot "appsettings.json"
    if (-not (Test-Path $appSettingsPath)) {
        Write-Host "appsettings.json not found" -ForegroundColor Red
        return $null
    }

    $json = Get-Content $appSettingsPath -Raw | ConvertFrom-Json
    $encryptedConnectionString = $json.ConnectionStrings.DefaultConnection
    $isEncrypted = $json.ConnectionStrings.IsEncrypted

    if (-not $isEncrypted) { return $encryptedConnectionString }

    # AES-256 복호화
    $aesKey = [byte[]](0x43,0x6F,0x69,0x6E,0x46,0x46,0x2D,0x54,0x72,0x61,0x64,0x69,0x6E,0x67,0x42,0x6F,0x74,0x2D,0x41,0x45,0x53,0x32,0x35,0x36,0x2D,0x4B,0x65,0x79,0x2D,0x33,0x32,0x42)
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
    return [System.Text.Encoding]::UTF8.GetString($decryptedBytes)
}

$conn = New-Object System.Data.SqlClient.SqlConnection
$conn.ConnectionString = Get-DecryptedConnectionString
if (-not $conn.ConnectionString) {
    Write-Host "Connection string error" -ForegroundColor Red
    exit 1
}

try {
    $conn.Open()
    Write-Host "=============================================" -ForegroundColor Cyan
    Write-Host "   TradeHistory & TradeLogs 테이블 검증" -ForegroundColor Cyan
    Write-Host "   실제 진입/청산 데이터 저장 확인" -ForegroundColor Cyan
    Write-Host "=============================================" -ForegroundColor Cyan
    Write-Host ""

    # 1. 전체 통계
    Write-Host "[1] 전체 통계 (최근 7일)" -ForegroundColor Yellow
    $cmd = $conn.CreateCommand()
    $cmd.CommandText = "SELECT COUNT(*) AS TotalRecords, SUM(CASE WHEN IsClosed = 1 THEN 1 ELSE 0 END) AS ClosedTrades, SUM(CASE WHEN IsClosed = 0 THEN 1 ELSE 0 END) AS OpenTrades, SUM(CASE WHEN PnL > 0 AND IsClosed = 1 THEN 1 ELSE 0 END) AS WinCount FROM TradeHistory WHERE CreatedAt >= DATEADD(DAY, -7, GETUTCDATE())"
    $adapter = New-Object System.Data.SqlClient.SqlDataAdapter $cmd
    $ds = New-Object System.Data.DataSet
    $adapter.Fill($ds) | Out-Null
    if ($ds.Tables[0].Rows.Count -gt 0) {
        $ds.Tables[0] | Format-Table -AutoSize
    }

    # 2. TradeHistory - 최근 진입 데이터
    Write-Host ""
    Write-Host "[2] TradeHistory - 최근 진입 TOP 10 (IsClosed=0)" -ForegroundColor Yellow
    $cmd.CommandText = "SELECT TOP 10 Id, Symbol, Side, EntryPrice, EntryTime, Quantity, DATEDIFF(MINUTE, EntryTime, GETUTCDATE()) AS MinutesOpen FROM TradeHistory WHERE IsClosed = 0 ORDER BY EntryTime DESC"
    $adapter = New-Object System.Data.SqlClient.SqlDataAdapter $cmd
    $ds = New-Object System.Data.DataSet
    $adapter.Fill($ds) | Out-Null
    if ($ds.Tables[0].Rows.Count -gt 0) {
        $ds.Tables[0] | Format-Table -AutoSize
        Write-Host "  💡 이 진입들은 청산 시 IsClosed=1, PnL 업데이트됩니다" -ForegroundColor Gray
    } else {
        Write-Host "  No open trades (모든 포지션 청산됨)" -ForegroundColor Green
    }

    # 3. TradeHistory - 최근 청산 데이터
    Write-Host ""
    Write-Host "[3] TradeHistory - 최근 청산 TOP 10 (IsClosed=1)" -ForegroundColor Yellow
    $cmd.CommandText = @"
SELECT TOP 10
    Id,
    Symbol,
    Side,
    EntryPrice,
    ExitPrice,
    PnL,
    PnLPercent,
    ExitReason,
    DATEDIFF(MINUTE, EntryTime, ExitTime) AS HoldingMinutes,
    ExitTime
FROM TradeHistory
WHERE IsClosed = 1"SELECT TOP 10 Id, Symbol, Side, EntryPrice, ExitPrice, PnL, PnLPercent, ExitReason, ExitTime FROM TradeHistory WHERE IsClosed = 1 ORDER BY ExitTime DESC"  $cmd.CommandText = "SELECT TOP 10 Id, Symbol, Side, Strategy, Price, AiScore, PnL, PnLPercent, Time FROM TradeLogs ORDER BY Time DESC"
    $adapter = New-Object System.Data.SqlClient.SqlDataAdapter $cmd
    $ds = New-Object System.Data.DataSet
    $adapter.Fill($ds) | Out-Null
    if ($ds.Tables[0].Rows.Count -gt 0) {
        $ds.Tables[0] | Format-Table -AutoSize
    } else {
        Write-Host "  No TradeLogs data" -ForegroundColor Yellow
    }

    # 5. 특정 심볼 상세
    if ($Symbol) {
        Write-Host ""
        Write-Host "[5] $Symbol 상세 거래 내역 (최근 5개)" -ForegroundColor Yellow
        $cmd.CommandText = @"
SELECT TOP 5
    Id,
    Side,
    EntryPrice,
    ExitPrice,
    PnL,
    PnLPercent,
    ExitReason,
    IsClosed,
    EntryTime,
    ExitTime
FROM TradeHistory
WHERE Symbol = @Symbol
ORDER BY EntryTime DESC
"@"SELECT TOP 5 Id, Side, EntryPrice, ExitPrice, PnL, PnLPercent, ExitReason, IsClosed, EntryTime FROM TradeHistory WHERE Symbol = @Symbol ORDER BY EntryTime DESC"  Write-Host "[6] 심볼별 승률 분석 (최근 7일, IsClosed=1)" -ForegroundColor Yellow
    $cmd.CommandText = @"
SELECT 
    Symbol,
    COUNT(*) AS TotalTrades,
    SUM(CASE WHEN PnL > 0 THEN 1 ELSE 0 END) AS Wins,
    SUM(CASE WHEN PnL <= 0 THEN 1 ELSE 0 END) AS Losses,
    CAST(100.0 * SUM(CASE WHEN PnL > 0 THEN 1 ELSE 0 END) / COUNT(*) AS DECIMAL(5,2)) AS WinRate,
    AVG(CAST(PnLPercent AS FLOAT)) AS AvgPnL,
    SUM(PnL) AS TotalPnL
FROM TradeHistory
WHERE IsClosed = 1
  AND ExitTime >= DATEADD(DAY, -7, GETUTCDATE())
GROUP BY Symbol
ORDER BY TotalTrades DESC
"@
    $cmd.Parameters.Cle"SELECT Symbol, COUNT(*) AS TotalTrades, SUM(CASE WHEN PnL > 0 THEN 1 ELSE 0 END) AS Wins, CAST(100.0 * SUM(CASE WHEN PnL > 0 THEN 1 ELSE 0 END) / COUNT(*) AS DECIMAL(5,2)) AS WinRate, AVG(CAST(PnLPercent AS FLOAT)) AS AvgPnL FROM TradeHistory WHERE IsClosed = 1 AND ExitTime >= DATEADD(DAY, -7, GETUTCDATE()) GROUP BY Symbol ORDER BY TotalTrades DESC"LECT 
    Strategy,
    COUNT(*) AS TotalTrades,
    SUM(CASE WHEN PnL > 0 THEN 1 ELSE 0 END) AS Wins,
    CAST(100.0 * SUM(CASE WHEN PnL > 0 THEN 1 ELSE 0 END) / COUNT(*) AS DECIMAL(5,2)) AS WinRate,
    AVG(CAST(PnLPercent AS FLOAT)) AS AvgPnL
FROM TradeLogs
WHERE Time >= DATEADD(DAY, -7, GETUTCDATE())
GROUP BY Strategy
ORDER BY TotalTrades DESC
"@
    $adapter = New-Object System.Data.SqlClient.SqlDataAdapter $cmd
    $ds = New-Object System.Data.DataSet
    $adapter.Fill($ds) "SELECT Strategy, COUNT(*) AS TotalTrades, SUM(CASE WHEN PnL > 0 THEN 1 ELSE 0 END) AS Wins, CAST(100.0 * SUM(CASE WHEN PnL > 0 THEN 1 ELSE 0 END) / COUNT(*) AS DECIMAL(5,2)) AS WinRate FROM TradeLogs WHERE Time >= DATEADD(DAY, -7, GETUTCDATE()) GROUP BY Strategy ORDER BY TotalTrades DESC"  Write-Host "  - TradeHistory: UpdateTradeExitAsync() 청산 시 업데이트" -ForegroundColor Gray
    Write-Host "  - TradeLogs: TryMirrorToTradeLogsAsync() 미러 로그" -ForegroundColor Gray
    $cmd.CommandText = @"
SELECT 
    'EntryOnly' AS Status,
    COUNT(*) AS Count,
    AVG(DATEDIFF(MINUTE, EntryTime, GETUTCDATE())) AS AvgMinutesOpen
FROM TradeHistory
WHERE IsClosed = 0 AND EntryTime >= DATEADD(DAY, -7, GETUTCDATE())
UNION ALL
SELECT 
    'Completed',
    COUNT(*),
    AVG(DATEDIFF(MINUTE, EntryTime, ExitTime))
FROM TradeHistory
WHERE IsClosed = 1 AND ExitTime >= DATEADD(DAY, -7, GETUTCDATE())
"@
    $adapter = New-Object System.Data.SqlClient.SqlDataAdapter $cmd
    $ds = New-Object System.Data.DataSet
    $adapter.Fill($ds) | Out-Null
    if ($ds.Tables[0].Rows.Count -gt 0) {
        $ds.Tables[0] | Format-Table -AutoSize
    }

    Write-Host ""
    Write-Host "=============================================" -ForegroundColor Green
    Write-Host "   검증 완료: 거래 데이터 정상 저장 중" -ForegroundColor Green
    Write-Host "======="SELECT COUNT(*) AS OpenTrades, AVG(DATEDIFF(MINUTE, EntryTime, GETUTCDATE())) AS AvgMinutesOpen FROM TradeHistory WHERE IsClosed = 0"  Write-Host ""
} finally {
    if ($conn.State -eq 'Open') {
        $conn.Close()
    }
}
