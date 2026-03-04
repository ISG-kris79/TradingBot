# 릴리스 노트 자동 생성 가이드

## 📝 개요

TradingBot은 GitHub Actions를 통해 릴리스 노트를 자동으로 생성합니다. 커밋 메시지를 분석하여 카테고리별로 정리된 릴리스 노트를 만듭니다.

## 🚀 전체 워크플로우

### 1. 코드 작업 및 커밋

**Conventional Commits 형식 사용 (권장):**

```bash
# 새로운 기능
git commit -m "feat: RSI 다이버전스 전략 추가"
git commit -m "feat(ui): 다크 모드 테마 지원"

# 버그 수정
git commit -m "fix: API 재연결 시 메모리 누수 수정"
git commit -m "fix(trading): 주문 취소 시 상태 업데이트 오류 해결"

# 성능 개선
git commit -m "perf: ML 모델 추론 속도 15% 개선"
git commit -m "perf(db): 데이터베이스 쿼리 최적화"

# 문서
git commit -m "docs: API 설정 가이드 추가"
git commit -m "docs: README 트러블슈팅 섹션 업데이트"

# 리팩토링
git commit -m "refactor: TradingEngine 코드 정리"

# 기타
git commit -m "chore: 의존성 패키지 업데이트"
git commit -m "style: 코드 포맷팅 적용"
```

### 2. 로컬에서 릴리스 노트 미리보기

배포 전에 릴리스 노트가 어떻게 생성되는지 확인:

```powershell
# 기본 사용 (이전 태그 자동 탐지)
.\generate_release_notes.ps1 -CurrentVersion "1.0.1"

# 이전 태그 직접 지정
.\generate_release_notes.ps1 -PreviousTag "v1.0.0" -CurrentVersion "1.0.1"

# 생성된 파일 확인
notepad release_notes.md
```

### 3. CHANGELOG.md 업데이트 (선택사항)

자동 생성 전에 CHANGELOG.md를 수동으로 업데이트하여 더 자세한 설명 추가:

```markdown
## [1.0.1] - 2026-03-01

### Added
- RSI 다이버전스 전략 추가
  - Regular Divergence (일반 다이버전스)
  - Hidden Divergence (숨은 다이버전스)
  - 자동 매매 진입/청산 로직 포함

### Fixed
- API 재연결 시 발생하던 메모리 누수 문제 해결
- 주문 취소 시 포지션 상태가 올바르게 업데이트되지 않던 버그 수정

### Performance
- ML 모델 추론 속도 15% 개선
  - ONNX Runtime GPU 가속 적용
  - 배치 처리 최적화
```

### 4. 태그 생성 및 푸시

```bash
# 변경사항 푸시
git push origin main

# 새 버전 태그 생성
git tag v1.0.1

# 태그 푸시 (GitHub Actions 자동 실행)
git push origin v1.0.1
```

### 5. GitHub Actions 자동 실행

태그가 푸시되면 자동으로:
1. ✅ 코드 체크아웃
2. ✅ .NET 9.0 설치
3. ✅ Velopack 도구 설치
4. ✅ 애플리케이션 퍼블리시
5. ✅ Velopack으로 패키징
6. ✅ **릴리스 노트 자동 생성** ← 여기서 자동 생성!
7. ✅ GitHub Release 생성 및 파일 업로드
8. ✅ 텔레그램 알림 발송

### 6. 결과 확인

GitHub Releases 페이지에서 확인:
- 📦 다운로드 파일 (Setup.exe, Portable.zip 등)
- 📝 자동 생성된 릴리스 노트
- 📋 변경 사항 카테고리별 정리
- 🔗 커밋 해시 링크

## 📊 릴리스 노트 구조

자동 생성된 릴리스 노트는 다음 섹션으로 구성됩니다:

