# [DEPRECATED] Process AI 아키텍처

## 상태

이 문서는 과거 외부 프로세스 분리 구조(MLService/TorchService) 기록입니다.
현재 운영 구조는 **단일 프로세스(in-process)** 입니다.

- 제거됨: `TradingBot.MLService`, `TradingBot.TorchService`
- 제거됨: Named Pipe IPC 기반 예측/학습 경로
- 현재: `AIPredictor`(ML.NET) + `TensorFlowEntryTimingTrainer`(TensorFlow.NET) 내부 백그라운드 실행

## 현재 권장 아키텍처

1. 메인 앱(`TradingBot`)에서 AI 예측/학습 직접 실행
2. 외부 exe 실행/헬스체크/IPC 없이 내부 서비스 호출
3. 배포 시 단일 앱 산출물 기준으로 검증

## 검증 명령

```powershell
dotnet build TradingBot.csproj -c Debug --no-incremental
```

## 참고

외부 프로세스 관련 이슈(파이프 연결 실패, 서비스 exe 미실행 등)는 현재 구조에 해당하지 않습니다.
문서/스크립트에서 해당 표현이 보이면 레거시 기록으로 취급하세요.
