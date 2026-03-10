# 2026-03-10 10:00 ~ 11:00 거래 분석
param([string]$Date = "2026-03-10")

function Get-DecryptedConnectionString {
    $appSettingsPath = Join-Path $PSScriptRoot "appsettings.json"
    $json = Get-Content $appSettingsPath -Raw | ConvertFrom-Json
    $encryptedConnectionString = $json.ConnectionStrings.DefaultConnection
    $isEncrypted = $json.ConnectionStrings.IsEncrypted
    if (-not $isEncrypted) { return $encryptedConnectionString }
    
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
try {
    $conn.Open()
} catch {
    Write-Host "❌ DB 연결 실패: $_" -ForegroundColor Red
    exit 1
}

$startTime = "$Date 10:00:00"
$endTime = "$Date 11:00:00"

Write-Host ""
Write-Host "═══════════════════════════════════════════════════════════" -ForegroundColor Cyan
Write-Host "   $Date 10:00 ~ 11:00 거래 기록 분석" -ForegroundColor Cyan
Write-Host "═══════════════════════════════════════════════════════════" -ForegroundColor Cyan
Write-Host ""

# TradeLogs 조회
Write-Host "[1] TradeLogs 진입 신호 기록" -ForegroundColor Yellow
$cmd = $conn.CreateCommand()
$cmd.CommandText = "SELECT Symbol, Side, Strategy, CONVERT(TIME, [Time]) AS Time, Price, AiScore, PnL FROM dbo.TradeLogs WHERE [Time] >= @Start AND [Time] < @End ORDER BY [Time]"
$cmd.Parameters.AddWithValue("@Start", $startTime) | Out-Null
$cmd.Parameters.AddWithValue("@End", $endTime) | Out-Null

$adapter = New-Object System.Data.SqlClient.SqlDataAdapter($cmd)
$ds = New-Object System.Data.DataSet
$adapter.Fill($ds) | Out-Null

if ($ds.Tables[0].Rows.Count -gt 0) {
    Write-Host "✓ 발견: $($ds.Tables[0].Rows.Count)건" -ForegroundColor Green
    $ds.Tables[0] | Format-Table -AutoSize
} else {
    Write-Host "✗ 10시~11시 TradeLogs 기록 없음" -ForegroundColor Red
}

Write-Host ""

# TradeHistory 조회
Write-Host "[2] TradeHistory 진입 포지션" -ForegroundColor Yellow
$cmd.CommandText = "SELECT Id, Symbol, Side, EntryPrice, CONVERT(TIME, EntryTime) AS Time, IsClosed FROM dbo.TradeHistory WHERE EntryTime >= @Start AND EntryTime < @End"
$cmd.Parameters.Clear()
$cmd.Parameters.AddWithValue("@Start", $startTime) | Out-Null
$cmd.Parameters.AddWithValue("@End", $endTime) | Out-Null

$adapter = New-Object System.Data.SqlClient.SqlDataAdapter($cmd)
$ds = New-Object System.Data.DataSet
$adapter.Fill($ds) | Out-Null

if ($ds.Tables[0].Rows.Count -gt 0) {
    Write-Host "✓ 진입 포지션: $($ds.Tables[0].Rows.Count)건" -ForegroundColor Green
    $ds.Tables[0] | Format-Table -AutoSize
} else {
    Write-Host "✗ 10시~11시 진입 포지션 없음 → Gate 차단됨" -ForegroundColor Red
    Write-Host ""
    Write-Host "   원인 추정:" -ForegroundColor Gray
    Write-Host "   · WaveGate 신뢰도 기준 초과 (기존: 55%, 신규: 50%)" -ForegroundColor Gray
    Write-Host "   · RSI 다이버전스 미충족 (감도: ±3.0→±0.5)" -ForegroundColor Gray
    Write-Host "   · 피보나치 레벨 미충족 (0.382~0.786 확장됨)" -ForegroundColor Gray
}

Write-Host ""

# 최근 거래 트렌드
Write-Host "[3] 최근 3시간 거래량 (비교용)" -ForegroundColor Yellow
$cmd.CommandText = "SELECT TOP 10 Symbol, COUNT(*) AS Trades FROM dbo.TradeLogs WHERE [Time] >= DATEADD(HOUR, -3, @Start) GROUP BY Symbol ORDER BY Trades DESC"
$cmd.Parameters.Clear()
$cmd.Parameters.AddWithValue("@Start", $startTime) | Out-Null

$adapter = New-Object System.Data.SqlClient.SqlDataAdapter($cmd)
$ds = New-Object System.Data.DataSet
$adapter.Fill($ds) | Out-Null

if ($ds.Tables[0].Rows.Count -gt 0) {
    $ds.Tables[0] | Format-Table -AutoSize
}

$conn.Close()

Write-Host ""
Write-Host "═══════════════════════════════════════════════════════════" -ForegroundColor Green
Write-Host "✅ 분석 완료" -ForegroundColor Green
Write-Host "═══════════════════════════════════════════════════════════" -ForegroundColor Green
Write-Host ""
Write-Host "💡 다음: v2.4.10 배포 후 재운영 모니터링" -ForegroundColor Cyan
