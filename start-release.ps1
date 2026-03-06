#!/usr/bin/env pwsh
<#
.SYNOPSIS
    🚀 배포 시작 스크립트 - 체크리스트를 확인하고 배포를 시작합니다.

.DESCRIPTION
    이 스크립트는 RELEASE_CHECKLIST.md를 열어주고, 배포에 필요한 단계를 안내합니다.
    배포 전에 반드시 실행하여 누락된 단계가 없는지 확인하세요.

.PARAMETER Version
    배포할 버전 번호 (예: 2.2.2)

.PARAMETER SkipChecklist
    체크리스트 확인을 건너뜁니다 (권장하지 않음)

.EXAMPLE
    .\start-release.ps1 -Version "2.2.2"

.EXAMPLE
    .\start-release.ps1 -Version "2.2.2" -SkipChecklist
#>

param(
    [Parameter(Mandatory = $false)]
    [string]$Version,
    
    [Parameter(Mandatory = $false)]
    [switch]$SkipChecklist
)

$ErrorActionPreference = "Stop"

function Write-Header {
    param([string]$Message)
    Write-Host "`n" -ForegroundColor Cyan
    Write-Host "━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━" -ForegroundColor Cyan
    Write-Host " $Message" -ForegroundColor Cyan
    Write-Host "━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━" -ForegroundColor Cyan
    Write-Host ""
}

function Write-Checklist {
    Write-Host "📋 배포 체크리스트" -ForegroundColor Yellow
    Write-Host ""
    Write-Host "1단계: 버전 업데이트 및 빌드" -ForegroundColor White
    Write-Host "  ☐ TradingBot.csproj 버전 업데이트" -ForegroundColor Gray
    Write-Host "  ☐ CHANGELOG.md 업데이트" -ForegroundColor Gray
    Write-Host "  ☐ dotnet build -c Release 실행" -ForegroundColor Gray
    Write-Host ""
    Write-Host "2단계: Git 커밋 및 동기화" -ForegroundColor White
    Write-Host "  ☐ git add . && git commit -m 'Release vX.X.X'" -ForegroundColor Gray
    Write-Host "  ☐ git push Tradingbot main" -ForegroundColor Gray
    Write-Host ""
    Write-Host "3단계: Git 태그 생성" -ForegroundColor White
    Write-Host "  ☐ git tag -s vX.X.X -m 'Release vX.X.X'" -ForegroundColor Gray
    Write-Host "  ☐ git push Tradingbot vX.X.X" -ForegroundColor Gray
    Write-Host ""
    Write-Host "4단계: Velopack 배포 및 패키징" -ForegroundColor White
    Write-Host "  ☐ dotnet publish" -ForegroundColor Gray
    Write-Host "  ☐ .\publish-and-release.ps1 실행" -ForegroundColor Gray
    Write-Host ""
    Write-Host "5단계: GitHub 릴리스 업로드" -ForegroundColor White
    Write-Host "  ☐ gh release upload" -ForegroundColor Gray
    Write-Host "  ☐ gh release edit --latest" -ForegroundColor Gray
    Write-Host ""
}

Write-Header "🚀 TradingBot 릴리스 배포 시작"

# 버전 확인
if ([string]::IsNullOrWhiteSpace($Version)) {
    # .csproj에서 버전 읽기
    $csprojPath = Join-Path $PSScriptRoot "TradingBot.csproj"
    if (Test-Path $csprojPath) {
        [xml]$csproj = Get-Content $csprojPath
        $currentVersion = $csproj.Project.PropertyGroup.Version
        Write-Host "현재 프로젝트 버전: $currentVersion" -ForegroundColor Cyan
        Write-Host ""
    }
    
    $Version = Read-Host "배포할 버전을 입력하세요 (예: 2.2.2)"
    
    if ([string]::IsNullOrWhiteSpace($Version)) {
        Write-Host "❌ 버전을 입력해야 합니다." -ForegroundColor Red
        exit 1
    }
}

Write-Host "배포 버전: v$Version" -ForegroundColor Green
Write-Host ""

