param(
    [string]$Symbol = "XRPUSDT"
)

function Invoke-Query($query) {
    $conn = New-Object System.Data.SqlClient.SqlConnection
    $conn.ConnectionString = "Server=localhost;Database=TradingBot;Integrated Security=True;TrustServerCertificate=True"
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
    $logs = Invoke-Query "SELECT TOP 10 Id, Symbol, Side, Strategy, Price, PnL, PnLPercent, ExitReason FROM TradeLogs ORDER BY Id DESC"
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
