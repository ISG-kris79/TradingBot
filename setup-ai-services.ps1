# AI 서비스 프로젝트 생성 스크립트

Write-Host "========================================" -ForegroundColor Cyan
Write-Host "AI Service Projects Setup" -ForegroundColor Cyan
Write-Host "========================================" -ForegroundColor Cyan
Write-Host ""

$baseDir = "E:\PROJECT\CoinFF\TradingBot"
$solutionFile = "$baseDir\TradingBot\TradingBot.sln"

# 1. MLService 프로젝트 생성
Write-Host "[1/6] Creating MLService project..." -ForegroundColor Yellow
Set-Location $baseDir

if (Test-Path "$baseDir\TradingBot.MLService") {
    Write-Host "  MLService project already exists, skipping..." -ForegroundColor Gray
} else {
    dotnet new console -n TradingBot.MLService -f net9.0
    Set-Location "$baseDir\TradingBot.MLService"
    
    # 패키지 추가
    dotnet add package Microsoft.ML
    dotnet add package System.IO.Pipes
    
    Write-Host "  ✓ MLService project created" -ForegroundColor Green
}

# 2. TorchService 프로젝트 생성
Write-Host "[2/6] Creating TorchService project..." -ForegroundColor Yellow
Set-Location $baseDir

if (Test-Path "$baseDir\TradingBot.TorchService") {
    Write-Host "  TorchService project already exists, skipping..." -ForegroundColor Gray
} else {
    dotnet new console -n TradingBot.TorchService -f net9.0
    Set-Location "$baseDir\TradingBot.TorchService"
    
    # 패키지 추가
    dotnet add package TorchSharp
    dotnet add package TorchSharp-cpu
    dotnet add package System.IO.Pipes
    
    Write-Host "  ✓ TorchService project created" -ForegroundColor Green
}

# 3. Program.cs 파일 복사
Write-Host "[3/6] Copying Program.cs files..." -ForegroundColor Yellow

$mlProgramSource = "$baseDir\TradingBot\Services\ProcessAI\MLService_Program.cs"
$mlProgramDest = "$baseDir\TradingBot.MLService\Program.cs"
Copy-Item $mlProgramSource $mlProgramDest -Force
Write-Host "  ✓ MLService Program.cs copied" -ForegroundColor Green

$torchProgramSource = "$baseDir\TradingBot\Services\ProcessAI\TorchService_Program.cs"
$torchProgramDest = "$baseDir\TradingBot.TorchService\Program.cs"
Copy-Item $torchProgramSource $torchProgramDest -Force
Write-Host "  ✓ TorchService Program.cs copied" -ForegroundColor Green

# 4. AIServiceContracts 공유
Write-Host "[4/6] Setting up shared contracts..." -ForegroundColor Yellow

# MLService에 Contracts 추가 (링크)
$contractsSource = "$baseDir\TradingBot\Services\ProcessAI\AIServiceContracts.cs"
$namedPipeServerSource = "$baseDir\TradingBot\Services\ProcessAI\NamedPipeServer.cs"

# MLService .csproj에 링크 추가
$mlCsproj = "$baseDir\TradingBot.MLService\TradingBot.MLService.csproj"
[xml]$mlXml = Get-Content $mlCsproj
$itemGroup = $mlXml.CreateElement("ItemGroup")
$compile1 = $mlXml.CreateElement("Compile")
$compile1.SetAttribute("Include", "..\TradingBot\Services\ProcessAI\AIServiceContracts.cs")
$compile1.SetAttribute("Link", "AIServiceContracts.cs")
$itemGroup.AppendChild($compile1) | Out-Null
$compile2 = $mlXml.CreateElement("Compile")
$compile2.SetAttribute("Include", "..\TradingBot\Services\ProcessAI\NamedPipeServer.cs")
$compile2.SetAttribute("Link", "NamedPipeServer.cs")
$itemGroup.AppendChild($compile2) | Out-Null
$mlXml.Project.AppendChild($itemGroup) | Out-Null
$mlXml.Save($mlCsproj)

# TorchService .csproj에 링크 추가
$torchCsproj = "$baseDir\TradingBot.TorchService\TradingBot.TorchService.csproj"
[xml]$torchXml = Get-Content $torchCsproj
$itemGroup = $torchXml.CreateElement("ItemGroup")
$compile1 = $torchXml.CreateElement("Compile")
$compile1.SetAttribute("Include", "..\TradingBot\Services\ProcessAI\AIServiceContracts.cs")
$compile1.SetAttribute("Link", "AIServiceContracts.cs")
$itemGroup.AppendChild($compile1) | Out-Null
$compile2 = $torchXml.CreateElement("Compile")
$compile2.SetAttribute("Include", "..\TradingBot\Services\ProcessAI\NamedPipeServer.cs")
$compile2.SetAttribute("Link", "NamedPipeServer.cs")
$itemGroup.AppendChild($compile2) | Out-Null
$torchXml.Project.AppendChild($itemGroup) | Out-Null
$torchXml.Save($torchCsproj)

Write-Host "  ✓ Shared contracts configured" -ForegroundColor Green

# 5. 솔루션에 프로젝트 추가
Write-Host "[5/6] Adding projects to solution..." -ForegroundColor Yellow
Set-Location "$baseDir\TradingBot"

dotnet sln $solutionFile add ..\TradingBot.MLService\TradingBot.MLService.csproj
dotnet sln $solutionFile add ..\TradingBot.TorchService\TradingBot.TorchService.csproj

Write-Host "  ✓ Projects added to solution" -ForegroundColor Green

# 6. 빌드 테스트
Write-Host "[6/6] Building projects..." -ForegroundColor Yellow

Set-Location "$baseDir\TradingBot.MLService"
$mlBuildResult = dotnet build -c Release
if ($LASTEXITCODE -eq 0) {
    Write-Host "  ✓ MLService build successful" -ForegroundColor Green
} else {
    Write-Host "  ✗ MLService build failed" -ForegroundColor Red
}

Set-Location "$baseDir\TradingBot.TorchService"
$torchBuildResult = dotnet build -c Release
if ($LASTEXITCODE -eq 0) {
    Write-Host "  ✓ TorchService build successful" -ForegroundColor Green
} else {
    Write-Host "  ✗ TorchService build failed" -ForegroundColor Red
}

Write-Host ""
Write-Host "========================================" -ForegroundColor Cyan
Write-Host "Setup Complete!" -ForegroundColor Green
Write-Host "========================================" -ForegroundColor Cyan
Write-Host ""
Write-Host "Next steps:" -ForegroundColor Yellow
Write-Host "1. Review generated projects in:" -ForegroundColor White
Write-Host "   - $baseDir\TradingBot.MLService" -ForegroundColor Gray
Write-Host "   - $baseDir\TradingBot.TorchService" -ForegroundColor Gray
Write-Host ""
Write-Host "2. Update TradingEngine.cs to use:" -ForegroundColor White
Write-Host "   - MLServiceClient instead of AIPredictor" -ForegroundColor Gray
Write-Host "   - TorchServiceClient instead of TransformerTrainer" -ForegroundColor Gray
Write-Host ""
Write-Host "3. Test the services:" -ForegroundColor White
Write-Host "   - Run TradingBot and check if services start automatically" -ForegroundColor Gray
Write-Host "   - Monitor logs for IPC communication" -ForegroundColor Gray
Write-Host ""
Write-Host "For details, see: PROCESS_AI_ARCHITECTURE.md" -ForegroundColor Cyan

Set-Location "$baseDir\TradingBot"
