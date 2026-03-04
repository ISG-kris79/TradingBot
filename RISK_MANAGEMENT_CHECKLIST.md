# 🛡️ Phase 14 리스크 관리 체크리스트

## 📋 개요

Phase 14 고급 거래 기능(차익거래, 자금 이동, 포트폴리오 리밸런싱)을 실제 운영 환경에 배포하기 전 필수 확인 사항입니다.

---

## ✅ 시뮬레이션 모드 검증

### 설정 확인

- [ ] `appsettings.json` 모든 `SimulationMode: true` 확인

  ```json
  "ArbitrageSettings": { "SimulationMode": true }
  "FundTransferSettings": { "SimulationMode": true }
  "PortfolioRebalancingSettings": { "SimulationMode": true }
  ```

### 테스트 시나리오

- [ ] **차익거래 서비스**
  - [ ] 기회 감지 로그 확인
  - [ ] Telegram 알림 수신 확인
  - [ ] DB에 시뮬레이션 로그 저장 확인
  - [ ] 실제 주문 실행되지 않음 확인 (Binance/Bybit API 로그)

- [ ] **자금 이동 서비스**
  - [ ] 잔고 체크 로직 동작 확인
  - [ ] 이동 필요 감지 시 로그 출력
  - [ ] 실제 출금/입금 API 호출 없음 확인

- [ ] **리밸런싱 서비스**
  - [ ] 포트폴리오 분석 로그 확인
  - [ ] 리밸런싱 액션 계산 정확성 검증
  - [ ] 실제 매수/매도 주문 없음 확인

---

## 🔒 보안 체크리스트

### API 키 관리

- [ ] `.gitignore`에 `appsettings.json` 포함 확인
- [ ] API 키는 환경 변수 또는 Azure Key Vault 사용 (프로덕션)
- [ ] ConnectionString 암호화 활성화 (`IsEncrypted: true`)
- [ ] Telegram 봇 토큰 노출 방지

### 권한 제한

- [ ] Binance API 키: 현물/선물 거래 권한만 부여 (출금 권한 제거)
- [ ] Bybit API 키: 필요 최소 권한만 설정
- [ ] IP 화이트리스트 설정 (거래소 대시보드)

### 데이터베이스 보안

- [ ] SQL Server 인증 모드: Windows 인증 사용 권장
- [ ] 데이터베이스 백업 스케줄 설정 (일 1회 이상)
- [ ] ConnectionString에 민감 정보 하드코딩 금지

---

## 💰 자금 관리

### 초기 설정

- [ ] **차익거래**
  - [ ] `DefaultQuantity`: 최소 금액으로 시작 (100 USDT)
  - [ ] `MinProfitPercent`: 보수적으로 설정 (0.5% 이상)
  - [ ] `AutoExecute: false` (수동 승인 권장)

- [ ] **자금 이동**
  - [ ] `MinTransferAmount`: 거래소 최소 출금액 이상
  - [ ] `TargetBalanceRatio`: 거래소별 리스크 분산 (50:50)

- [ ] **리밸런싱**
  - [ ] `RebalanceThreshold`: 너무 빈번한 실행 방지 (5% 이상)
  - [ ] 거래 수수료 고려한 임계값 설정

### 한도 설정

- [ ] 일일 최대 거래 횟수 제한 구현 검토
- [ ] 단일 거래 최대 금액 제한 (`MaxQuantity` 추가 고려)
- [ ] 누적 손실 시 자동 중지 로직 (Circuit Breaker)

---

## 🔍 모니터링 설정

### 로깅

- [ ] MainWindow 로그 출력 확인
- [ ] 파일 로깅 활성화 (NLog/Serilog 권장)
- [ ] 에러 로그 별도 파일 저장
- [ ] 로그 로테이션 설정 (최대 크기/일수)

### 데이터베이스 모니터링

- [ ] Phase 14 테이블 생성 확인 (`SETUP_GUIDE.md` 참조)
- [ ] 인덱스 생성 확인 (쿼리 성능)
- [ ] 통계 뷰 정상 동작 확인

### Telegram 알림

- [ ] 봇 토큰 및 채팅 ID 설정
- [ ] 알림 정상 수신 테스트
  - [ ] 차익거래 기회 감지
  - [ ] 차익거래 실행 결과
  - [ ] 자금 이동 완료/실패
  - [ ] 리밸런싱 완료

### 실시간 대시보드

- [ ] "ADVANCED FEATURES" 탭 UI 확인
- [ ] 시작/중지 버튼 동작
- [ ] 상태 표시 업데이트 (실행 중 🟢 / 중지됨)

