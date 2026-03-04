# 🚀 Visual Studio 게시 빠른 시작 가이드

Visual Studio에서 게시 버튼을 클릭하면 자동으로 **Velopack 배포** 및 **GitHub 릴리스**가 생성됩니다.

## ⚡ 빠른 시작 (5분)

### 1️⃣ 사전 준비 (최초 1회만)

```powershell
# Velopack CLI 설치
dotnet tool install -g vpk

# GitHub CLI 설치 (릴리스 자동 업로드용)
winget install GitHub.cli

# GitHub 로그인
gh auth login
```

### 2️⃣ 버전 업데이트

**TradingBot.csproj** 파일 열기:

```xml
<PropertyGroup>
  <Version>1.0.1</Version>  <!-- 이 버전을 수정하세요 -->
</PropertyGroup>
```

### 3️⃣ Visual Studio에서 게시

1. Solution Explorer에서 **TradingBot** 프로젝트 우클릭
2. **게시(Publish)** 선택
3. **VelopackRelease** 프로필 선택
4. **게시** 버튼 클릭

### 4️⃣ 자동 실행 과정

```
✅ [1/4] Velopack 도구 확인
✅ [2/4] Velopack 패키징 (Setup.exe 생성)
✅ [3/4] 릴리스 노트 자동 생성 (Git 커밋 분석)
✅ [4/4] GitHub 릴리스 생성 및 업로드
🎉 완료!
```

### 5️⃣ 결과 확인

**로컬 파일** (`Releases/` 폴더):
- ✅ `TradingBot-win-Setup.exe` (사용자 설치 파일)
- ✅ `TradingBot-1.0.1-full.nupkg` (Velopack 패키지)
- ✅ `RELEASES` (업데이트 메타데이터)

**GitHub Releases**:
- 자동 업로드 완료
- URL: `https://github.com/<사용자명>/TradingBot/releases/tag/v1.0.1`

---

## 🎯 워크플로우 요약

```mermaid
graph LR
    A[코드 변경] --> B[버전 업데이트<br/>TradingBot.csproj]
    B --> C[Visual Studio<br/>게시 버튼 클릭]
    C --> D[Velopack<br/>Setup.exe 생성]
    D --> E[GitHub 릴리스<br/>자동 업로드]
    E --> F[완료 🎉]
```

---

## ❓ 자주 묻는 질문 (FAQ)

### Q1: "vpk: 명령을 찾을 수 없습니다" 오류가 나요

```powershell
dotnet tool install -g vpk
```

설치 후 PowerShell을 재시작하세요.

### Q2: GitHub 릴리스를 건너뛰고 싶어요

**publish-and-release.ps1**을 직접 실행:

```powershell
.\publish-and-release.ps1 -PublishPath ".\TradingBot\bin\publish" -Version "1.0.1" -SkipGitHubRelease
```

### Q3: 릴리스 노트를 수정하고 싶어요

1. 스크립트 실행 전:
   ```powershell
   .\generate_release_notes.ps1 -CurrentVersion "1.0.1"
   ```

2. 생성된 `release_notes.md` 파일 편집

3. 다시 게시

### Q4: 커밋 메시지가 릴리스 노트에 제대로 분류되지 않아요

**Conventional Commits** 규칙 사용:

```bash
git commit -m "feat: 새로운 기능 추가"       # ✨ Features
git commit -m "fix: 버그 수정"             # 🐛 Bug Fixes
git commit -m "perf: 성능 개선"            # ⚡ Performance
git commit -m "docs: 문서 업데이트"        # 📚 Documentation
```

### Q5: 게시 프로필이 보이지 않아요

**Properties/PublishProfiles/VelopackRelease.pubxml** 파일이 있는지 확인하세요.

없다면:
```bash
git pull origin main
```

---

## 🔗 추가 문서

- **상세 가이드**: [DEPLOYMENT.md](DEPLOYMENT.md)
- **프로젝트 README**: [README.md](README.md)
- **TODO 목록**: [TradingBot/TODO.md](TradingBot/TODO.md)

---

## 💡 팁

### 릴리스 전 체크리스트

- [ ] 버전 번호 확인 (`TradingBot.csproj`)
- [ ] 변경 사항 테스트 완료
- [ ] 커밋 메시지가 Conventional Commits 규칙 준수
- [ ] Velopack CLI 설치 확인 (`vpk --version`)
- [ ] GitHub CLI 로그인 확인 (`gh auth status`)

### 효율적인 배포 프로세스

**개발 → 커밋 → 게시 → 릴리스** 한 번에:

```bash
# 1. 코드 변경 후 커밋
git add .
git commit -m "feat: 새로운 트레이딩 전략 추가"

# 2. 버전 업데이트 (TradingBot.csproj)
# (수동으로 Version 수정)

# 3. Visual Studio에서 게시 버튼 클릭
# → 자동으로 모든 작업 완료!

# 4. Git 푸시 (선택사항, 소스 코드 업로드)
git push origin main
```

---

**마지막 업데이트**: 2025-01-XX  
**작성자**: TradingBot 개발팀
