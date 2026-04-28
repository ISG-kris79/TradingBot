[Console]::OutputEncoding = [Text.Encoding]::UTF8
$dir = "$env:LOCALAPPDATA\TradingBot\Models"
Write-Host "==== Models folder (sorted by LastWriteTime DESC) ====" -ForegroundColor Cyan
Get-ChildItem $dir -File | Sort-Object LastWriteTime -Descending |
    Select-Object Name, @{N='KB';E={[int]($_.Length/1KB)}}, LastWriteTime | Format-Table -AutoSize
