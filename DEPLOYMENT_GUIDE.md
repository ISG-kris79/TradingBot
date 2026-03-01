# TradingBot 배포 가이드

## 📋 배포 전 체크리스트

### 1. 버전 업데이트
**TradingBot/TradingBot.csproj** 파일에서 버전 정보를 수정합니다:

```xml
<Version>2.0.X</Version>
<AssemblyVersion>2.0.X.0</AssemblyVersion>
<FileVersion>2.0.X.0</FileVersion>
```

⚠️ **중요**: 이 버전 정보가 로그인 창과 앱 전체에 표시됩니다.

### 2. 변경사항 커밋
```powershell
git add -A
git commit -m "chore: Release vX.X.X - 변경사항 요약"
```

💡 **팁**: 커밋 날짜가 GitHub 릴리스의 "Published" 날짜로 표시됩니다.

---

## 🚀 배포 실행

### 자동 배포 (권장)

#### 1단계: 클린 빌드
```powershell
dotnet clean TradingBot/TradingBot.csproj
dotnet publish TradingBot/TradingBot.csproj -c Release -o bin/publish --self-contained false
```

#### 2단계: 배포 스크립트 실행
```powershell
.\publish-and-release.ps1 -PublishPath "bin\publish" -Version "X.X.X"
```

> **자동으로 수행되는 작업**:
> 1. Velopack 패키징 (Setup.exe, Portable.zip, .nupkg)
> 2. 릴리스 노트 생성 (release_notes.md)
> 3. GitHub 릴리스 생성 시도 (실패 시 수동 진행)

#### 3단계: GitHub 릴리스 수동 생성 (자동 실패 시)
```powershell
gh release create "vX.X.X" `
  --title "TradingBot vX.X.X" `
  --notes-file "release_notes.md" `
  --latest `
  "Releases\TradingBot-win-Setup.exe" `
  "Releases\TradingBot-win-Portable.zip" `
  "Releases\TradingBot-X.X.X-full.nupkg" `
  "Releases\RELEASES" `
  "Releases\releases.win.json" `
  "Releases\assets.win.json"
```

⚠️ **필수 파일**: `releases.win.json`과 `assets.win.json`이 없으면 **자동 업데이트가 작동하지 않습니다**.

---

## 📦 생성되는 파일 목록

배포 후 `Releases/` 폴더에 다음 파일들이 생성됩니다:

| 파일 | 용도 | 크기 | GitHub 업로드 필수 |
|------|------|------|-------------------|
| `TradingBot-win-Setup.exe` | Windows 설치 프로그램 | ~91MB | ✅ 필수 |
| `TradingBot-win-Portable.zip` | 포터블 버전 | ~89MB | ✅ 필수 |
| `TradingBot-X.X.X-full.nupkg` | Velopack 업데이트 패키지 | ~89MB | ✅ 필수 |
| `RELEASES` | Velopack 매니페스트 | ~80B | ✅ 필수 |
| **`releases.win.json`** | **자동 업데이트 메타데이터** | ~254B | ✅ **필수** |
| **`assets.win.json`** | **업데이트 에셋 정보** | ~202B | ✅ **필수** |

---

## ✅ 배포 확인

### 1. GitHub 릴리스 확인
```powershell
gh release view vX.X.X
```

확인 사항:
- [ ] Latest 태그가 있는가?
- [ ] 6개의 파일이 모두 업로드되었는가?
- [ ] 릴리스 날짜가 올바른가?

### 2. 웹 브라우저 확인
https://github.com/ISG-kris79/TradingBot/releases

- [ ] 릴리스 목록에 표시되는가?
- [ ] 한글 릴리스 노트가 정상 표시되는가?
- [ ] "Latest" 배지가 있는가?

### 3. 자동 업데이트 테스트
1. 이전 버전 앱 실행
2. 로그인 진행
3. "새 버전 X.X.X를 발견했습니다" 알림 확인
4. "예" 선택 → 자동 다운로드 및 설치
5. 재시작 후 버전 확인

---

## 🔧 문제 해결

### 문제 1: 자동 업데이트가 작동하지 않음

**원인**: `releases.win.json` 파일이 GitHub에 업로드되지 않음

**해결**:
```powershell
gh release upload vX.X.X "Releases\releases.win.json" "Releases\assets.win.json" --clobber
```

### 문제 2: 로그인 창에 이전 버전이 표시됨

**원인**: `.csproj` 파일의 버전이 업데이트되지 않음

**해결**:
1. `TradingBot/TradingBot.csproj`에서 버전 수정
2. 리빌드 및 재배포:
```powershell
dotnet clean TradingBot/TradingBot.csproj
dotnet publish TradingBot/TradingBot.csproj -c Release -o bin/publish
gh release delete vX.X.X --yes  # 기존 릴리스 삭제
# 다시 배포 스크립트 실행
```

### 문제 3: GitHub 릴리스 목록에 안 보임

**원인**: 브라우저 캐시 또는 Git 태그 동기화 문제

**해결**:
1. 브라우저 강력 새로고침: `Ctrl + Shift + R`
2. Git 태그 동기화:
```powershell
git fetch --tags
```
3. 직접 URL 접근: `https://github.com/ISG-kris79/TradingBot/releases/tag/vX.X.X`

