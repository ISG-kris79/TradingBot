# Deprecated: External AI service projects were removed.
# - TradingBot.MLService: removed
# - TradingBot.TorchService: removed
# AI inference/training now runs in-process (internal background tasks).

Write-Host "[DEPRECATED] setup-ai-services.ps1" -ForegroundColor Yellow
Write-Host "External service projects are removed." -ForegroundColor Yellow
Write-Host "Use the main app build only:" -ForegroundColor Gray
Write-Host "  dotnet build TradingBot.csproj -c Debug" -ForegroundColor Gray
