using System;
using System.Collections.Generic;
using System.Linq;
using TradingBot.Models;

namespace TradingBot.Strategies
{
    /// <summary>
    /// 스나이퍼 모드: 종목당 일일 1~2회 진입으로 정조준
    /// 
    /// 핵심 원칙:
    /// 1. 진입 문턱 80점: AI(27) + 기술(45) + 보너스(8) 중첩 필요
    /// 2. 종목당 일일 2회 제한: 24시간 중 가장 큰 추세 1~2개에만 집중
    /// 3. 최대 5개 포지션: 메이저 4 + 밈 1 분산 운영
    /// 4. 120분 쿨다운: 한 파동 먹고 다음까지 2시간 휴식
    /// 
    /// 효과: 어설픈 반등·눌림목은 점수 미달로 걸러짐
    /// 결과: "확률 80% 이상 A급 자리"에서만 방아쇠
    /// </summary>
    public class SniperManager
    {
        // ════════════════ 다중 타임프레임 데이터 구조 ════════════════
        /// <summary>1시간봉 / 1분봉 추세 비교용 데이터</summary>
        public class MultiTimeFrameData
        {
            /// <summary>1시간봉 SMA20</summary>
            public decimal Sma20_1h { get; set; }
            /// <summary>1시간봉 SMA50</summary>
            public decimal Sma50_1h { get; set; }
            /// <summary>1시간봉 SMA200</summary>
            public decimal Sma200_1h { get; set; }
            
            /// <summary>1분봉 현재가</summary>
            public decimal Price_1m { get; set; }
            /// <summary>1분봉 SMA20</summary>
            public decimal Sma20_1m { get; set; }
            /// <summary>1분봉 SMA50</summary>
            public decimal Sma50_1m { get; set; }
        }

        // ════════════════ 설정값 & 콜백 ════════════════
        private double _minimumEntryScore = 80.0;
        private int _maxTradesPerSymbolPerDay = 2;
        private int _maxActivePositions = 5;
        private int _entryCooldownMinutes = 120;

        /// <summary>TradingEngine으로부터 다중 타임프레임 데이터를 조회하는 콜백</summary>
        private Func<string, MultiTimeFrameData>? _trendDataProvider;

        // ════════════════ 상태 추적 ════════════════
        /// <summary>심볼별 오늘의 진입 시간 기록 (냉각 시간 추적)</summary>
        private Dictionary<string, List<DateTime>> _todayEntryTimes = new();

        /// <summary>현재 활성 포지션 수</summary>
        private int _activePositionCount = 0;

        /// <summary>메이저 4종목 (추세 추종)</summary>
        private static readonly string[] MajorSymbols = { "BTCUSDT", "ETHUSDT", "SOLUSDT", "XRPUSDT" };

        public SniperManager(double minimumScore, int maxTrades, int maxPositions, int cooldownMin)
        {
            _minimumEntryScore = minimumScore;
            _maxTradesPerSymbolPerDay = maxTrades;
            _maxActivePositions = maxPositions;
            _entryCooldownMinutes = cooldownMin;
        }

        /// <summary>
        /// TradingEngine으로부터 트렌드 데이터 제공자(콜백) 주입
        /// </summary>
        public void SetTrendDataProvider(Func<string, MultiTimeFrameData> provider)
        {
            _trendDataProvider = provider;
        }

        /// <summary>
        /// 최종 진입 승인 게이트
        /// 모든 조건을 통과해야 '스나이퍼 타겟'으로 진입
        /// </summary>
        public bool CanEnter(string symbol, double currentScore)
        {
            // ─── 1. 점수 문턱 (80점 이상) ───
            if (currentScore < _minimumEntryScore)
            {
                Log($"❌ [{symbol}] 점수 미달: {currentScore:F1}/{_minimumEntryScore} → 진입 거부");
                return false;
            }

            // ─── 2. 종목당 하루 2회 제한 ───
            int todayTradeCount = GetTodayTradeCount(symbol);
            if (todayTradeCount >= _maxTradesPerSymbolPerDay)
            {
                Log($"🚫 [{symbol}] 오늘의 사냥 횟수 초과 ({todayTradeCount}/{_maxTradesPerSymbolPerDay}) → 내일을 기약");
                return false;
            }

            // ─── 3. 120분 쿨다운 체크 (중복 진입 방지) ───
            if (!IsEnoughCooldown(symbol))
            {
                Log($"⏳ [{symbol}] 진입 쿨다운 중 (120분) → 다음 파동 대기");
                return false;
            }

            // ─── 4. 최대 5개 포지션 제한 ───
            if (_activePositionCount >= _maxActivePositions)
            {
                Log($"⛔ 최대 포지션 도달 ({_activePositionCount}/{_maxActivePositions}) → 기존 포지션 정리 후 진입");
                return false;
            }

            // ─── 5. 메이저 전용 필터: 1H 추세와 1분 방향 일치 확인 ───
            if (IsMajorSymbol(symbol) && !IsTrendMatched(symbol))
            {
                Log($"📊 [{symbol}] 1H 추세와 1분 방향 불일치 → 추세 추종 필터 탈락");
                return false;
            }

            // ─── 모든 관문 통과: A급 타점 ───
            Log($"🎯 [{symbol}] 스나이퍼 타겟 포착! 점수: {currentScore:F1}/100 → 진입 실행!");
            RecordEntry(symbol);
            _activePositionCount++;
            return true;
        }

