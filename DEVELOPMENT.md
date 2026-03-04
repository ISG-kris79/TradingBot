# 🛠️ 개발 프로세스 가이드 (Development Process)

CoinFF TradingBot의 개발, 테스트, 배포 프로세스를 정리한 문서입니다.

## 1. 브랜치 전략 (Branching Strategy)
- **main**: 배포 가능한 안정 버전 (Production). 모든 PR은 이곳으로 병합됩니다.
- **feat/**: 새로운 기능 개발 (예: `feat/transformer-model`)
- **fix/**: 버그 수정 (예: `fix/api-reconnect`)
- **refactor/**: 코드 리팩토링 (기능 변경 없음)
- **docs/**: 문서 작업

## 2. 개발 워크플로우 (Workflow)
1. **이슈 생성**: GitHub Issues에 작업할 내용 등록
2. **브랜치 생성**: 작업 유형에 맞는 브랜치 생성
3. **개발 및 테스트**:
   - 로컬 빌드: `dotnet build`
   - 백테스팅 검증: `BacktestService`를 통한 전략 수익률 검증
4. **커밋 (Commit)**: Conventional Commits 규칙 준수
5. **PR (Pull Request)**: `main` 브랜치로 PR 생성 및 코드 리뷰
6. **병합 (Merge)**: 승인 후 병합 (CI/CD 자동 실행)

## 3. 품질 관리 (Quality Assurance)
- **시뮬레이션 (Paper Trading)**: `MockExchangeService`를 활용한 가상 매매 테스트
- **정적 분석**: IDE 내장 분석기 활용, Warning 제거 및 코드 스타일 통일

## 4. 배포 프로세스 (Release)
1. **버전 업데이트**: `.csproj` 버전 수정
2. **태그 푸시**: `v1.x.x` 태그 생성 (GitHub Actions 트리거)
3. **자동 배포**: Velopack 패키지 생성 및 GitHub Releases 업로드
4. **모니터링**: 텔레그램 알림 확인 및 초기 가동 모니터링

## 5. 문서화 (Documentation)
- **README.md**: 프로젝트 개요 및 설치 방법
- **CHANGELOG.md**: 버전별 변경 이력
- **TODO.md**: 개발 로드맵 및 작업 현황
- **RELEASE_NOTES_GUIDE.md**: 릴리스 노트 작성 가이드