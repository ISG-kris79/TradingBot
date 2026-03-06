# 🚀 릴리스 배포 체크리스트

> **이 문서를 매번 참고하여 배포 과정에서 빠뜨리는 단계가 없도록 하세요!**

---

## 📋 배포 전 체크리스트

- [ ] 코드 변경사항 모두 커밋 완료
- [ ] 테스트 완료 (수동 테스트)
- [ ] `CHANGELOG.md`에 새 버전 내용 추가
- [ ] 버전 번호 업데이트 완료

---

## 🔢 1단계: 버전 업데이트 및 빌드

### 1-1. 버전 번호 업데이트
다음 파일들의 버전 번호를 업데이트:
```xml
<!-- TradingBot.csproj -->
<PropertyGroup>
  <Version>X.X.X</Version>
  <FileVersion>X.X.X</FileVersion>
  <AssemblyVersion>X.X.X</AssemblyVersion>
</PropertyGroup>
```

### 1-2. CHANGELOG.md 업데이트
```markdown
## [X.X.X] - YYYY-MM-DD
### Added
- 새로운 기능 목록

### Fixed
- 버그 수정 목록

### Changed
- 변경사항 목록
```

### 1-3. Release 빌드 실행
```powershell
dotnet build TradingBot.csproj -c Release
```

**✅ 체크포인트**: 빌드가 에러 없이 완료되었는가?

---

## 📝 2단계: Git 커밋 및 동기화

### 2-1. Git 커밋
```powershell
git add .
git commit -m "Release vX.X.X - 간단한 변경사항 요약"
```

### 2-2. GitHub에 푸시
```powershell
git push Tradingbot main
```

**✅ 체크포인트**: GitHub에 커밋이 올라갔는가?

---

## 📦 3단계: Velopack 배포 및 패키징

### 3-1. 애플리케이션 Publish
```powershell
dotnet publish TradingBot.csproj -c Release -r win-x64 --self-contained -p:PublishSingleFile=false -o "bin\publish\win-x64"
```

### 3-2. Velopack 패키징 실행
```powershell
.\publish-and-release.ps1 -PublishPath "bin\publish\win-x64" -Version "X.X.X"
```

**스크립트가 자동으로 수행하는 작업:**
1. ✅ Velopack 도구 확인
2. ✅ `vpk pack` 실행하여 Setup.exe 및 .nupkg 생성
3. ✅ `generate_release_notes.ps1`로 릴리스 노트 생성
4. ✅ GitHub 릴리스 생성 (이미 존재하면 스킵)

**생성되는 파일 (Releases 폴더):**
- `TradingBot-win-Setup.exe` (~159MB)
- `TradingBot-2.X.X-full.nupkg` (~156MB)
- `TradingBot-win-Portable.zip` (~156MB)
- `RELEASES` (메타데이터)
- `releases.win.json` (자동 업데이트용)
- `assets.win.json` (자동 업데이트용)

**✅ 체크포인트**: Releases 폴더에 7개 파일이 생성되었는가?

---

## 🏷️ 4단계: Git 태그 생성

### 4-1. Annotated Tag 생성
```powershell
git tag -a vX.X.X -m "Release vX.X.X - 주요 변경사항 요약"
```

### 4-2. 태그 푸시
```powershell
git push Tradingbot vX.X.X
```

### 4-3. (선택) GPG 서명된 태그 생성 (Verified 표시용)
```powershell
# GPG 설치 확인
gpg --version

# 서명된 태그 생성
git tag -s vX.X.X -m "Release vX.X.X - 주요 변경사항 요약"

# 태그 푸시 (force로 덮어쓰기)
git push Tradingbot vX.X.X --force
```

**✅ 체크포인트**: GitHub에서 태그가 보이는가?

---

## 🌐 5단계: GitHub 릴리스 업로드

### 5-1. 기존 릴리스에 파일 업로드
```powershell
# 필수 파일 업로드 (6개)
gh release upload vX.X.X `
  "Releases\TradingBot-win-Setup.exe" `
  "Releases\TradingBot-X.X.X-full.nupkg" `
  "Releases\TradingBot-win-Portable.zip" `
  "Releases\RELEASES" `
  "Releases\releases.win.json" `
  "Releases\assets.win.json" `
  --clobber
```

### 5-2. Latest 릴리스로 설정
```powershell
gh release edit vX.X.X --latest
```

**✅ 체크포인트**: GitHub 릴리스 페이지에서 다음을 확인:
- [ ] Latest 배지가 표시되는가?
- [ ] 7개 파일이 모두 업로드되었는가?
- [ ] Setup.exe 파일 크기가 ~159MB인가?

---

## 🎯 최종 확인 체크리스트

릴리스 완료 후 반드시 확인:

- [ ] GitHub 릴리스 페이지에서 **Latest** 배지 확인
- [ ] 릴리스 노트가 올바르게 표시되는가?
- [ ] 다운로드 가능한 에셋이 7개인가?
  - [ ] TradingBot-win-Setup.exe
  - [ ] TradingBot-X.X.X-full.nupkg
  - [ ] TradingBot-vX.X.X-win-x64.zip
  - [ ] TradingBot-win-Portable.zip
  - [ ] RELEASES
  - [ ] releases.win.json
  - [ ] assets.win.json
- [ ] Setup.exe 다운로드 및 설치 테스트
- [ ] 자동 업데이트 작동 확인 (기존 버전에서)

---

## 🔐 GPG 서명 설정 (선택 사항 - Verified 표시용)

### Windows에서 GPG 설치 및 설정

#### 1. GPG 설치
```powershell
# Chocolatey로 설치
choco install gpg4win

