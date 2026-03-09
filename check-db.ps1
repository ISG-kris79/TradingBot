param(
    [string]$Symbol = "XRPUSDT"
)

function Get-DecryptedConnectionString {
    try {
        # appsettings.json 읽기
        $appSettingsPath = Join-Path $PSScriptRoot "appsettings.json"
        if (-not (Test-Path $appSettingsPath)) {
            Write-Host "appsettings.json not found at: $appSettingsPath" -ForegroundColor Red
            return $null
        }

        $json = Get-Content $appSettingsPath -Raw | ConvertFrom-Json
        $encryptedConnectionString = $json.ConnectionStrings.DefaultConnection
        $isEncrypted = $json.ConnectionStrings.IsEncrypted

        # 암호화되지 않은 경우 그대로 반환
        if (-not $isEncrypted) {
            return $encryptedConnectionString
        }

        # AES-256 복호화 (SharedServicesCompat.cs의 SecurityService.DecryptString 로직)
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
    $adapter = New-Object System.Data.SqlClient.SqlDataAdapter $cmd
    $dataset = New-Object System.Data.DataSet
    [void]$adapter.Fill($dataset)
    
    $conn.Close()
    return $dataset.Tables[0]
}

Write-Host "========================================" -ForegroundColor Cyan
Write-Host "DB Check: TradeHistory & TradeLogs" -ForegroundColor Cyan
Write-Host "========================================" -ForegroundColor Cyan
Write-Host ""

try {
    Write-Host "[1] TradeHistory recent 10 rows:" -ForegroundColor Yellow
    $history = Invoke-Query "SELECT TOP 10 Id, UserId, Symbol, Side, EntryPrice, ExitPrice, PnL, PnLPercent, ExitReason, IsClosed FROM TradeHistory ORDER BY Id DESC"
    if ($history.Rows.Count -gt 0) {
        $history | Format-Table -AutoSize
    } else {
        Write-Host "No data" -ForegroundColor Red
    }

    Write-Host ""
    Write-Host "[2] TradeHistory for $Symbol" ":" -ForegroundColor Yellow
    $symbolData = Invoke-Query "SELECT TOP 5 Id, UserId, Symbol, EntryPrice, ExitPrice, PnL, PnLPercent, ExitReason FROM TradeHistory WHERE Symbol='$Symbol' ORDER BY Id DESC"
    if ($symbolData.Rows.Count -gt 0) {
        $symbolData | Format-Table -AutoSize
    } else {
        Write-Host "No $Symbol data" -ForegroundColor Red
    }

    Write-Host ""
    Write-Host "[3] TradeLogs recent 10 rows:" -ForegroundColor Yellow
    # TradeLogs 기본 스키마 컬럼만 조회 (ExitReason은 선택적 컬럼이므로 제외)
    $logs = Invoke-Query "SELECT TOP 10 Id, Symbol, Side, Strategy, Price, AiScore, Time, PnL, PnLPercent FROM TradeLogs ORDER BY Id DESC"
    if ($logs.Rows.Count -gt 0) {
        $logs | Format-Table -AutoSize
    } else {
        Write-Host "No data" -ForegroundColor Red
    }

    Write-Host ""
    Write-Host "[4] Statistics:" -ForegroundColor Yellow
    $stats = Invoke-Query "SELECT 'TradeHistory' AS [Table], COUNT(*) AS Total FROM TradeHistory UNION ALL SELECT 'TradeLogs', COUNT(*) FROM TradeLogs"
    $stats | Format-Table -AutoSize

    Write-Host ""
    Write-Host "========================================" -ForegroundColor Cyan
    Write-Host "Done" -ForegroundColor Green
    Write-Host "========================================" -ForegroundColor Cyan
}
catch {
    Write-Host ""
    Write-Host "Error: $($_.Exception.Message)" -ForegroundColor Red
    Write-Host ""
    Write-Host "Check:" -ForegroundColor Yellow
    Write-Host "  1. SQL Server running" -ForegroundColor Yellow
    Write-Host "  2. TradingBot database exists" -ForegroundColor Yellow
    exit 1
}
