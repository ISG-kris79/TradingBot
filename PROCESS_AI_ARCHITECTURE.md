# AI 서비스 프로세스 분리 아키텍처

## 개요

TorchSharp BEX64 크래시 문제를 해결하기 위해 ML.NET과 TorchSharp를 별도 프로세스로 분리했습니다.
이를 통해 AI 모델 크래시가 메인 TradingBot 프로세스에 영향을 주지 않습니다.

## 아키텍처

```
┌─────────────────┐
│  TradingBot.exe │  ← 메인 WPF 프로세스
│  (Main Process) │
└────────┬────────┘
         │
    Named Pipe IPC
         │
    ┌────┴──────────────────┐
    │                       │
┌───▼──────────────┐  ┌────▼─────────────────┐
│ MLService.exe    │  │  TorchService.exe    │
│ (ML.NET 전용)    │  │  (TorchSharp 전용)   │
│  - 예측          │  │  - Transformer 예측  │
│  - 학습          │  │  - Transformer 학습  │
│  - 크래시 격리   │  │  - BEX64 격리       │
└──────────────────┘  └──────────────────────┘
```

## 프로젝트 생성

### 1. MLService 프로젝트 생성

```powershell
cd E:\PROJECT\CoinFF\TradingBot
dotnet new console -n TradingBot.MLService -f net9.0
cd TradingBot.MLService

# 필요한 패키지 추가
dotnet add package Microsoft.ML
dotnet add package System.IO.Pipes

# Program.cs 교체
# Services/ProcessAI/MLService_Program.cs 내용을 Program.cs로 복사
```

### 2. TorchService 프로젝트 생성

```powershell
cd E:\PROJECT\CoinFF\TradingBot
dotnet new console -n TradingBot.TorchService -f net9.0
cd TradingBot.TorchService

# 필요한 패키지 추가
dotnet add package TorchSharp
dotnet add package TorchSharp-cpu
dotnet add package System.IO.Pipes

# Program.cs 교체
# Services/ProcessAI/TorchService_Program.cs 내용을 Program.cs로 복사
```

### 3. 솔루션에 프로젝트 추가

```powershell
cd E:\PROJECT\CoinFF\TradingBot\TradingBot
dotnet sln TradingBot.sln add ../TradingBot.MLService/TradingBot.MLService.csproj
dotnet sln TradingBot.sln add ../TradingBot.TorchService/TradingBot.TorchService.csproj
```

## 메인 프로세스 통합

### AIPredictor 교체 예시

**기존 코드:**
```csharp
private AIPredictor _aiPredictor = new AIPredictor();

var prediction = _aiPredictor.Predict(candleData);
```

**새 코드:**
```csharp
using TradingBot.Services.ProcessAI;

private MLServiceClient _mlServiceClient = new MLServiceClient();

// 초기화 시
await _mlServiceClient.StartAsync();

// 예측 시
var prediction = await _mlServiceClient.PredictAsync(candleData);

// 종료 시
_mlServiceClient.Dispose();
```

### Transformer 교체 예시

**기존 코드:**
```csharp
private EntryTimingTransformerTrainer _transformerTrainer = new EntryTimingTransformerTrainer();

var (candlesToTarget, confidence) = _transformerTrainer.Predict(sequence);
```

**새 코드:**
```csharp
using TradingBot.Services.ProcessAI;

private TorchServiceClient _torchServiceClient = new TorchServiceClient();

// 초기화 시
await _torchServiceClient.StartAsync();

// 예측 시
var (candlesToTarget, confidence) = await _torchServiceClient.PredictAsync(sequence);

// 종료 시
_torchServiceClient.Dispose();
```

## 빌드 설정

### 1. 출력 경로 설정

각 서비스 프로젝트의 `.csproj`에 추가:

```xml
<PropertyGroup>
  <OutputPath>$(SolutionDir)TradingBot\bin\$(Configuration)\net9.0-windows\win-x64\Services\</OutputPath>
</PropertyGroup>
```

