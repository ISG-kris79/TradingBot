# CoinFF TradingBot 🤖

![Build Status](https://github.com/ISG-kris79/TradingBot/actions/workflows/build.yml/badge.svg)

CoinFF TradingBot은 C# WPF 기반의 암호화폐 자동 매매 프로그램입니다. Binance, Bitget, Bybit 등 주요 거래소의 선물 거래를 지원하며, AI 예측 및 다양한 알고리즘 전략을 탑재하고 있습니다.

## ✨ 주요 기능

* **다중 거래소 지원**: Binance, Bybit (API 연동)
* **AI 기반 예측**: ML.NET + TensorFlow.NET 기반 인프로세스(내부 백그라운드) 시장 분석
* **다양한 전략**:
  * **Pump Scan**: 실시간 급등주 포착 및 스캘핑
  * **Grid Strategy**: 횡보장 수익 극대화를 위한 그리드 매매
  * **Arbitrage**: 거래소 간 가격 차이를 이용한 차익 거래
* **백테스팅**: 과거 데이터를 기반으로 전략 수익률 검증 및 시뮬레이션
* **안정성**: 서킷 브레이커, 자동 재연결, 텔레그램 알림 및 원격 제어

## ⚙️ 설정 관리 (Configuration)

### GeneralSettings 초기화 시스템

앱 시작 시 자동으로 GeneralSettings를 로드합니다:

1. **appsettings.json** 기본값 로드
2. **DB 사용자별 설정** 로드 (우선순위 높음)
3. **메모리 캐시**: `MainWindow.CurrentGeneralSettings` static 필드에 저장

**사용 예시 (TradingEngine):**

```csharp
// TradingEngine 초기화 시
_settings = MainWindow.CurrentGeneralSettings 
    ?? AppConfig.Current?.Trading?.GeneralSettings 
    ?? new TradingSettings();

// 실시간 설정 접근
int leverage = _settings.DefaultLeverage;
decimal margin = _settings.DefaultMargin;
decimal targetRoe = _settings.TargetRoe;
```

**GeneralSettingsProvider 싱글톤:**

```csharp
// 싱글톤 인스턴스로 접근
var settings = GeneralSettingsProvider.Instance.GetSettings();

// 빠른 접근 헬퍼
int leverage = GeneralSettingsProvider.Instance.DefaultLeverage;
decimal margin = GeneralSettingsProvider.Instance.DefaultMargin;

// 설정 저장
await GeneralSettingsProvider.Instance.SaveSettingsAsync(newSettings);

// 설정 새로고침
await GeneralSettingsProvider.Instance.RefreshSettingsAsync();
```

**설정 항목:**

* `DefaultLeverage`: 기본 레버리지 (기본값: 20x)
* `DefaultMargin`: 기본 마진 (기본값: 200 USDT)
* `TargetRoe`: 목표 수익률 (기본값: 20%)
* `StopLossRoe`: 손절 수익률 (기본값: 15%)
* `TrailingStartRoe`: 트레일링 시작 수익률 (기본값: 20%)
* `TrailingDropRoe`: 트레일링 드롭 수익률 (기본값: 5%)

## 🛠 기술 스택 (Tech Stack)

### Core Framework

* **Framework**: [.NET 9.0](https://dotnet.microsoft.com/) (C# Latest)
* **UI Framework**: WPF (Windows Presentation Foundation)
* **Architecture**: MVVM (Model-View-ViewModel) with [ReactiveUI 23.1.1](https://www.reactiveui.net/)

### 거래소 연동 (Exchange Integration)

* **Binance**: [Binance.Net 12.5.2](https://github.com/JKorf/Binance.Net) - REST API & WebSocket
* **Bybit**: Custom implementation

### AI/ML & 데이터 분석

* **Machine Learning**: [ML.NET 5.0.0](https://dotnet.microsoft.com/apps/machinelearning-ai/ml-dotnet) + FastTree 5.0.0
* **Deep Learning**: [TensorFlow.NET 0.150.0](https://github.com/SciSharp/TensorFlow.NET) - Transformer 대체 시계열 예측 경로
* **Technical Indicators**: [Skender.Stock.Indicators 2.7.1](https://github.com/DaveSkender/Stock.Indicators)
* **Data Pipeline**: TimeSeriesDataLoader - 최적화된 배치 처리 및 정규화

### 데이터베이스 (Database)

* **SQL Server**: [Microsoft.Data.SqlClient 6.1.4](https://github.com/dotnet/SqlClient)
* **ORM**: [Dapper 2.1.66](https://github.com/DapperLib/Dapper) - Micro ORM
* **CSV Export**: [CsvHelper 33.1.0](https://joshclose.github.io/CsvHelper/)

### UI/UX

* **Charting**: [LiveCharts.Wpf 0.9.7](https://lvcharts.net/) - 실시간 차트 시각화
* **MVVM Binding**: ReactiveUI - 반응형 데이터 바인딩

### 로깅 & 알림 (Logging & Notifications)

* **Logging**: [Serilog 3.1.1](https://serilog.net/)

* Serilog.Sinks.Console 5.0.1
* Serilog.Sinks.File 5.0.0

* **Telegram Bot**: [Telegram.Bot 22.9.0](https://github.com/TelegramBots/Telegram.Bot) - 알림 및 원격 제어

### 배포 & 업데이트 (Deployment & Updates)

* **Auto Update**: [Velopack 0.0.1298](https://velopack.io/) - 자동 업데이트 프레임워크
* **CI/CD**: GitHub Actions

### 기타 도구 (Utilities)

* **Configuration**: Microsoft.Extensions.Configuration 9.0.0 (JSON, UserSecrets)

## 프로젝트 구조 (Project Structure)

```text
CoinFF/
├── TradingBot/                     # 메인 프로젝트 (WPF 애플리케이션)
│   ├── Models/                     # 데이터 모델 (DTO, Entity)
│   ├── Services/                   # 비즈니스 로직 서비스 (DB, API, 백테스팅 등)
│   ├── Strategies/                 # 매매 전략 구현체 (Grid, Arbitrage, Pump 등)
│   ├── ViewModels/                 # MVVM 패턴의 ViewModel
│   ├── AppConfig.cs                # 애플리케이션 설정 관리
│   ├── DatabaseService.cs          # 데이터베이스 연동 (Dapper/MSSQL)
│   ├── MainWindow.xaml             # 메인 UI 진입점
│   ├── MarketDataManager.cs        # 실시간 시세 데이터 관리 (WebSocket)
│   └── TradingEngine.cs            # 핵심 매매 엔진 및 스케줄러
├── .github/workflows/              # GitHub Actions CI/CD 워크플로우 설정
├── Releases/                       # 빌드된 배포 파일 저장소 (Setup.exe)
├── build_release.bat               # 배포용 빌드 스크립트 (Velopack)
└── generate_release_notes.ps1      # 릴리스 노트 자동 생성 스크립트
```

## � 설치 및 실행

### 일반 사용자

1. **요구 사항**: Windows 10/11, .NET 9.0 Runtime
2. **설치**:
    * GitHub 저장소의 [Releases](https://github.com/ISG-kris79/TradingBot/releases) 페이지에서 최신 버전의 `TradingBot-win-Setup.exe`를 다운로드
    * `TradingBot-win-Setup.exe`를 실행하여 설치 (Velopack 자동 업데이트 지원)
    * ⚠️ 첫 릴리스가 아직 없는 경우, 아래 개발자 빌드 방법을 참고하세요
3. **설정**:
    * 프로그램 실행 후 `Settings` 메뉴 진입
    * 거래소 API Key 입력 (Binance 등)
    * Telegram Bot Token 및 Chat ID 입력 (알림 수신용)

### 개발자 (로컬 빌드 및 실행)

```bash
# 저장소 클론
git clone https://github.com/ISG-kris79/TradingBot

# 패키지 복원
dotnet restore

# 빌드 및 실행
dotnet build
dotnet run --project TradingBot/TradingBot.csproj
```

**설치 파일 생성 (Setup.exe):**

방법 1: PowerShell 스크립트 사용 (권장)

```powershell
# build_setup.ps1 실행 (버전 입력 필요)
.\build_setup.ps1
# Releases\TradingBot-win-Setup.exe 생성됨
```

방법 2: 배치 파일 사용

```bash
# build_setup.bat 실행
build_setup.bat
# Releases\TradingBot-win-Setup.exe 생성됨
```

방법 3: 수동 명령어

```bash
# Velopack 도구 설치
dotnet tool install -g vpk

# 애플리케이션 퍼블리시
dotnet publish TradingBot/TradingBot.csproj -c Release -r win-x64 --self-contained -o ./publish

# Velopack으로 패키징
vpk pack -u TradingBot -v 1.0.0 -p ./publish -e TradingBot.exe

# Releases 폴더에 TradingBot-win-Setup.exe 생성됨
```

> **참고:**
>
> * 배치 파일(.bat)에서 한글이 깨지는 경우 PowerShell 스크립트(.ps1)를 사용하세요.
> * 첫 빌드 후에는 `VelopackApp.Build().Run()` 코드가 자동으로 포함되어 Setup.exe가 정상 생성됩니다.

## 📦 배포 (Deployment)

### 🚀 Visual Studio 자동 배포 (권장)

Visual Studio에서 **게시(Publish)** 버튼을 클릭하면 자동으로:

1. ✅ Velopack 패키징 (`TradingBot-win-Setup.exe` 생성)
2. ✅ 릴리스 노트 자동 생성 (Git 커밋 히스토리 분석)
3. ✅ GitHub 릴리스 생성 및 파일 업로드

**사용 방법:**

1. **버전 설정**: `TradingBot.csproj`에서 버전 수정

   ```xml
   <PropertyGroup>
     <Version>1.0.1</Version>
   </PropertyGroup>
   ```

2. **게시 실행**:
   * Solution Explorer에서 **TradingBot** 프로젝트 우클릭
   * **게시(Publish)** 선택
   * **VelopackRelease** 프로필 선택
   * **게시** 버튼 클릭

3. **자동 실행**:
   * 빌드 및 퍼블리시
   * Velopack 패키징
   * 릴리스 노트 생성
   * GitHub 릴리스 생성 (파일 자동 업로드)

4. **결과 확인**:
   * `Releases/` 폴더에 Setup.exe 생성
   * GitHub Releases 페이지에 자동 업로드

**📘 상세 가이드**: [DEPLOYMENT.md](DEPLOYMENT.md) 참조

**필수 사전 준비**:

```powershell
# Velopack CLI 설치
dotnet tool install -g vpk

# GitHub CLI 설치 (자동 릴리스용)
winget install GitHub.cli
gh auth login
```

---

### 🤖 GitHub Actions CI/CD

이 프로젝트는 GitHub Actions를 통해 배포가 자동화되어 있습니다.

**배포 방법:**

1. 코드 변경 사항을 커밋 및 푸시

   ```bash
   # 커밋 메시지 규칙 (Conventional Commits 권장)
   git commit -m "feat: 새로운 기능 추가"
   git commit -m "fix: 버그 수정"
   git commit -m "docs: 문서 업데이트"
   ```

2. Git 태그 생성 및 푸시:

   ```bash
   git tag v1.0.0
   git push origin v1.0.0
   ```

3. GitHub Actions가 자동으로 빌드 및 릴리스 생성
4. 릴리스 노트가 커밋 히스토리에서 자동 생성됨
5. Releases 페이지에 다음 파일들이 업로드됩니다:
   * `TradingBot-win-Setup.exe`: 초기 설치 파일 (권장)
   * `TradingBot-win-Portable.zip`: 포터블 버전
   * `TradingBot-1.0.0-full.nupkg`: Velopack 업데이트 패키지
   * `RELEASES`: 업데이트 매니페스트
   * `assets.json`: 에셋 정보

### 📝 릴리스 노트 자동 생성

릴리스 노트는 커밋 메시지를 분석하여 자동으로 생성됩니다.

**로컬에서 미리보기:**

```powershell
# 릴리스 노트 생성 스크립트 실행
.\generate_release_notes.ps1 -CurrentVersion "1.0.1"

# 또는 이전 태그 직접 지정
.\generate_release_notes.ps1 -PreviousTag "v1.0.0" -CurrentVersion "1.0.1"
```

**커밋 메시지 규칙 (권장):**

* `feat:` - 새로운 기능 (✨ Features 섹션)
* `fix:` - 버그 수정 (🐛 Bug Fixes 섹션)
* `docs:` - 문서 변경 (📚 Documentation 섹션)
* `perf:` - 성능 개선 (⚡ Performance 섹션)
* `refactor:` - 코드 리팩토링 (🔧 Others 섹션)
* `style:` - 코드 스타일 변경
* `chore:` - 빌드/설정 변경

**예시:**

```bash
git commit -m "feat: RSI 다이버전스 전략 추가"
git commit -m "fix: API 재연결 오류 수정"
git commit -m "perf: ML 모델 추론 속도 15% 개선"
git commit -m "docs: 설치 가이드 업데이트"
```

**수동으로 CHANGELOG.md 관리:**

* `CHANGELOG.md` 파일을 직접 수정하여 릴리스 노트 관리 가능
* [Keep a Changelog](https://keepachangelog.com/ko/1.0.0/) 형식 준수
* 배포 전 `[Unreleased]` 섹션을 새 버전으로 변경

## ❓ 트러블슈팅 (Troubleshooting)

자주 발생하는 문제와 해결 방법입니다.

### 1. "Root element is missing" 또는 설정 오류

* **증상**: 프로그램 실행 시 설정 로드 오류가 발생하거나 실행되지 않음.
* **원인**: `user.config` 파일이 손상되었을 수 있습니다.
* **해결**: 프로그램이 자동으로 손상된 설정 파일을 감지하고 삭제합니다. 재실행해 보세요. 만약 해결되지 않으면 `%localappdata%\TradingBot` 폴더를 수동으로 삭제하세요.

### 2. API 연결 실패 (401 Unauthorized)

* **증상**: "API Key가 유효하지 않습니다" 또는 연결 실패 로그.
* **해결**:
  * API Key와 Secret이 정확한지 확인하세요 (공백 주의).
  * 거래소에서 API Key 권한(Futures Trading 등)이 활성화되었는지 확인하세요.
  * 서버 IP가 변경되었다면 거래소 화이트리스트 IP를 업데이트하세요.

### 3. 텔레그램 알림이 오지 않음

* **증상**: 봇은 동작하지만 텔레그램 메시지가 없음.
* **해결**:
  * Bot Token과 Chat ID가 올바른지 확인하세요.
  * 봇에게 먼저 `/start` 메시지를 보내 대화를 시작해야 합니다.

### 4. CI/CD 배포 실패 (GitHub Actions)

* **증상**: Actions 탭에서 워크플로우가 실패 표시됨.
* **해결**:
  * **로그 확인**: 실패한 단계(Step)를 클릭하여 상세 로그를 확인합니다. `vpk` 패키징 오류나 빌드 오류가 주원인입니다.
  * **버전 태그**: `v1.0.0` 형식의 태그가 아니면 버전 파싱에 실패할 수 있습니다. (수동 실행 시 주의)
  * **시크릿**: `TELEGRAM_BOT_TOKEN` 등이 Settings > Secrets에 등록되었는지 확인하세요.

## ⚠️ 주의 사항

* 이 프로그램은 투자를 보조하는 도구이며, 수익을 보장하지 않습니다.
* API Key는 반드시 **IP 제한**을 설정하여 사용하십시오.
* 초기에는 **Simulation Mode** 또는 소액으로 충분히 테스트하시기 바랍니다.

## 🗺️ 로드맵 (Roadmap)

이 프로젝트는 지속적으로 발전하고 있습니다. 향후 계획된 주요 기능들은 다음과 같습니다.

### Phase 7: 고급 AI 및 최적화 (예정)

* [ ] **Transformer 기반 모델 도입**: 기존 LSTM을 넘어선 최신 시계열 예측 모델 적용

* [ ] **강화학습(RL) 고도화**: PPO, SAC 등 심층 강화학습 알고리즘 적용 및 학습 환경 개선
* [ ] **하이퍼파라미터 자동 튜닝**: Optuna 등을 활용한 전략 파라미터 자동 최적화

### Phase 8: DeFi 및 확장성

* [ ] **DEX 연동**: Uniswap, dYdX 등 탈중앙화 거래소 지원

* [ ] **On-chain 데이터 분석**: 고래 지갑 추적 및 트랜잭션 분석 기능
* [ ] **모바일 앱 연동**: 상태 모니터링을 위한 모바일 컴패니언 앱 개발

### 커뮤니티 (Community)

* [ ] **커뮤니티 피드백 반영**: 사용자 피드백을 기반으로 한 기능 개선 및 우선순위 조정

## 🤝 기여 방법 (Contributing)

이 프로젝트에 기여하고 싶으신가요? 환영합니다! 다음 가이드라인을 따라주세요.

1. **Fork & Clone**: 저장소를 포크하고 로컬에 클론합니다.
2. **Branch 생성**: 작업 유형에 맞는 브랜치를 생성합니다.
    * 기능 추가: `feat/기능명`
    * 버그 수정: `fix/버그명`
3. **Commit 메시지**: Conventional Commits 규칙을 따릅니다. (예: `feat: 새로운 기능 추가`, `fix: 버그 수정`)
4. **Pull Request**: 작업이 완료되면 `main` 브랜치로 PR을 생성합니다.

## 📝 라이선스

이 프로젝트는 MIT 라이선스 하에 배포됩니다. 자세한 내용은 **LICENSE** 파일을 참조하세요.
