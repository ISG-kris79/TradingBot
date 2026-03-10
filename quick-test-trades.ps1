# TradeHistory & TradeLogs 간단 체크
param([string]$Symbol = "BTCUSDT")

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
$conn.Open()

Write-Host "=== TradeHistory & TradeLogs Check ===" -ForegroundColor Cyan
Write-Host ""

# 1. TradeHistory 통계
Write-Host "[1] TradeHistory Statistics" -ForegroundColor Yellow
$cmd = $conn.CreateCommand()
$cmd.CommandText = "SELECT COUNT(*) AS Total, SUM(CASE WHEN IsClosed=1 THEN 1 ELSE 0 END) AS Closed, SUM(CASE WHEN IsClosed=0 THEN 1 ELSE 0 END) AS Open FROM TradeHistory"
$reader = $cmd.ExecuteReader()
if ($reader.Read()) {
    Write-Host "Total: $($reader['Total']) | Closed: $($reader['Closed']) | Open: $($reader['Open'])" -ForegroundColor Green
}
$reader.Close()

# 2. TradeHistory 최근 진입
Write-Host ""
Write-Host "[2] Recent Open Trades (TOP 5)" -ForegroundColor Yellow
$cmd.CommandText = "SELECT TOP 5 Id, Symbol, Side, EntryPrice, EntryTime, IsClosed FROM TradeHistory WHERE IsClosed=0 ORDER BY EntryTime DESC"
$adapter = New-Object System.Data.SqlClient.SqlDataAdapter $cmd
$ds = New-Object System.Data.DataSet
$adapter.Fill($ds) | Out-Null
if ($ds.Tables[0].Rows.Count -gt 0) {
    $ds.Tables[0] | Format-Table -AutoSize
} else {
    Write-Host "No open trades" -ForegroundColor Green
}

# 3. TradeHistory 최근 청산
Write-Host "[3] Recent Closed Trades (TOP 5)" -ForegroundColor Yellow
$cmd.CommandText = "SELECT TOP 5 Id, Symbol, Side, EntryPrice, ExitPrice, PnL, PnLPercent, ExitReason FROM TradeHistory WHERE IsClosed=1 ORDER BY ExitTime DESC"
$adapter = New-Object System.Data.SqlClient.SqlDataAdapter $cmd
$ds = New-Object System.Data.DataSet
$adapter.Fill($ds) | Out-Null
if ($ds.Tables[0].Rows.Count -gt 0) {
    $ds.Tables[0] | Format-Table -AutoSize
} else {
    Write-Host "No closed trades yet" -ForegroundColor Yellow
}

# 4. TradeLogs 통계
Write-Host "[4] TradeLogs Statistics" -ForegroundColor Yellow
$cmd.CommandText = "SELECT COUNT(*) AS Total, SUM(CASE WHEN PnL>0 THEN 1 ELSE 0 END) AS Wins, SUM(CASE WHEN PnL<=0 THEN 1 ELSE 0 END) AS Losses FROM TradeLogs"
$reader = $cmd.ExecuteReader()
if ($reader.Read()) {
    Write-Host "Total: $($reader['Total']) | Wins: $($reader['Wins']) | Losses: $($reader['Losses'])" -ForegroundColor Green
}
$reader.Close()

# 5. TradeLogs 최근 데이터
Write-Host ""
Write-Host "[5] Recent TradeLogs (TOP 5)" -ForegroundColor Yellow
$cmd.CommandText = "SELECT TOP 5 Id, Symbol, Side, Strategy, Price, PnL, PnLPercent, Time FROM TradeLogs ORDER BY Time DESC"
$adapter = New-Object System.Data.SqlClient.SqlDataAdapter $cmd
$ds = New-Object System.Data.DataSet
$adapter.Fill($ds) | Out-Null
if ($ds.Tables[0].Rows.Count -gt 0) {
    $ds.Tables[0] | Format-Table -AutoSize
} else {
    Write-Host "No TradeLogs data" -ForegroundColor Yellow
}

# 6. 심볼별 승률
Write-Host "[6] Win Rate by Symbol (Closed trades only)" -ForegroundColor Yellow
$cmd.CommandText = "SELECT Symbol, COUNT(*) AS Total, SUM(CASE WHEN PnL>0 THEN 1 ELSE 0 END) AS Wins FROM TradeHistory WHERE IsClosed=1 GROUP BY Symbol ORDER BY Total DESC"
$adapter = New-Object System.Data.SqlClient.SqlDataAdapter $cmd
$ds = New-Object System.Data.DataSet
$adapter.Fill($ds) | Out-Null
if ($ds.Tables[0].Rows.Count -gt 0) {
    $ds.Tables[0] | Format-Table -AutoSize
} else {
    Write-Host "No completed trades" -ForegroundColor Yellow
}

$conn.Close()
Write-Host ""
Write-Host "=== Check Complete ===" -ForegroundColor Green
Write-Host ""
Write-Host "Learning Data Query Example:" -ForegroundColor Cyan
Write-Host "  await _dbManager.GetCompletedTradesForTrainingAsync(daysBack: 30)" -ForegroundColor Gray