---

## 🧪 통합 테스트

### End-to-End 시나리오

- [ ] **시나리오 1: 차익거래 전체 흐름**
  1. 서비스 시작 → 기회 감지 → Telegram 알림 → DB 저장
  2. 예상 시간: 스캔 간격(60초) 이내
  3. 확인: `ArbitrageExecutionLog` 테이블 INSERT

- [ ] **시나리오 2: 자금 이동 전체 흐름**
  1. 잔고 불균형 감지 → 이동 필요 계산 → 시뮬레이션 실행 → Telegram 알림
  2. 예상 시간: 체크 간격(60분) 이내
  3. 확인: `FundTransferLog` 테이블 INSERT

- [ ] **시나리오 3: 리밸런싱 전체 흐름**
  1. 포트폴리오 분석 → 편차 계산 → 액션 생성 → DB 저장 → Telegram 알림
  2. 예상 시간: 체크 간격(24시간) 이내
  3. 확인: `PortfolioRebalancingLog`, `RebalancingAction` 테이블 INSERT

### 성능 테스트

- [ ] 동시 실행 시 CPU/메모리 사용량 확인
- [ ] 장시간 실행 안정성 (24시간 이상)
- [ ] WebSocket 연결 안정성 (재연결 로직)

---

## 🚨 위기 대응 계획

### 비상 중지 절차

1. **UI에서 중지**: ADVANCED FEATURES 탭 → 각 서비스 "■ 중지" 버튼
2. **코드 중지**: `MainWindow.StopAdvancedFeatures()` 호출
3. **프로세스 종료**: 필요 시 앱 전체 종료

### 롤백 계획

- [ ] 이전 버전 실행 파일 백업 (`Releases/` 폴더)
- [ ] 데이터베이스 롤백 스크립트 준비 (`SETUP_GUIDE.md` 참조)
- [ ] 설정 파일 백업 (`appsettings.json.backup`)

### 긴급 연락처

- [ ] 개발팀 연락처 공유
- [ ] 거래소 고객센터 연락처 저장
- [ ] Telegram 알림 채널 항상 확인

---

## 📊 실운영 전환 체크리스트

### 시뮬레이션 → 프로덕션 전환

⚠️ **아래 모든 항목이 체크되어야 실운영 전환 가능**

- [ ] ✅ 모든 시뮬레이션 테스트 통과 (최소 1주일)
- [ ] ✅ 통계 뷰 정상 동작 확인
- [ ] ✅ Telegram 알림 100% 수신 확인
- [ ] ✅ 데이터베이스 백업 완료
- [ ] ✅ 실행 파일 백업 완료
- [ ] ✅ 비상 중지 절차 숙지

### 설정 변경

```json
// appsettings.json 수정
"ArbitrageSettings": {
  "SimulationMode": false  // ⚠️ true → false
}
"FundTransferSettings": {
  "SimulationMode": false  // ⚠️ true → false
}
"PortfolioRebalancingSettings": {
  "SimulationMode": false  // ⚠️ true → false
}
```

### 점진적 배포

1. **1단계 (1-3일)**: 차익거래만 활성화 (`AutoExecute: false`, 수동 승인)
2. **2단계 (3-7일)**: 차익거래 자동 실행 (`AutoExecute: true`, 소액)
3. **3단계 (7-14일)**: 자금 이동 활성화 (소액 테스트)
4. **4단계 (14일+)**: 리밸런싱 활성화 (전체 시스템)

### 모니터링 강화

- [ ] 실시간 로그 모니터링 (최소 1주일)
- [ ] 일일 통계 리포트 확인
- [ ] 주간 수익/손실 분석
- [ ] 월간 성과 리뷰

---

## 📝 문서화

### 운영 매뉴얼

- [ ] 서비스 시작/중지 절차
- [ ] 설정 변경 가이드
- [ ] 문제 해결 FAQ

### 코드 문서

- [ ] 주요 클래스 XML 주석 완료
- [ ] README.md 업데이트
- [ ] CHANGELOG.md Phase 14 항목 추가

---

## 🎯 최종 승인

**검토자**: _________________  
**승인 날짜**: _________________  
**서명**: _________________  

**실운영 전환 날짜**: _________________  
**전환 담당자**: _________________  

---

## 📞 지원 및 리포트

- **GitHub Issues**: 버그 리포트 및 기능 요청
- **Email**: [support@tradingbot.com](mailto:support@tradingbot.com)
- **Telegram**: @TradingBotSupport

---

**최종 업데이트**: 2026-02-28  
**버전**: Phase 14 v1.0  
**작성자**: TradingBot Development Team
