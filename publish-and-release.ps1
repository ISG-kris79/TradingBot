#!/usr/bin/env pwsh
<#
.SYNOPSIS
    Visual Studio 게시 후 자동으로 Velopack 패키징 및 GitHub 릴리스를 생성합니다.

.DESCRIPTION
    이 스크립트는 Visual Studio의 게시 프로필(VelopackRelease.pubxml)에서 자동 실행되며,
    1. Velopack으로 Setup.exe 생성
    2. 릴리스 노트 자동 생성
    3. GitHub CLI로 릴리스 생성 및 파일 업로드

.PARAMETER PublishPath
    Visual Studio가 게시한 출력 디렉터리 경로

.PARAMETER Version
    릴리스 버전 (예: 1.0.0)

.PARAMETER SkipGitHubRelease
    GitHub 릴리스 생성을 건너뜁니다 (Velopack 패키징만 수행)

.EXAMPLE
    .\publish-and-release.ps1 -PublishPath ".\bin\publish" -Version "1.0.1"
#>

param(
    [Parameter(Mandatory = $true)]
    [string]$PublishPath,
    
    [Parameter(Mandatory = $false)]
    [string]$Version,
    
    [Parameter(Mandatory = $false)]
    [switch]$SkipGitHubRelease
)

$ErrorActionPreference = "Stop"

# 색상 출력 함수
function Write-Step {
    param([string]$Message, [string]$Color = "Cyan")
    Write-Host "`n========================================" -ForegroundColor $Color
    Write-Host $Message -ForegroundColor $Color
    Write-Host "========================================" -ForegroundColor $Color
}

function Write-Success {
    param([string]$Message)
    Write-Host "✅ $Message" -ForegroundColor Green
}

function Show-ReleaseChecklist {
    $checklistPath = Join-Path $PSScriptRoot "RELEASE_CHECKLIST.md"
    
    if (Test-Path $checklistPath) {
        Write-Host "`n" -ForegroundColor Yellow
        Write-Host "━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━" -ForegroundColor Yellow
        Write-Host "⚠️  배포 전 체크리스트 확인이 필요합니다!" -ForegroundColor Yellow
        Write-Host "━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━" -ForegroundColor Yellow
        Write-Host ""
        Write-Host "📋 RELEASE_CHECKLIST.md를 확인하셨나요?" -ForegroundColor Cyan
        Write-Host ""
        Write-Host "필수 확인 사항:" -ForegroundColor White
        Write-Host "  ✓ 버전 번호 업데이트 (TradingBot.csproj)" -ForegroundColor Gray
        Write-Host "  ✓ CHANGELOG.md 업데이트" -ForegroundColor Gray
        Write-Host "  ✓ Git 커밋 및 푸시 완료" -ForegroundColor Gray
        Write-Host "  ✓ Git 태그 생성 완료" -ForegroundColor Gray
        Write-Host ""
        
        $response = Read-Host "체크리스트를 확인하셨습니까? [Y/N/Open]"
        
        if ($response -eq "Open" -or $response -eq "O") {
            Write-Host "📄 RELEASE_CHECKLIST.md를 엽니다..." -ForegroundColor Cyan
            
            # VS Code로 열기 시도
            if (Get-Command code -ErrorAction SilentlyContinue) {
                code $checklistPath
            } else {
                # 기본 텍스트 에디터로 열기
                Start-Process $checklistPath
            }
            
            Write-Host ""
            $response = Read-Host "체크리스트 확인 후 계속하시겠습니까? [Y/N]"
        }
        
        if ($response -ne "Y" -and $response -ne "y") {
            Write-Host "❌ 배포가 취소되었습니다." -ForegroundColor Red
            Write-Host "💡 Tip: RELEASE_CHECKLIST.md를 확인하고 필요한 단계를 완료하세요." -ForegroundColor Yellow
            exit 1
        }
        
        Write-Success "체크리스트 확인 완료! 배포를 계속합니다..."
    } else {
        Write-Host "⚠️  경고: RELEASE_CHECKLIST.md를 찾을 수 없습니다." -ForegroundColor Yellow
        $response = Read-Host "계속하시겠습니까? [Y/N]"
        if ($response -ne "Y" -and $response -ne "y") {
            exit 1
        }
    }
}