# 또는 직접 다운로드
# https://www.gpg4win.org/
```

#### 2. GPG 키 생성
```powershell
# 대화형 키 생성
gpg --full-generate-key

# 선택사항:
# - RSA and RSA (default)
# - 4096 bits
# - 유효기간: 0 (무한)
# - 이름, 이메일 입력 (GitHub 이메일과 동일하게)
```

#### 3. GPG 키 ID 확인
```powershell
gpg --list-secret-keys --keyid-format=long

# 출력 예시:
# sec   rsa4096/YOUR_KEY_ID 2024-01-01
```

#### 4. Git에 GPG 키 등록
```powershell
# GPG 키 설정
git config --global user.signingkey YOUR_KEY_ID

# 태그에 자동으로 서명
git config --global tag.gpgSign true

# 커밋에 자동으로 서명 (선택)
git config --global commit.gpgSign true
```

#### 5. GitHub에 GPG 공개키 등록
```powershell
# 공개키 복사
gpg --armor --export YOUR_KEY_ID

# GitHub Settings > SSH and GPG keys > New GPG key에 붙여넣기
```

#### 6. 서명된 태그 생성
```powershell
# 기존 태그 삭제
git tag -d vX.X.X
git push Tradingbot :refs/tags/vX.X.X

# 서명된 태그 생성
git tag -s vX.X.X -m "Release vX.X.X"

# 푸시
git push Tradingbot vX.X.X
```

---

## 🛠️ 트러블슈팅

### 문제: "릴리스가 이미 존재합니다"
```powershell
# 수동으로 파일만 업로드
gh release upload vX.X.X "Releases\*.exe" "Releases\*.nupkg" "Releases\*.zip" "Releases\RELEASES" "Releases\*.json" --clobber
```

### 문제: "태그가 없습니다"
```powershell
# 태그 생성 후 푸시
git tag -a vX.X.X -m "Release vX.X.X"
git push Tradingbot vX.X.X
```

### 문제: "vpk 명령을 찾을 수 없습니다"
```powershell
# Velopack 전역 설치
dotnet tool install -g vpk

# 또는 로컬 설치
dotnet tool install vpk --tool-path .
```

### 문제: "Setup.exe가 생성되지 않습니다"
```powershell
# 기존 Releases 폴더 삭제 후 재시도
Remove-Item -Recurse -Force Releases
.\publish-and-release.ps1 -PublishPath "bin\publish\win-x64" -Version "X.X.X"
```

---

## 📚 참고 문서

- `publish-and-release.ps1` - 자동화된 배포 스크립트
- `generate_release_notes.ps1` - 릴리스 노트 자동 생성
- `CHANGELOG.md` - 버전별 변경사항
- `DEPLOYMENT_GUIDE.md` - 상세 배포 가이드

---

## 🔄 빠른 배포 명령어 모음

전체 배포를 한 번에 실행하려면:

```powershell
# 1. 버전 변수 설정
$version = "2.2.2"

# 2. 버전 업데이트는 수동으로!
# TradingBot.csproj, CHANGELOG.md 수정

# 3. Git 커밋 및 푸시
git add .
git commit -m "Release v$version - 변경사항 요약"
git push Tradingbot main

# 4. 태그 생성 및 푸시
git tag -a v$version -m "Release v$version"
git push Tradingbot v$version

# 5. Publish 및 Velopack 패키징
dotnet publish TradingBot.csproj -c Release -r win-x64 --self-contained -p:PublishSingleFile=false -o "bin\publish\win-x64"
.\publish-and-release.ps1 -PublishPath "bin\publish\win-x64" -Version $version

# 6. GitHub 릴리스 파일 업로드
gh release upload v$version `
  "Releases\TradingBot-win-Setup.exe" `
  "Releases\TradingBot-$version-full.nupkg" `
  "Releases\TradingBot-win-Portable.zip" `
  "Releases\RELEASES" `
  "Releases\releases.win.json" `
  "Releases\assets.win.json" `
  --clobber

# 7. Latest로 설정
gh release edit v$version --latest
```

---

## ⚡ 원라인 배포 (버전 업데이트 후)

```powershell
$v="2.2.2"; dotnet publish TradingBot.csproj -c Release -r win-x64 --self-contained -p:PublishSingleFile=false -o "bin\publish\win-x64"; .\publish-and-release.ps1 -PublishPath "bin\publish\win-x64" -Version $v; gh release upload v$v "Releases\TradingBot-win-Setup.exe" "Releases\TradingBot-$v-full.nupkg" "Releases\TradingBot-win-Portable.zip" "Releases\RELEASES" "Releases\releases.win.json" "Releases\assets.win.json" --clobber; gh release edit v$v --latest
```

---

**💡 Tip**: 이 문서를 북마크하고, 배포할 때마다 체크리스트를 따라가세요!
