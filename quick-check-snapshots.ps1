# TradePatternSnapshots 간단 체크
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

# 1. 전체 통계
Write-Host "=== TradePatternSnapshots 통계 ===" -ForegroundColor Cyan
$cmd = $conn.CreateCommand()
$cmd.CommandText = "SELECT COUNT(*) AS Total, SUM(CASE WHEN Label=1 THEN 1 ELSE 0 END) AS Success, SUM(CASE WHEN Label=0 THEN 1 ELSE 0 END) AS Fail, SUM(CASE WHEN Label IS NULL THEN 1 ELSE 0 END) AS Pending FROM dbo.TradePatternSnapshots"
$reader = $cmd.ExecuteReader()
if ($reader.Read()) {
    Write-Host "Total: $($reader['Total']) | Success: $($reader['Success']) | Fail: $($reader['Fail']) | Pending: $($reader['Pending'])" -ForegroundColor Green
}
$reader.Close()

# 2. 최근 라벨링 데이터
Write-Host ""
Write-Host "=== 최근 라벨링 데이터 TOP 5 ===" -ForegroundColor Cyan
$cmd.CommandText = "SELECT TOP 5 Id, Symbol, Side, EntryTime, EntryPrice, Label, PnLPercent, ExitReason FROM dbo.TradePatternSnapshots WHERE Label IS NOT NULL ORDER BY UpdatedAt DESC"
$adapter = New-Object System.Data.SqlClient.SqlDataAdapter $cmd
$ds = New-Object System.Data.DataSet
$adapter.Fill($ds) | Out-Null
if ($ds.Tables[0].Rows.Count -gt 0) {
    $ds.Tables[0] | Format-Table -AutoSize
} else {
    Write-Host "No labeled data" -ForegroundColor Yellow
}

# 3. 대기 중인 진입
Write-Host "=== 대기 중인 진입 TOP 5 ===" -ForegroundColor Cyan
$cmd.CommandText = "SELECT TOP 5 Id, Symbol, Side, EntryTime, EntryPrice, FinalScore FROM dbo.TradePatternSnapshots WHERE Label IS NULL ORDER BY EntryTime DESC"
$adapter = New-Object System.Data.SqlClient.SqlDataAdapter $cmd
$ds = New-Object System.Data.DataSet
$adapter.Fill($ds) | Out-Null
if ($ds.Tables[0].Rows.Count -gt 0) {
    $ds.Tables[0] | Format-Table -AutoSize
} else {
    Write-Host "No pending" -ForegroundColor Green
}

$conn.Close()
Write-Host ""
Write-Host "OK: DB snapshot collection working" -ForegroundColor Green