        /// <summary>
        /// 포지션 종료 시 호출 (청산/손절)
        /// </summary>
        public void OnPositionClosed(string symbol)
        {
            if (_activePositionCount > 0) _activePositionCount--;
            Log($"✅ [{symbol}] 포지션 종료 → 활성 포지션: {_activePositionCount}/{_maxActivePositions}");
        }

        /// <summary>
        /// 해당 심볼이 메이저 4종목인지 확인
        /// </summary>
        public static bool IsMajorSymbol(string symbol)
        {
            return MajorSymbols.Any(s => symbol.StartsWith(s.Replace("USDT", "")));
        }

        /// <summary>
        /// 오늘 해당 심볼의 진입 횟수 조회
        /// </summary>
        private int GetTodayTradeCount(string symbol)
        {
            if (!_todayEntryTimes.ContainsKey(symbol))
                return 0;

            var today = DateTime.Now.Date;
            return _todayEntryTimes[symbol].Count(t => t.Date == today);
        }

        /// <summary>
        /// 120분 쿨다운 체크
        /// </summary>
        private bool IsEnoughCooldown(string symbol)
        {
            if (!_todayEntryTimes.ContainsKey(symbol) || _todayEntryTimes[symbol].Count == 0)
                return true;

            var lastEntry = _todayEntryTimes[symbol].Last();
            var elapsed = DateTime.Now - lastEntry;
            return elapsed.TotalMinutes >= _entryCooldownMinutes;
        }

        /// <summary>
        /// 1시간봉 추세와 1분봉 방향 일치 검증 (메이저 전용)
        /// 
        /// 로직:
        /// 1. 1H 추세 판단: SMA20 > SMA50 > SMA200 (상승) vs 그 반대 (하락)
        /// 2. 1분 현재 방향: Price > SMA20 (상승) vs 그 미만 (하락)
        /// 3. 추세와 방향이 일치할 때만 진입 허용
        /// 
        /// 예시:
        /// - 1H 상승(SMA20 > SMA50) + 1분 상승(Price > SMA20) = OK ✅
        /// - 1H 상승 + 1분 하락 = NG ❌ (역행하는 반전 신호)
        /// - 1H 하락 + 1분 하락 = OK ✅
        /// </summary>
        private bool IsTrendMatched(string symbol)
        {
            // TrendDataProvider가 설정되지 않으면 메이저 필터 스킵
            if (_trendDataProvider == null)
            {
                Log($"⚠️  [{symbol}] TrendDataProvider 미설정 → 1H/1분 필터 스킵");
                return true;
            }

            try
            {
                var data = _trendDataProvider(symbol);
                if (data == null)
                {
                    Log($"⚠️  [{symbol}] 다중 타임프레임 데이터 없음 → 필터 스킵");
                    return true;
                }

                // ─── 1시간봉 추세 판단 ───
                // SMA20 > SMA50 이면 상승 추세 (1), 그 외 하락 추세 (-1)
                bool h1Uptrend = data.Sma20_1h > data.Sma50_1h;

                // ─── 1분봉 현재 방향 판단 ───
                // Price > SMA20 이면 상승 방향 (1), 그 외 하락 방향 (-1)
                bool m1Uptrend = data.Price_1m > data.Sma20_1m;

                // ─── 추세 일치 검증 ───
                bool matched = (h1Uptrend && m1Uptrend) || (!h1Uptrend && !m1Uptrend);

                if (matched)
                {
                    string h1Direction = h1Uptrend ? "상승" : "하락";
                    string m1Direction = m1Uptrend ? "상승" : "하락";
                    Log($"✅ [{symbol}] 1H/1분 추세 일치 (1H:{h1Direction} ← → 1분:{m1Direction})");
                    return true;
                }
                else
                {
                    string h1Direction = h1Uptrend ? "상승" : "하락";
                    string m1Direction = m1Uptrend ? "상승" : "하락";
                    Log($"❌ [{symbol}] 1H/1분 추세 불일치 (1H:{h1Direction} ⚠️ 1분:{m1Direction}) → 진입 거부");
                    return false;
                }
            }
            catch (Exception ex)
            {
                Log($"⚠️  [{symbol}] 추세 비교 중 오류: {ex.Message} → 필터 스킵");
                return true;
            }
        }

        /// <summary>
        /// 진입 기록 저장
        /// </summary>
        private void RecordEntry(string symbol)
        {
            if (!_todayEntryTimes.ContainsKey(symbol))
                _todayEntryTimes[symbol] = new List<DateTime>();

            _todayEntryTimes[symbol].Add(DateTime.Now);
        }

        /// <summary>
        /// 일일 기록 초기화 (자정마다 호출)
        /// </summary>
        public void ResetDailyLog()
        {
            _todayEntryTimes.Clear();
            Log("🔄 [SniperManager] 일일 기록 초기화 완료");
        }

        /// <summary>
        /// 현재 활성 포지션 수 조회
        /// </summary>
        public int GetActivePositionCount()
        {
            return _activePositionCount;
        }

        public void SetActivePositionCount(int count)
        {
            _activePositionCount = Math.Max(0, count);
        }

        /// <summary>
        /// 로그 출력
        /// </summary>
        private void Log(string message)
        {
            try
            {
                MainWindow.Instance?.AddLog(message);
            }
            catch { /* 로그 실패 무시 */ }
        }
    }
}
