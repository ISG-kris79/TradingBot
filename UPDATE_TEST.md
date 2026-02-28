# 버전 업데이트 기능 테스트 가이드

## ✅ 현재 상태

### 빌드 정보
- **현재 버전**: 1.0.0.0
- **빌드 날짜**: 2026-02-26
- **파일 크기**: 160,768 bytes
- **경로**: `TradingBot\bin\Release\net9.0-windows\TradingBot.exe`

### 구현된 기능

#### 1. Velopack 통합 ✅
- `VelopackApp.Build().Run()`: App.xaml.cs에 구현됨
- 자동 업데이트 체크 로직: `CheckForUpdatesAsync()` 메서드로 구현
- 백그라운드 업데이트 확인: 앱 시작 시 자동 실행

#### 2. 버전 정보 ✅
TradingBot.csproj에 추가됨:
```xml
<Version>1.0.0</Version>
<AssemblyVersion>1.0.0.0</AssemblyVersion>
<FileVersion>1.0.0.0</FileVersion>
<Company>TradingBot</Company>
<Product>TradingBot</Product>
<Description>Cryptocurrency Trading Bot with AI/ML capabilities</Description>
```

#### 3. 업데이트 확인 로직 ✅
```csharp
// App.xaml.cs - CheckForUpdatesAsync()
- 현재 버전 로깅
- GitHub Releases에서 새 버전 확인
- 자동 다운로드
- 사용자 알림 (재시작 여부 선택)
- 상세한 디버그 로그
```

## 🧪 테스트 방법

### 1. 개발 환경에서 테스트
```powershell
# 1. 프로세스 종료 (실행 중이면)
Stop-Process -Name "TradingBot" -Force -ErrorAction SilentlyContinue

# 2. Release 빌드
dotnet build TradingBot\TradingBot.csproj -c Release

# 3. 버전 확인
Get-Item ".\TradingBot\bin\Release\net9.0-windows\TradingBot.exe" | Select-Object Name, @{Name="Version";Expression={$_.VersionInfo.FileVersion}}

# 4. 실행
.\TradingBot\bin\Release\net9.0-windows\TradingBot.exe
```

**예상 동작**:
- 앱 시작 시 백그라운드에서 업데이트 확인
- 디버그 출력 창에 로그 표시:
  ```
  현재 버전: 1.0.0.0
  업데이트 확인 중...
  업데이트 관리자를 사용할 수 없습니다 (개발 환경일 수 있음)
  ```

### 2. 배포 환경 테스트 (Setup.exe 사용)

#### Step 1: 버전 1.0.0 배포
```powershell
# Setup.exe 생성
.\build_setup.bat
# 버전 입력: 1.0.0

# 생성된 파일 확인
Get-ChildItem .\Releases
# TradingBot-win-Setup.exe
# TradingBot-1.0.0-full.nupkg
```

#### Step 2: GitHub Release 생성
1. GitHub 저장소로 이동
2. `git tag v1.0.0` → `git push origin v1.0.0`
3. GitHub Actions가 자동으로 Release 생성
4. 또는 수동으로 Release 생성 후 파일 업로드:
   - TradingBot-win-Setup.exe
   - TradingBot-1.0.0-full.nupkg
   - RELEASES

#### Step 3: 버전 1.0.1 배포
```powershell
# 1. 버전 업데이트
# TradingBot.csproj 수정:
<Version>1.0.1</Version>
<AssemblyVersion>1.0.1.0</AssemblyVersion>
<FileVersion>1.0.1.0</FileVersion>

# 2. 새 Setup 생성
.\build_setup.bat
# 버전 입력: 1.0.1

# 3. GitHub Release 생성
git tag v1.0.1
git push origin v1.0.1
```

#### Step 4: 자동 업데이트 테스트
1. v1.0.0으로 설치된 앱 실행
2. 앱 시작 시 자동으로 v1.0.1 확인
3. 업데이트 다운로드 완료 후 알림 팝업 표시:
   ```
   새 버전 1.0.1이(가) 다운로드되었습니다.
   현재 버전: 1.0.0.0
   
   지금 재시작하여 업데이트를 적용하시겠습니까?
   
   (나중에 적용하려면 '아니요'를 선택하세요. 다음 실행 시 자동으로 적용됩니다.)
   ```
4. "예" 선택 → 자동 재시작 및 업데이트 적용
5. "아니요" 선택 → 다음 실행 시 자동 적용

## 🔧 설정 수정 필요

### GitHub Release URL 변경
**현재**: `https://github.com/YourAccount/TradingBot/releases`
**변경 필요**: App.xaml.cs 52번째 줄

```csharp
// 수정 전
var updateManager = new UpdateManager("https://github.com/YourAccount/TradingBot/releases");

// 수정 후 (실제 GitHub 계정명으로 변경)
var updateManager = new UpdateManager("https://github.com/실제계정명/TradingBot/releases");
```

## 📊 디버그 로그 확인

Visual Studio의 출력 창에서 다음 로그 확인:
```
현재 버전: 1.0.0.0
업데이트 확인 중...
새 버전 발견: 1.0.1
업데이트 다운로드 완료
업데이트 적용 및 재시작 중...
또는
사용자가 나중에 업데이트하기로 선택했습니다.
```

오류 발생 시:
```
업데이트 확인 실패: [오류 메시지]
스택 추적: [상세 정보]
```

## ⚠️ 주의사항

1. **개발 환경에서는 업데이트가 작동하지 않음**
   - Setup.exe로 설치된 환경에서만 정상 작동
   - 디버그 빌드에서는 "업데이트 관리자를 사용할 수 없습니다" 메시지 표시

2. **인터넷 연결 필요**
   - GitHub Releases에 접근하려면 인터넷 연결 필수
   - 연결 실패 시 조용히 무시되며 앱 시작은 정상 진행

3. **GitHub Release 구조**
   - Velopack은 특정 파일 구조를 요구
   - RELEASES, *.nupkg 파일이 필수
   - build_setup.bat가 자동으로 생성

## ✅ 검증 체크리스트

- [x] Version 정보가 TradingBot.csproj에 설정됨
- [x] VelopackApp.Build().Run() 호출됨
- [x] CheckForUpdatesAsync() 구현됨
- [x] 디버그 로깅 추가됨
- [x] 사용자 알림 UI 구현됨
- [ ] GitHub Release URL 변경 (실제 계정명으로)
- [ ] GitHub에 v1.0.0 Release 생성
- [ ] Setup.exe를 통한 실제 업데이트 테스트

## 🚀 배포 워크플로우

1. **코드 변경 및 버전 업데이트**
   ```powershell
   # TradingBot.csproj의 Version 증가
   # 예: 1.0.0 → 1.0.1
   ```

2. **빌드 및 패키징**
   ```powershell
   .\build_setup.bat
   # 새 버전 번호 입력
   ```

3. **Git 태그 생성**
   ```bash
   git add .
   git commit -m "Release v1.0.1"
   git tag v1.0.1
   git push origin main
   git push origin v1.0.1
   ```

4. **GitHub Actions 자동 배포**
   - `.github/workflows/build.yml`이 자동 실행
   - Release 자동 생성 및 파일 업로드

5. **사용자 자동 업데이트**
   - 설치된 앱이 실행될 때 자동으로 새 버전 감지
   - 다운로드 후 사용자에게 재시작 여부 확인

---

**작성일**: 2026-02-26  
**버전**: 1.0.0.0  
**상태**: ✅ 구현 완료, 실제 배포 테스트 대기중
