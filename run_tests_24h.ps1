$stopTime = (Get-Date).AddHours(24)
$logFile = "test_results.log"

while ((Get-Date) -lt $stopTime) {
    dotnet test TradingBot.Tests/TradingBot.Tests.csproj | Out-File -FilePath $logFile -Append
    if ($LASTEXITCODE -ne 0) {
        "Tests failed. Stopping execution." | Out-File -FilePath $logFile -Append
        break
    }
    Start-Sleep -Seconds 5
}
"Test run finished." | Out-File -FilePath $logFile -Append