function Write-Error-Custom {
    param([string]$Message)
    Write-Host "❌ $Message" -ForegroundColor Red
}

function Write-Warning-Custom {
    param([string]$Message)
    Write-Host "⚠️  $Message" -ForegroundColor Yellow
}

# ========================================
# 배포 전 체크리스트 확인
# ========================================
Show-ReleaseChecklist

# 버전 추출 (없으면 .csproj에서 읽기)
if ([string]::IsNullOrWhiteSpace($Version)) {
    Write-Step "버전 정보 추출 중..." "Yellow"
    
    $csprojPath = Join-Path $PSScriptRoot "TradingBot\TradingBot.csproj"
    if (Test-Path $csprojPath) {
        [xml]$csproj = Get-Content $csprojPath
        $Version = $csproj.Project.PropertyGroup.Version
        Write-Host "  .csproj에서 버전 추출: $Version" -ForegroundColor Gray
    }
    
    if ([string]::IsNullOrWhiteSpace($Version)) {
        Write-Error-Custom "버전 정보를 찾을 수 없습니다."
        $Version = Read-Host "버전을 입력하세요 (예: 1.0.1)"
    }
}

Write-Host "  릴리스 버전: v$Version" -ForegroundColor Cyan
Write-Host "  게시 경로: $PublishPath" -ForegroundColor Gray

# 1. Velopack 도구 확인
Write-Step "[1/4] Velopack 도구 확인" "Yellow"

$vpkExists = Get-Command vpk -ErrorAction SilentlyContinue
if (-not $vpkExists) {
    Write-Warning-Custom "Velopack 도구가 설치되지 않았습니다. 설치 중..."
    dotnet tool install -g vpk
    if ($LASTEXITCODE -ne 0) {
        Write-Error-Custom "Velopack 설치 실패"
        exit 1
    }
    Write-Success "Velopack 설치 완료"
}
else {
    Write-Success "Velopack 도구 확인 완료"
}

# 2. Velopack 패키징
Write-Step "[2/4] Velopack 패키징" "Yellow"

$ReleasesDir = Join-Path $PSScriptRoot "Releases"

# Releases 디렉터리 정리
if (Test-Path $ReleasesDir) {
    Write-Host "  기존 Releases 폴더 정리 중..." -ForegroundColor Gray
    Remove-Item "$ReleasesDir\*" -Recurse -Force -ErrorAction SilentlyContinue
}