### 문제 4: 릴리스 날짜가 과거로 표시됨

**원인**: Git 태그가 이전 커밋을 가리키고 있음

**해결**:
```powershell
# 최신 커밋 생성
git add -A
git commit -m "chore: Release vX.X.X"

# 태그 재생성
git tag -d vX.X.X
git tag vX.X.X
git push origin vX.X.X --force

# 릴리스 재생성
gh release delete vX.X.X --yes
# publish-and-release.ps1 다시 실행 또는 gh release create 수동 실행
```

### 문제 5: PowerShell 스크립트 파싱 오류 (한글 깨짐)

**원인**: UTF-8 BOM 인코딩이 아님

**해결**:
```powershell
$content = Get-Content "publish-and-release.ps1" -Raw -Encoding UTF8
[System.IO.File]::WriteAllText("$PWD\publish-and-release.ps1", $content, (New-Object System.Text.UTF8Encoding $true))
```

---

## 📝 배포 후 작업

### 1. Git 푸시
```powershell
git push origin master
git push origin vX.X.X  # 태그 푸시
```

### 2. 릴리스 노트 업데이트 (필요시)
GitHub 웹에서 릴리스 노트를 수동으로 편집할 수 있습니다.

### 3. 사용자 공지
- Discord/텔레그램 등에 업데이트 공지
- 주요 변경사항 안내

---

## 🎯 빠른 배포 명령어 요약

```powershell
# 1. 버전 업데이트 (.csproj 파일 수정)

# 2. 커밋
git add -A
git commit -m "chore: Release vX.X.X - 변경사항"

# 3. 빌드
dotnet clean TradingBot/TradingBot.csproj
dotnet publish TradingBot/TradingBot.csproj -c Release -o bin/publish --self-contained false

# 4. 배포
.\publish-and-release.ps1 -PublishPath "bin\publish" -Version "X.X.X"

# 5. GitHub 릴리스 수동 생성 (자동 실패 시)
gh release create "vX.X.X" --title "TradingBot vX.X.X" --notes-file "release_notes.md" --latest `
  "Releases\TradingBot-win-Setup.exe" `
  "Releases\TradingBot-win-Portable.zip" `
  "Releases\TradingBot-X.X.X-full.nupkg" `
  "Releases\RELEASES" `
  "Releases\releases.win.json" `
  "Releases\assets.win.json"

# 6. 푸시
git push origin master
git push origin vX.X.X
```

---

## 📚 참고 문서

- [Velopack 공식 문서](https://velopack.io/)
- [GitHub CLI 문서](https://cli.github.com/manual/)
- [CHANGELOG.md](CHANGELOG.md) - 버전별 변경 이력
- [QUICKSTART_PUBLISH.md](QUICKSTART_PUBLISH.md) - 기존 배포 가이드

---

## 💡 자주 묻는 질문 (FAQ)

**Q: 버전 번호 규칙은?**
A: Semantic Versioning 준수 - `Major.Minor.Patch` (예: 2.0.9)

**Q: releases.win.json이 왜 중요한가요?**
A: Velopack은 이 파일로 업데이트 정보를 확인합니다. 없으면 자동 업데이트가 완전히 작동하지 않습니다.

**Q: Setup.exe vs Portable.zip 차이는?**
A: Setup.exe는 설치 프로그램 (자동 업데이트 지원), Portable.zip은 설치 없이 실행 가능 (수동 업데이트 필요)

**Q: 배포 실패 시 롤백 방법은?**
A: GitHub에서 이전 릴리스를 Latest로 설정: `gh release edit vX.X.X --latest`

---

**마지막 업데이트**: 2026-03-01
**작성자**: AI Assistant
