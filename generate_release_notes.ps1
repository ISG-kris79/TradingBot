# 릴리스 노트 자동 생성 스크립트
# 커밋 메시지 규칙: feat:, fix:, docs:, style:, refactor:, perf:, test:, chore:

param(
    [string]$PreviousTag = "",
    [string]$CurrentVersion = "1.1.2"
)

$ErrorActionPreference = "Continue"

# 이전 태그 자동 찾기
if ([string]::IsNullOrEmpty($PreviousTag)) {
    $PreviousTag = git describe --tags --abbrev=0 HEAD^ 2>$null
    if (-not $PreviousTag -or $LASTEXITCODE -ne 0) {
        $PreviousTag = git rev-list --max-parents=0 HEAD 2>$null
        if (-not $PreviousTag) {
            Write-Warning "Git 히스토리를 찾을 수 없습니다. 릴리스 노트 생성을 건너뜁니다."
            exit 0
        }
        Write-Host "첫 릴리스입니다. 모든 커밋을 포함합니다."
    }
}

Write-Host "버전 범위: $PreviousTag..HEAD" -ForegroundColor Cyan
Write-Host "현재 버전: v$CurrentVersion" -ForegroundColor Cyan

# 커밋 로그 가져오기
$commits = git log "$PreviousTag..HEAD" --pretty=format:"%s|%h|%an" --no-merges

# 제외할 키워드 목록 (정규식 패턴)
$excludePatterns = @(
    "^Merge",           # Merge 커밋
    "^Bump version",    # 버전 올림
    "\[skip ci\]",      # CI 스킵
    "^Release"          # 릴리스 커밋
)

# 카테고리별 분류
$features = @()
$fixes = @()
$docs = @()
$performance = @()
$refactors = @()
$others = @()

foreach ($commit in $commits) {
    $parts = $commit -split '\|'
    $message = $parts[0]
    
    # 제외 패턴 확인
    $skip = $false
    foreach ($pattern in $excludePatterns) {
        if ($message -match $pattern) {
            $skip = $true
            break
        }
    }
    if ($skip) { continue }

    $hash = $parts[1]
    $author = $parts[2]
    
    $line = "- $message ([$hash](https://github.com/ISG-kris79/TradingBot/commit/$hash))"
    
    switch -Regex ($message) {
        '^feat(\(.+\))?:' { $features += $line; break }
        '^fix(\(.+\))?:' { $fixes += $line; break }
        '^docs(\(.+\))?:' { $docs += $line; break }
        '^perf(\(.+\))?:' { $performance += $line; break }
        '^refactor(\(.+\))?:' { $refactors += $line; break }
        '^style(\(.+\))?:' { $refactors += $line; break }
        '^chore(\(.+\))?:' { $others += $line; break }
        default { $others += $line }
    }
}

# 릴리스 노트 생성
$releaseNotes = @"
## 🚀 TradingBot v$CurrentVersion

**릴리스 날짜**: $(Get-Date -Format 'yyyy-MM-dd')

### 📦 다운로드

| 파일명 | 용도 | 크기 |
|--------|------|------|
| ``TradingBot-win-Setup.exe`` | Windows 설치 파일 (권장) | ~86MB |
| ``TradingBot-win-Portable.zip`` | 포터블 버전 | ~80MB |
| ``TradingBot-$CurrentVersion-full.nupkg`` | Velopack 업데이트 패키지 | ~80MB |

"@

if ($features.Count -gt 0) {
    $releaseNotes += @"

### ✨ 새로운 기능 (Features)
$($features -join "`n")
"@
}

if ($fixes.Count -gt 0) {
    $releaseNotes += @"

### 🐛 버그 수정 (Bug Fixes)
$($fixes -join "`n")
"@
}

if ($performance.Count -gt 0) {
    $releaseNotes += @"

### ⚡ 성능 개선 (Performance)
$($performance -join "`n")
"@
}

if ($refactors.Count -gt 0) {
    $releaseNotes += @"

### 🔨 코드 개선 (Code Improvements)
$($refactors -join "`n")
"@
}

if ($docs.Count -gt 0) {
    $releaseNotes += @"

### 📚 문서 (Documentation)
$($docs -join "`n")
"@
}

if ($others.Count -gt 0) {
    $releaseNotes += @"

### 🔧 기타 변경사항 (Others)
$($others -join "`n")
"@
}

$releaseNotes += @"

---

### 📋 시스템 요구사항
- **운영체제**: Windows 10/11 (64-bit)
- **프레임워크**: .NET 9.0 Runtime (설치 파일에 포함)
- **권장 메모리**: 4GB 이상
- **디스크 공간**: 500MB 이상

### 🔧 설치 방법
1. ``TradingBot-win-Setup.exe`` 다운로드
2. 실행 파일을 열고 설치 마법사 진행
3. 설치 완료 후 바탕화면 또는 시작 메뉴에서 실행
4. 첫 실행 시 Settings에서 API Key 설정

### 🔄 업데이트 방법
- Velopack 자동 업데이트가 활성화되어 있어 새 버전 출시 시 자동 알림
- 또는 [Releases 페이지](https://github.com/ISG-kris79/TradingBot/releases)에서 최신 Setup.exe 다운로드

### ⚠️ 주의사항
- 처음 사용 시 **시뮬레이션 모드**로 충분히 테스트 권장
- API Key는 반드시 **IP 제한** 설정하여 사용
- 거래소 API Key 권한: **선물 거래 활성화** 필요
- 텔레그램 알림을 받으려면 Bot Token과 Chat ID 설정 필요

### 📞 지원
- **이슈 제보**: [GitHub Issues](https://github.com/ISG-kris79/TradingBot/issues)
- **문서**: [README.md](https://github.com/ISG-kris79/TradingBot/blob/main/README.md)
- **변경 로그**: [CHANGELOG.md](https://github.com/ISG-kris79/TradingBot/blob/main/CHANGELOG.md)

---

**Full Changelog**: [$PreviousTag...v$CurrentVersion](https://github.com/ISG-kris79/TradingBot/compare/$PreviousTag...v$CurrentVersion)
"@

# 파일로 저장
$releaseNotes | Out-File -FilePath "release_notes.md" -Encoding utf8 -NoNewline

Write-Host "`n✅ 릴리스 노트 생성 완료: release_notes.md" -ForegroundColor Green
Write-Host "`n=== 생성된 릴리스 노트 미리보기 ===" -ForegroundColor Yellow
Write-Host $releaseNotes

Write-Host "`n💡 팁: 커밋 메시지 규칙을 따르면 자동 분류가 더 정확합니다!" -ForegroundColor Cyan
Write-Host "   - feat: 새로운 기능" -ForegroundColor Gray
Write-Host "   - fix: 버그 수정" -ForegroundColor Gray
Write-Host "   - docs: 문서 변경" -ForegroundColor Gray
Write-Host "   - perf: 성능 개선" -ForegroundColor Gray
Write-Host "   - refactor: 코드 리팩토링" -ForegroundColor Gray
Write-Host "   - style: 코드 스타일 변경" -ForegroundColor Gray
Write-Host "   - chore: 빌드/설정 변경" -ForegroundColor Gray
