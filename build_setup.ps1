#!/usr/bin/env pwsh
# TradingBot Setup.exe 생성 스크립트 (PowerShell)

Write-Host "========================================" -ForegroundColor Cyan
Write-Host "TradingBot Setup.exe 생성 스크립트" -ForegroundColor Cyan
Write-Host "========================================" -ForegroundColor Cyan
Write-Host ""

# 버전 입력 받기
$version = Read-Host "버전을 입력하세요 (예: 1.0.0)"

if ([string]::IsNullOrWhiteSpace($version)) {
    Write-Host "에러: 버전을 입력해야 합니다." -ForegroundColor Red
    Read-Host "Press Enter to exit"
    exit 1
}

Write-Host ""
Write-Host "[1/4] Velopack 도구 확인 중..." -ForegroundColor Yellow

# Velopack 도구 확인
$vpkExists = Get-Command vpk -ErrorAction SilentlyContinue
if (-not $vpkExists) {
    Write-Host "Velopack 도구가 설치되지 않았습니다. 설치 중..." -ForegroundColor Yellow
    dotnet tool install -g vpk
    if ($LASTEXITCODE -ne 0) {
        Write-Host "에러: Velopack 설치 실패" -ForegroundColor Red
        Read-Host "Press Enter to exit"
        exit 1
    }
}
Write-Host "Velopack 도구 확인 완료" -ForegroundColor Green

Write-Host ""
Write-Host "[2/4] 애플리케이션 퍼블리시 중..." -ForegroundColor Yellow
dotnet publish TradingBot/TradingBot.csproj -c Release -r win-x64 --self-contained -o ./publish
if ($LASTEXITCODE -ne 0) {
    Write-Host "에러: 퍼블리시 실패" -ForegroundColor Red
    Read-Host "Press Enter to exit"
    exit 1
}
Write-Host "퍼블리시 완료" -ForegroundColor Green

Write-Host ""
Write-Host "[3/4] Velopack으로 패키징 중..." -ForegroundColor Yellow
vpk pack -u TradingBot -v $version -p ./publish -e TradingBot.exe
if ($LASTEXITCODE -ne 0) {
    Write-Host "에러: 패키징 실패" -ForegroundColor Red
    Read-Host "Press Enter to exit"
    exit 1
}
Write-Host "패키징 완료" -ForegroundColor Green

Write-Host ""
Write-Host "[4/4] 정리 중..." -ForegroundColor Yellow
Write-Host ""
Write-Host "========================================" -ForegroundColor Cyan
Write-Host "빌드 완료!" -ForegroundColor Green
Write-Host "========================================" -ForegroundColor Cyan
Write-Host ""
Write-Host "생성된 파일:" -ForegroundColor Yellow
Get-ChildItem Releases/*Setup.exe -ErrorAction SilentlyContinue | Format-Table Name, @{Label="Size(MB)";Expression={[math]::Round($_.Length/1MB,2)}}, LastWriteTime -AutoSize
Write-Host ""
$setupFile = Get-ChildItem Releases/*Setup.exe -ErrorAction SilentlyContinue | Select-Object -First 1
if ($setupFile) {
    Write-Host "설치 파일: $($setupFile.FullName)" -ForegroundColor Green
    Write-Host "설치 방법: 해당 파일을 더블클릭하여 설치하세요." -ForegroundColor Cyan
}
Write-Host ""
Read-Host "Press Enter to exit"
