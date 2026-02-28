# 🚀 자동 배포 가이드

이 문서는 Visual Studio에서 Velopack 배포와 GitHub 릴리스를 자동화하는 방법을 설명합니다.

## 📋 목차
1. [사전 준비](#사전-준비)
2. [Visual Studio에서 게시하기](#visual-studio에서-게시하기)
3. [수동 배포 (PowerShell)](#수동-배포-powershell)
4. [문제 해결](#문제-해결)
5. [고급 설정](#고급-설정)

---

## 사전 준비

### 1. 필수 도구 설치

#### ✅ Velopack CLI
```powershell
dotnet tool install -g vpk
```

확인:
```powershell
vpk --version
```

#### ✅ GitHub CLI (선택사항, 자동 릴리스용)
```powershell
winget install GitHub.cli
```

확인 및 로그인:
```powershell
gh --version
gh auth login
```

**GitHub 토큰 권한**: `repo` (전체 저장소 액세스) 필요

---

### 2. 버전 설정

**TradingBot.csproj** 파일에서 버전 설정:

```xml
<PropertyGroup>
  <Version>1.0.1</Version>
  <AssemblyVersion>1.0.1</AssemblyVersion>
  <FileVersion>1.0.1</FileVersion>
</PropertyGroup>
```

⚠️ **중요**: 세 가지 버전을 모두 동일하게 설정해야 합니다.

---

## Visual Studio에서 게시하기

### 방법 1: GUI를 통한 게시 (권장)

1. **Solution Explorer**에서 **TradingBot** 프로젝트 우클릭
2. **게시(Publish)** 선택
3. **VelopackRelease** 프로필 선택
4. **게시** 버튼 클릭

### 실행 과정
```
Visual Studio 게시 시작
  ↓
[1/4] Velopack 도구 확인
  ↓
[2/4] Velopack 패키징 (Setup.exe 생성)
  ↓
[3/4] 릴리스 노트 자동 생성
  ↓
[4/4] GitHub 릴리스 생성 및 업로드
  ↓
완료! 🎉
```

### 결과물
배포 완료 후 **Releases/** 폴더에 생성됩니다:
- ✅ `TradingBot-win-Setup.exe` (사용자용 설치 파일)
- ✅ `TradingBot-1.0.1-full.nupkg` (Velopack 패키지)
- ✅ `RELEASES` (업데이트 메타데이터)

GitHub Releases 페이지에 자동 업로드됩니다.

---

### 방법 2: 명령줄을 통한 게시

```powershell
# 프로젝트 루트에서 실행
dotnet publish TradingBot/TradingBot.csproj -p:PublishProfile=VelopackRelease
```

---

## 수동 배포 (PowerShell)

Visual Studio 없이 PowerShell로 직접 배포할 수도 있습니다.

### 기본 사용
```powershell
cd E:\PROJECT\CoinFF\TradingBot

# 빌드 + Velopack + GitHub 릴리스 (모두 자동)
.\publish-and-release.ps1 -PublishPath ".\TradingBot\bin\publish" -Version "1.0.1"
```

### 옵션

#### Velopack만 생성 (GitHub 릴리스 건너뛰기)
```powershell
.\publish-and-release.ps1 -PublishPath ".\TradingBot\bin\publish" -Version "1.0.1" -SkipGitHubRelease
```

#### 버전 자동 감지 (.csproj에서 읽기)
```powershell
.\publish-and-release.ps1 -PublishPath ".\TradingBot\bin\publish"
```

---

## 문제 해결

### 1️⃣ "vpk: 명령을 찾을 수 없습니다"

**원인**: Velopack CLI가 설치되지 않음

**해결책**:
```powershell
dotnet tool install -g vpk
```

설치 후 PowerShell을 재시작하세요.

---

### 2️⃣ "GitHub CLI(gh)가 설치되지 않았습니다"

**원인**: GitHub CLI가 설치되지 않음

**옵션 A**: GitHub 릴리스를 건너뛰고 Velopack만 생성
```powershell
.\publish-and-release.ps1 -PublishPath ... -SkipGitHubRelease
```

**옵션 B**: GitHub CLI 설치 및 로그인
```powershell
winget install GitHub.cli
gh auth login
```

---

### 3️⃣ "인증되지 않았습니다"

**원인**: GitHub CLI 로그인이 필요

**해결책**:
```powershell
gh auth login
```

브라우저에서 인증을 완료하세요.

---

### 4️⃣ "릴리스 v1.0.1이 이미 존재합니다"

**원인**: 동일한 버전 태그가 이미 GitHub에 존재

**옵션 A**: 스크립트가 자동으로 묻습니다
```
덮어쓰시겠습니까? (y/N): y
```

**옵션 B**: GitHub에서 수동으로 삭제
```powershell
gh release delete v1.0.1 --yes
```

**옵션 C**: 버전 번호 변경 (.csproj에서)

---

### 5️⃣ ".csproj에서 버전을 찾을 수 없습니다"

**원인**: TradingBot.csproj에 `<Version>` 태그가 없음

**해결책**: .csproj 파일에 추가
```xml
<PropertyGroup>
  <Version>1.0.1</Version>
</PropertyGroup>
```

---

### 6️⃣ "publish-and-release.ps1 실행 정책 오류"

**원인**: PowerShell 실행 정책 제한

**해결책**:
```powershell
Set-ExecutionPolicy -Scope CurrentUser -ExecutionPolicy RemoteSigned
```

또는 일회성 우회:
```powershell
powershell -ExecutionPolicy Bypass -File .\publish-and-release.ps1 ...
```

---

## 고급 설정

### 릴리스 노트 커스터마이징

자동 생성된 릴리스 노트를 수정하려면:

1. **release_notes.md** 파일 편집 (스크립트가 자동 생성)
2. 다음 실행 시 해당 내용이 사용됩니다

#### 수동으로 릴리스 노트 생성
```powershell
.\generate_release_notes.ps1 -CurrentVersion "1.0.1"
```

생성된 `release_notes.md`를 편집한 후 배포를 진행하세요.

---

### GitHub 릴리스 제목 변경

**publish-and-release.ps1** 파일에서 수정:
```powershell
$title = "TradingBot v$Version - 메이저 업데이트"
```

---

### 프리릴리스(Pre-release) 생성

**publish-and-release.ps1** 파일의 GitHub 릴리스 생성 부분에 `--prerelease` 플래그 추가:
```powershell
gh release create "$tag" --title "$title" --notes-file "$tempNotesFile" --prerelease
```

---

### 출력 디렉터리 변경

**VelopackRelease.pubxml** 파일에서 수정:
```xml
<PublishDir>원하는\경로\</PublishDir>
```

---

### Self-Contained vs Framework-Dependent

현재 설정: **Self-Contained** (모든 런타임 포함, 크기 큼)

**Framework-Dependent**로 변경하려면:
**VelopackRelease.pubxml**:
```xml
<SelfContained>false</SelfContained>
```

⚠️ 사용자가 .NET 9.0 Runtime을 설치해야 합니다.

---

## 워크플로우 요약

### 릴리스 프로세스
```
1. 코드 변경 및 커밋
   git add .
   git commit -m "feat: 새로운 기능 추가"
   git push

2. 버전 업데이트
   TradingBot.csproj에서 <Version> 수정

3. Visual Studio에서 게시
   프로젝트 우클릭 → 게시 → VelopackRelease → 게시

4. 자동 실행
   ✅ Velopack 패키징
   ✅ 릴리스 노트 생성
   ✅ GitHub 릴리스 생성

5. 확인
   - Releases/ 폴더 확인
   - GitHub Releases 페이지 확인
```

---

## 추가 리소스

- [Velopack 공식 문서](https://github.com/velopack/velopack)
- [GitHub CLI 문서](https://cli.github.com/manual/)
- [MSBuild PublishProfile 문서](https://learn.microsoft.com/en-us/aspnet/core/host-and-deploy/visual-studio-publish-profiles)

---

## 도움이 필요하신가요?

문제가 발생하면:
1. 이 문서의 [문제 해결](#문제-해결) 섹션 확인
2. GitHub Issues에 질문 등록
3. 로그 파일 첨부: `Releases/` 폴더의 로그

---

**마지막 업데이트**: 2025-01-XX
**작성자**: TradingBot 개발팀