이렇게 하면 서비스 실행파일이 메인 앱의 Services 폴더에 빌드됩니다.

### 2. 배포 시 포함

TradingBot.csproj에 추가:

```xml
<ItemGroup>
  <None Include="$(OutputPath)Services\TradingBot.MLService.exe">
    <CopyToOutputDirectory>PreserveNewest</CopyToOutputDirectory>
  </None>
  <None Include="$(OutputPath)Services\TradingBot.TorchService.exe">
    <CopyToOutputDirectory>PreserveNewest</CopyToOutputDirectory>
  </None>
</ItemGroup>
```

## 장점

### 1. 크래시 격리
- TorchSharp BEX64 크래시가 발생해도 메인 프로세스는 영향 없음
- 서비스 프로세스만 재시작하면 됨

### 2. 독립적인 메모리 관리
- 각 프로세스가 독립적인 메모리 공간 사용
- ML 모델 메모리 누수가 메인 프로세스에 영향 없음

### 3. 개별 재시작
- 모델 재학습 시 서비스만 재시작
- UI는 중단 없이 계속 동작

### 4. 버전 관리 용이
- ML.NET과 TorchSharp 버전을 독립적으로 업데이트 가능

## 모니터링

### Health Check

```csharp
// MLService 상태 확인
bool mlReady = _mlServiceClient.IsModelLoaded;

// TorchService 상태 확인
bool torchReady = _torchServiceClient.IsModelReady;
```

### 자동 재시작

프로세스 매니저가 30초마다 Health Check를 수행하고, 
실패 시 자동으로 프로세스를 재시작합니다.

## 성능 고려사항

### IPC 오버헤드
- Named Pipe 통신은 약 1-5ms 오버헤드 발생
- 배치 예측으로 오버헤드 최소화 권장

### 메모리
- 3개 프로세스(메인 + ML + Torch)가 동시 실행
- 총 메모리 사용량 증가 (약 500MB 추가)

### 권장 설정
- 샘플링 간격을 늘려서 예측 빈도 감소
- 배치 예측 사용 (여러 심볼을 한 번에 예측)

## 문제 해결

### 프로세스가 시작되지 않는 경우
1. 실행 파일 경로 확인: `Services/TradingBot.MLService.exe`
2. .NET 9.0 Runtime 설치 확인
3. 로그 확인: OnLog 이벤트 구독

### Named Pipe 연결 실패
1. 방화벽 설정 확인 (Named Pipe는 로컬 통신)
2. 프로세스가 실제로 실행 중인지 확인 (작업 관리자)
3. 다른 인스턴스가 Pipe를 사용 중인지 확인

### 성능 저하
1. IPC 호출 빈도 줄이기 (캐싱 활용)
2. 배치 예측 사용
3. 타임아웃 조정 (기본 30초)

## 다음 단계

1. TradingEngine.cs에서 기존 AIPredictor/Transformer 호출을 새 클라이언트로 교체
2. AIDoubleCheckEntryGate.cs 통합
3. 배포 빌드 및 테스트
4. v2.5.0 릴리스 준비

## 참고 파일

- `Services/ProcessAI/AIServiceContracts.cs` - 통신 계약
- `Services/ProcessAI/NamedPipeServer.cs` - 서버 측 IPC
- `Services/ProcessAI/NamedPipeClient.cs` - 클라이언트 측 IPC
- `Services/ProcessAI/AIServiceProcessManager.cs` - 프로세스 관리
- `Services/ProcessAI/MLServiceClient.cs` - ML.NET 클라이언트 래퍼
- `Services/ProcessAI/TorchServiceClient.cs` - TorchSharp 클라이언트 래퍼
- `Services/ProcessAI/MLService_Program.cs` - ML 서비스 엔트리포인트
- `Services/ProcessAI/TorchService_Program.cs` - Torch 서비스 엔트리포인트