```markdown
## 🚀 TradingBot v1.0.1

**릴리스 날짜**: 2026-03-01

### 📦 다운로드
[파일 목록 테이블]

### ✨ 새로운 기능 (Features)
- feat: ... (커밋해시)
- feat(ui): ... (커밋해시)

### 🐛 버그 수정 (Bug Fixes)
- fix: ... (커밋해시)
- fix(trading): ... (커밋해시)

### ⚡ 성능 개선 (Performance)
- perf: ... (커밋해시)

### 📚 문서 (Documentation)
- docs: ... (커밋해시)

### 🔧 기타 변경사항 (Others)
- refactor: ... (커밋해시)
- chore: ... (커밋해시)

---

### 📋 시스템 요구사항
[요구사항 목록]

### 🔧 설치 방법
[설치 가이드]

### ⚠️ 주의사항
[주의사항]

**Full Changelog**: [v1.0.0...v1.0.1 비교 링크]
```

## 🎯 커밋 메시지 베스트 프랙티스

### ✅ 좋은 예시

```bash
# 구체적이고 명확한 설명
git commit -m "feat: Bollinger Band squeeze 전략 추가"
git commit -m "fix: WebSocket 재연결 시 중복 스트림 방지"
git commit -m "perf: 캔들 데이터 캐싱으로 메모리 사용량 30% 감소"

# 범위(scope) 지정으로 더 명확하게
git commit -m "feat(strategy): Volume Profile 분석 추가"
git commit -m "fix(ui): 차트 줌 인/아웃 버그 수정"
git commit -m "docs(api): Binance API 설정 가이드 추가"
```

### ❌ 나쁜 예시

```bash
# 너무 모호함
git commit -m "업데이트"
git commit -m "버그 수정"
git commit -m "작업 중"

# 형식 없음
git commit -m "RSI 전략 추가했고 버그도 수정함"
git commit -m "여러가지 변경"
```

## 🔧 고급 기능

### 수동으로 릴리스 노트 편집

GitHub Actions가 생성한 릴리스를 나중에 수동으로 편집 가능:

1. GitHub Releases 페이지 접속
2. 해당 릴리스의 "Edit release" 클릭
3. 설명(Body) 수정
4. "Update release" 클릭

### 드래프트 릴리스 생성

자동 생성된 릴리스를 먼저 드래프트로 만들고 싶다면 `dotnet.yml` 수정:

```yaml
- name: Create GitHub Release
  with:
    draft: true  # false → true 변경
    prerelease: false
```

### Pre-release 표시

베타/알파 버전인 경우:

```bash
# 베타 버전 태그
git tag v1.0.1-beta
git push origin v1.0.1-beta
```

그리고 워크플로우 수정:

```yaml
- name: Create GitHub Release
  with:
    draft: false
    prerelease: ${{ contains(github.ref, 'beta') || contains(github.ref, 'alpha') }}
```

## 📚 추가 리소스

- [Conventional Commits 가이드](https://www.conventionalcommits.org/ko/v1.0.0/)
- [Keep a Changelog](https://keepachangelog.com/ko/1.0.0/)
- [Semantic Versioning](https://semver.org/lang/ko/)
- [GitHub Actions 문서](https://docs.github.com/en/actions)

## 💡 팁

1. **일관된 커밋 메시지**: 팀 전체가 Conventional Commits 형식을 따르면 릴리스 노트 품질이 향상됩니다.

2. **CHANGELOG.md와 병행**: 자동 생성 + 수동 편집을 함께 사용하여 최상의 결과를 얻으세요.

3. **정기적인 릴리스**: 작은 변경사항도 자주 릴리스하면 사용자가 업데이트를 쉽게 파악할 수 있습니다.

4. **Breaking Changes 표시**: 호환성이 깨지는 변경이 있을 땐 커밋 메시지에 `BREAKING CHANGE:` 추가:
   ```bash
   git commit -m "feat!: API 인증 방식 변경
   
   BREAKING CHANGE: 기존 API Key 형식이 더 이상 지원되지 않습니다.
   새로운 형식으로 재발급이 필요합니다."
   ```

5. **로컬 테스트**: 배포 전 `generate_release_notes.ps1`로 미리 확인하세요!