Write-Host "  vpk pack 실행 중..." -ForegroundColor Gray
Write-Host "  명령어: vpk pack -u TradingBot -v $Version -p `"$PublishPath`" -e TradingBot.exe --skipVeloAppCheck" -ForegroundColor DarkGray

vpk pack -u TradingBot -v $Version -p "$PublishPath" -e TradingBot.exe --skipVeloAppCheck

if ($LASTEXITCODE -ne 0) {
    Write-Error-Custom "Velopack 패키징 실패 (Exit Code: $LASTEXITCODE)"
    exit 1
}

Write-Success "Velopack 패키징 완료"

# 생성된 파일 확인
$setupFile = Get-ChildItem -Path $ReleasesDir -Filter "*Setup.exe" -ErrorAction SilentlyContinue | Select-Object -First 1
$nupkgFile = Get-ChildItem -Path $ReleasesDir -Filter "*.nupkg" -ErrorAction SilentlyContinue | Select-Object -First 1
$releasesFile = Join-Path $ReleasesDir "RELEASES"

if ($setupFile) {
    $setupSizeMB = [math]::Round($setupFile.Length / 1MB, 2)
    Write-Host "  Setup.exe: $($setupFile.Name) - $setupSizeMB MB" -ForegroundColor Green
}
if ($nupkgFile) {
    $nupkgSizeMB = [math]::Round($nupkgFile.Length / 1MB, 2)
    Write-Host "  NuGet Package: $($nupkgFile.Name) - $nupkgSizeMB MB" -ForegroundColor Green
}
if (Test-Path $releasesFile) {
    Write-Host "  RELEASES file: Created" -ForegroundColor Green
}

# 3. 릴리스 노트 생성
Write-Step "[3/4] 릴리스 노트 생성" "Yellow"

$releaseNotesScript = Join-Path $PSScriptRoot "generate_release_notes.ps1"
if (Test-Path $releaseNotesScript) {
    Write-Host "  generate_release_notes.ps1 실행 중..." -ForegroundColor Gray
    & $releaseNotesScript -CurrentVersion $Version
    
    if ($LASTEXITCODE -eq 0) {
        Write-Success "릴리스 노트 생성 완료"
    }
    else {
        Write-Warning-Custom "릴리스 노트 생성 실패 (계속 진행)"
    }
}
else {
    Write-Warning-Custom "generate_release_notes.ps1을 찾을 수 없습니다."
}

# 4. GitHub 릴리스 생성
if ($SkipGitHubRelease) {
    Write-Step "[4/4] GitHub 릴리스 건너뜀" "Yellow"
    Write-Warning-Custom "-SkipGitHubRelease 플래그가 설정되어 GitHub 릴리스를 건너뜁니다."
}
else {
    Write-Step "[4/4] GitHub 릴리스 생성" "Yellow"
    
    # GitHub CLI 확인
    $ghExists = Get-Command gh -ErrorAction SilentlyContinue
    if (-not $ghExists) {
        Write-Warning-Custom "GitHub CLI(gh)가 설치되지 않았습니다."
        Write-Host "  설치 방법: https://cli.github.com/" -ForegroundColor Gray
        Write-Host "  또는: winget install GitHub.cli" -ForegroundColor Gray
        Write-Warning-Custom "GitHub 릴리스 생성을 건너뜁니다."
    }
    else {
        Write-Host "  GitHub CLI 확인 완료" -ForegroundColor Gray
        
        # Git 저장소 확인
        $gitRoot = git rev-parse --show-toplevel 2>$null
        if (-not $gitRoot) {
            Write-Warning-Custom "Git 저장소가 아닙니다. GitHub 릴리스를 건너뜁니다."
        }
        else {
            Write-Host "  Git 저장소: $gitRoot" -ForegroundColor Gray
            
            # 릴리스 노트 파일 확인
            $releaseNotesFile = Join-Path $PSScriptRoot "release_notes.md"
            $releaseBody = ""
            
            if (Test-Path $releaseNotesFile) {
                $releaseBody = Get-Content $releaseNotesFile -Raw
                Write-Host "  릴리스 노트 파일 사용: release_notes.md" -ForegroundColor Gray
            }
            else {
                $releaseBody = @"
TradingBot v$Version Release

Download:
- Windows Setup: TradingBot-win-Setup.exe (Recommended)
- Portable Version: TradingBot-win-Portable.zip

System Requirements:
- Windows 10/11 (64-bit)
- .NET 9.0 Runtime (Included)

Installation:
1. Download and run TradingBot-win-Setup.exe
2. Auto-update enabled for future releases

Note:
- Configure API Key in Settings before first use
"@
                Write-Host "  기본 릴리스 노트 사용" -ForegroundColor Gray
            }
            
            # GitHub 릴리스 생성
            Write-Host "  GitHub 릴리스 생성 중..." -ForegroundColor Gray
            
            $tag = "v$Version"
            $title = "TradingBot v$Version"
            
            # 릴리스 생성 (파일 업로드 포함)
            $releaseFiles = @()
            if ($setupFile) { $releaseFiles += $setupFile.FullName }
            if ($nupkgFile) { $releaseFiles += $nupkgFile.FullName }
            if (Test-Path $releasesFile) { $releaseFiles += $releasesFile }
            
            # Velopack 자동 업데이트에 필요한 JSON 파일들도 포함
            $releasesWinJson = Join-Path $ReleasesDir "releases.win.json"
            $assetsWinJson = Join-Path $ReleasesDir "assets.win.json"
            if (Test-Path $releasesWinJson) { $releaseFiles += $releasesWinJson }
            if (Test-Path $assetsWinJson) { $releaseFiles += $assetsWinJson }
            
            $filesArgs = $releaseFiles | ForEach-Object { "`"$_`"" }
            
            try {
                # 기존 릴리스 확인 (없으면 정상 진행)
                $releaseExists = $false
                $releaseListJson = gh release list --limit 200 --json tagName 2>$null
                if ($LASTEXITCODE -eq 0 -and -not [string]::IsNullOrWhiteSpace($releaseListJson)) {
                    try {
                        $releaseList = $releaseListJson | ConvertFrom-Json
                        $releaseTags = @()
                        foreach ($item in @($releaseList)) {
                            if ($item.tagName) {
                                $releaseTags += [string]$item.tagName
                            }
                        }

                        if ($releaseTags -contains $tag) {
                            $releaseExists = $true
                        }
                    }
                    catch {
                        Write-Warning-Custom "기존 릴리스 목록 파싱 실패: $_"
                    }
                }

                if ($releaseExists) {
                    Write-Warning-Custom "릴리스 $tag가 이미 존재합니다."
                    $overwrite = Read-Host "덮어쓰시겠습니까? (y/N)"
                    if ($overwrite -eq 'y' -or $overwrite -eq 'Y') {
                        Write-Host "  기존 릴리스 삭제 중..." -ForegroundColor Gray
                        gh release delete $tag --yes
                    }
                    else {
                        Write-Warning-Custom "GitHub 릴리스 생성을 취소했습니다."
                        exit 0
                    }
                }
                
                # 릴리스 생성
                Write-Host "  명령어: gh release create $tag --title `"$title`" --notes-file ..." -ForegroundColor DarkGray
                
                $tempNotesFile = [System.IO.Path]::GetTempFileName()
                Set-Content -Path $tempNotesFile -Value $releaseBody -Encoding UTF8
                
                $ghCommand = "gh release create `"$tag`" --title `"$title`" --notes-file `"$tempNotesFile`""
                foreach ($file in $releaseFiles) {
                    $ghCommand += " `"$file`""
                }
                
                Invoke-Expression $ghCommand
                
                Remove-Item $tempNotesFile -Force -ErrorAction SilentlyContinue
                
                if ($LASTEXITCODE -eq 0) {
                    Write-Success "GitHub 릴리스 생성 완료!"
                    Write-Host "  🔗 릴리스 URL: https://github.com/$(gh repo view --json nameWithOwner -q .nameWithOwner)/releases/tag/$tag" -ForegroundColor Cyan
                }
                else {
                    Write-Error-Custom "GitHub 릴리스 생성 실패 (Exit Code: $LASTEXITCODE)"
                }
            }
            catch {
                Write-Error-Custom "GitHub 릴리스 생성 중 오류: $_"
            }
        }
    }
}

# 완료
Write-Step "🎉 배포 완료!" "Green"
Write-Host ""
Write-Host "다음 단계:" -ForegroundColor Cyan
Write-Host "  1. Releases 폴더의 파일 확인" -ForegroundColor Gray
if (-not $SkipGitHubRelease -and $ghExists) {
    Write-Host "  2. GitHub Releases 페이지에서 릴리스 확인" -ForegroundColor Gray
    Write-Host "  3. 사용자에게 업데이트 알림" -ForegroundColor Gray
}
else {
    Write-Host "  2. GitHub에 수동으로 릴리스 업로드 (선택)" -ForegroundColor Gray
}
Write-Host ""

# 성공 시 탐색기에서 Releases 폴더 열기
if (Test-Path $ReleasesDir) {
    $openFolder = Read-Host "Releases 폴더를 여시겠습니까? (Y/n)"
    if ($openFolder -ne 'n' -and $openFolder -ne 'N') {
        Start-Process explorer.exe $ReleasesDir
    }
}

exit 0