# 체크리스트 표시
if (-not $SkipChecklist) {
    Write-Checklist
    
    # RELEASE_CHECKLIST.md 열기
    $checklistPath = Join-Path $PSScriptRoot "RELEASE_CHECKLIST.md"
    if (Test-Path $checklistPath) {
        Write-Host "💡 RELEASE_CHECKLIST.md를 열겠습니까?" -ForegroundColor Cyan
        $openChecklist = Read-Host "[Y/N]"
        
        if ($openChecklist -eq "Y" -or $openChecklist -eq "y") {
            Write-Host "📄 RELEASE_CHECKLIST.md를 엽니다..." -ForegroundColor Cyan
            
            if (Get-Command code -ErrorAction SilentlyContinue) {
                code $checklistPath
            } else {
                Start-Process $checklistPath
            }
            
            Write-Host ""
            Write-Host "체크리스트를 확인하고 Enter를 눌러 계속하세요..." -ForegroundColor Yellow
            Read-Host
        }
    }
    
    Write-Host ""
    Write-Host "━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━" -ForegroundColor Cyan
    Write-Host ""
}

# 배포 명령어 출력
Write-Header "📝 배포 명령어 (순서대로 실행하세요)"

Write-Host "# 1. Publish" -ForegroundColor Yellow
Write-Host "dotnet publish TradingBot.csproj -c Release -r win-x64 --self-contained -p:PublishSingleFile=false -o `"bin\publish\win-x64`"" -ForegroundColor White
Write-Host ""

Write-Host "# 2. Velopack 패키징 (체크리스트 확인 포함)" -ForegroundColor Yellow
Write-Host ".\publish-and-release.ps1 -PublishPath `"bin\publish\win-x64`" -Version `"$Version`"" -ForegroundColor White
Write-Host ""

Write-Host "# 3. GitHub 릴리스 파일 업로드" -ForegroundColor Yellow
Write-Host "gh release upload v$Version ``" -ForegroundColor White
Write-Host "  `"Releases\TradingBot-win-Setup.exe`" ``" -ForegroundColor White
Write-Host "  `"Releases\TradingBot-$Version-full.nupkg`" ``" -ForegroundColor White
Write-Host "  `"Releases\TradingBot-win-Portable.zip`" ``" -ForegroundColor White
Write-Host "  `"Releases\RELEASES`" ``" -ForegroundColor White
Write-Host "  `"Releases\releases.win.json`" ``" -ForegroundColor White
Write-Host "  `"Releases\assets.win.json`" ``" -ForegroundColor White
Write-Host "  --clobber" -ForegroundColor White
Write-Host ""

Write-Host "# 4. Latest로 설정" -ForegroundColor Yellow
Write-Host "gh release edit v$Version --latest" -ForegroundColor White
Write-Host ""

Write-Host "━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━" -ForegroundColor Cyan
Write-Host ""

# 원라인 배포 옵션
Write-Host "💡 원라인 배포 (버전 업데이트 후):" -ForegroundColor Cyan
Write-Host ""
$oneLiner = "`$v=`"$Version`"; dotnet publish TradingBot.csproj -c Release -r win-x64 --self-contained -p:PublishSingleFile=false -o `"bin\publish\win-x64`"; .\publish-and-release.ps1 -PublishPath `"bin\publish\win-x64`" -Version `$v; gh release upload v`$v `"Releases\TradingBot-win-Setup.exe`" `"Releases\TradingBot-`$v-full.nupkg`" `"Releases\TradingBot-win-Portable.zip`" `"Releases\RELEASES`" `"Releases\releases.win.json`" `"Releases\assets.win.json`" --clobber; gh release edit v`$v --latest"
Write-Host $oneLiner -ForegroundColor Gray
Write-Host ""

# 클립보드 복사
Write-Host "명령어를 클립보드에 복사하시겠습니까?" -ForegroundColor Cyan
$copyToClipboard = Read-Host "[Y/N]"

if ($copyToClipboard -eq "Y" -or $copyToClipboard -eq "y") {
    $oneLiner | Set-Clipboard
    Write-Host "✅ 클립보드에 복사되었습니다!" -ForegroundColor Green
    Write-Host "💡 Tip: PowerShell에서 붙여넣기 후 실행하세요." -ForegroundColor Yellow
}

Write-Host ""
Write-Host "━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━" -ForegroundColor Green
Write-Host "✅ 배포 준비 완료! 위의 명령어를 순서대로 실행하세요." -ForegroundColor Green
Write-Host "━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━" -ForegroundColor Green
Write-Host ""
