using Binance.Net.Enums;
using Binance.Net.Interfaces;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using TradingBot.Services;

namespace TradingBot
{
    /// <summary>
    /// [Time-Out Probability Entry] 엔진
    /// ──────────────────────────────────────────────────────────────────────
    /// 60분간 진입이 없을 때, DB에 축적된 과거 패턴과 코사인 유사도(Cosine Similarity)를
    /// 비교하여 승률이 70% 이상인 메이저 코인에 자동 '확률적 진입'을 수행합니다.
    ///
    /// 알고리즘 요약:
    ///   1. 현재 15분봉 최근 20개 OHLC → [RSI, BB위치, MACD히스토, 거래량비율] 피처벡터 추출
    ///   2. DB TradePatternSnapshots (라벨링 완료)에서 동일 심볼/방향 최대 500개 조회
    ///   3. 코사인 유사도 ≥ 0.65 인 패턴 필터 → 상위 1,000개 선정
    ///   4. PnLPercent ≥ +3% 비율(승률) 계산
    ///   5. 승률 ≥ 70% → ETH/XRP/SOL 중 최초 조건 충족 종목 40% 비중 진입
    ///
    /// 안전장치: ATR 하이브리드 스톱은 ExecuteAutoOrder 내 기본값으로 자동 적용됨.
    /// </summary>
    public class TimeOutProbabilityEngine
    {
        // ──────────────────────────────────────────────────────────────────
        // 설정 상수
        // ──────────────────────────────────────────────────────────────────

        /// <summary>진입 결정 승률 임계값 (70%)</summary>
        public const double WinProbabilityThreshold = 0.70;

        /// <summary>유사 패턴 필터 최소 코사인 유사도 (65%)</summary>
        public const double MinCosineSimilarity = 0.65;

        /// <summary>수익(승) 판정 기준 PnL% (+3%)</summary>
        public const double WinPnLThresholdPct = 3.0;

        /// <summary>패턴 매칭용 최근 캔들 수</summary>
        private const int CandleLookback = 34;  // RSI 14기간 + 20개 = 34

        /// <summary>DB 조회 최대 행수</summary>
        private const int MaxHistoryRows = 500;

        /// <summary>DB 조회 기간(일)</summary>
        private const int LookbackDays = 180;

        /// <summary>유사도 필터 통과 후 최대 사용 행수</summary>
        private const int MaxSimilarPatterns = 1000;

        /// <summary>유사도 탐색 최소 샘플 수 (이하면 데이터 부족으로 스킵)</summary>
        private const int MinSampleThreshold = 5;

        /// <summary>확률 진입 비중 (기본 비중 대비 배율 — 40%)</summary>
        public const decimal EntryManualSizeMultiplier = 0.40m;

        /// <summary>MACD 히스토 정규화 스케일 (±0.003 → [-1,1])</summary>
        private const double MacdNormScale = 0.003;

        /// <summary>거래량 비율 정규화 최대값 (5배)</summary>
        private const double VolRatioMaxScale = 5.0;

        /// <summary>스캔 대상 메이저 코인 (ETH → XRP → SOL 순)</summary>
        public static readonly string[] MajorCoins = { "ETHUSDT", "XRPUSDT", "SOLUSDT" };

        // ──────────────────────────────────────────────────────────────────
        // 의존 서비스
        // ──────────────────────────────────────────────────────────────────
        private readonly IExchangeService _exchangeService;
        private readonly DbManager _dbManager;
        private readonly Action<string>? _onLog;

        // ──────────────────────────────────────────────────────────────────
        // UI 상태 (스레드 안전 읽기)
        // ──────────────────────────────────────────────────────────────────
        private volatile string _scanStatus  = "STANDBY";
        private volatile string _matchSymbol = "—";
        private double _matchWinProbability  = 0;
        private volatile bool _activated     = false;

        /// <summary>현재 스캔 상태 메시지</summary>
        public string ScanStatus          => _scanStatus;
        /// <summary>가장 최근 매칭 심볼</summary>
        public string MatchSymbol         => _matchSymbol;
        /// <summary>가장 최근 매칭 승률 (0 ~ 1)</summary>
        public double MatchWinProbability => _matchWinProbability;
        /// <summary>70% 임계 돌파 여부 (UI Glow 트리거)</summary>
        public bool   Activated           => _activated;

        // ──────────────────────────────────────────────────────────────────
        // 이벤트
        // ──────────────────────────────────────────────────────────────────

        /// <summary>
        /// UI 갱신 이벤트: (scanStatus, matchSymbol, winRate 0~1, activated)
        /// </summary>
        public event Action<string, string, double, bool>? OnUIUpdated;

        /// <summary>
        /// 진입 요청 이벤트 (TradingEngine.ExecuteAutoOrder 콜백으로 연결됨):
        /// (symbol, direction, signalSource, manualSizeMultiplier, token) → Task
        /// </summary>
        public event Func<string, string, string, decimal, CancellationToken, Task>? OnEntryRequested;

        // ──────────────────────────────────────────────────────────────────
        // 생성자
        // ──────────────────────────────────────────────────────────────────
        public TimeOutProbabilityEngine(
            IExchangeService exchangeService,
            DbManager dbManager,
            Action<string>? onLog = null)
        {
            _exchangeService = exchangeService ?? throw new ArgumentNullException(nameof(exchangeService));
            _dbManager       = dbManager       ?? throw new ArgumentNullException(nameof(dbManager));
            _onLog           = onLog;
        }

        // ──────────────────────────────────────────────────────────────────
        // Public: 60분 공백 시 메이저 코인 확률 스캔
        // ──────────────────────────────────────────────────────────────────

        /// <summary>
        /// 60분 진입 공백 감지 후 호출.
        /// ETH → XRP → SOL 순으로 승률 70%+ 종목을 탐색, 최초 조건 충족 시 진입 요청.
        /// </summary>
        public async Task RunTimeOutScanAsync(CancellationToken token)
        {
            try
            {
                Log("⏳ [TimeOut-Prob] 60분 공백 감지 — 메이저 코인 과거 패턴 매칭 엔진 가동...");
                UpdateUI("⏳ IDLE 60M — DEEP SCANNING PAST 180D DATA...", "—", 0, false);

                foreach (var symbol in MajorCoins)
                {
                    if (token.IsCancellationRequested) break;

                    try
                    {
                        UpdateUI($"🔍 {symbol} 패턴 분석 중...", symbol, 0, false);

                        // 15분봉 캔들 조회 (RSI 계산을 위해 34개)
                        var klines = (await _exchangeService.GetKlinesAsync(
                            symbol, KlineInterval.FifteenMinutes, CandleLookback, token))?.ToList();

                        if (klines == null || klines.Count < 20)
                        {
                            Log($"⚠️ [TimeOut-Prob] {symbol} 15분봉 캔들 부족 ({klines?.Count ?? 0}개) — 스킵");
                            continue;
                        }

                        // 현재 피처 벡터 추출
                        double[] currentVec = ExtractFeatureVector(klines);

                        // LONG / SHORT 중 더 높은 승률 방향 선택
                        double bestWinRate   = 0;
                        string bestDirection = "LONG";
                        string bestDetail    = "";

                        foreach (var dir in new[] { "LONG", "SHORT" })
                        {
                            if (token.IsCancellationRequested) break;

                            var (winRate, detail) = await AnalyzeSimilarPatternAsync(
                                symbol, dir, currentVec, token);

                            Log($"   📊 {symbol} [{dir}] 과거 유사 패턴 승률: {winRate:P1} ({detail})");

                            if (winRate > bestWinRate)
                            {
                                bestWinRate   = winRate;
                                bestDirection = dir;
                                bestDetail    = detail;
                            }
                        }

                        // UI 업데이트 (승률 게이지 표시)
                        UpdateUI(
                            $"⚡ {symbol} {bestDirection} → 승률 {bestWinRate:P1}",
                            symbol,
                            bestWinRate,
                            bestWinRate >= WinProbabilityThreshold);

                        if (bestWinRate >= WinProbabilityThreshold)
                        {
                            string entryMsg =
                                $"🎯 [TimeOut 확률 진입] {symbol} [{bestDirection}] | " +
                                $"승률 {bestWinRate:P1} | {bestDetail} | 비중 {EntryManualSizeMultiplier * 100:F0}%";

                            Log(entryMsg);
                            MainWindow.Instance?.AddAlert(entryMsg);

                            // TradingEngine.ExecuteAutoOrder 콜백으로 진입 요청
                            if (OnEntryRequested != null)
                            {
                                await OnEntryRequested.Invoke(
                                    symbol,
                                    bestDirection,
                                    "TIMEOUT_PROB_ENTRY",
                                    EntryManualSizeMultiplier,
                                    token);
                            }

                            UpdateUI(
                                $"✅ {symbol} 진입 완료! 승률 {bestWinRate:P1} — ENTRY FIRED",
                                symbol,
                                bestWinRate,
                                true);

                            // 1개 진입 후 스캔 즉시 종료
                            return;
                        }

                        await Task.Delay(400, token);
                    }
                    catch (OperationCanceledException) { break; }
                    catch (Exception ex)
                    {
                        Log($"⚠️ [TimeOut-Prob] {symbol} 스캔 오류: {ex.Message}");
                    }
                }

                Log("⛔ [TimeOut-Prob] 메이저 3종 모두 승률 70% 미달 — 자동 진입 없음");
                UpdateUI("SCAN COMPLETE — NO ENTRY (승률 70% 미달)", "—", 0, false);
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                Log($"⚠️ [TimeOut-Prob] 전체 스캔 오류: {ex.Message}");
                UpdateUI("SCAN ERROR", "—", 0, false);
            }
        }

        // ──────────────────────────────────────────────────────────────────
        // 내부: 과거 DB 패턴과 코사인 유사도 기반 승률 계산
        // ──────────────────────────────────────────────────────────────────

        private async Task<(double winRate, string detail)> AnalyzeSimilarPatternAsync(
            string symbol, string direction, double[] currentVec, CancellationToken token)
        {
            try
            {
                // DB에서 라벨링된 과거 스냅샷 조회 (최근 180일, 최대 500개)
                var snapshots = await _dbManager.GetLabeledTradePatternSnapshotsAsync(
                    symbol, direction,
                    lookbackDays: LookbackDays,
                    maxRows: MaxHistoryRows);

                if (snapshots == null || snapshots.Count < MinSampleThreshold)
                    return (0, $"데이터 부족 ({snapshots?.Count ?? 0}개, 최소 {MinSampleThreshold})");

                // 코사인 유사도 계산 후 임계 이상 필터
                var similar = new List<(double Sim, decimal PnLPct)>(snapshots.Count);

                foreach (var snap in snapshots)
                {
                    if (!snap.Label.HasValue || !snap.PnLPercent.HasValue) continue;

                    double[] histVec = ExtractFeatureVectorFromSnapshot(snap);
                    double   cosine  = CosineSimilarity(currentVec, histVec);

                    if (cosine >= MinCosineSimilarity)
                        similar.Add((cosine, snap.PnLPercent.Value));
                }

                if (similar.Count < MinSampleThreshold)
                    return (0, $"유사 패턴 부족 ({similar.Count}개, 유사도 ≥ {MinCosineSimilarity:P0} 기준)");

                // 유사도 내림차순 상위 MaxSimilarPatterns개 선정
                var top = similar
                    .OrderByDescending(s => s.Sim)
                    .Take(MaxSimilarPatterns)
                    .ToList();

                // 승 = PnLPercent >= +3%
                int wins     = top.Count(s => (double)s.PnLPct >= WinPnLThresholdPct);
                double winRate  = (double)wins / top.Count;
                double avgSim   = top.Average(s => s.Sim);

                return (winRate, $"유사패턴 {top.Count}건, 승 {wins}건, 평균유사도 {avgSim:F3}");
            }
            catch (Exception ex)
            {
                Log($"⚠️ [TimeOut-Prob] {symbol}/{direction} 패턴 분석 오류: {ex.Message}");
                return (0, $"분석 오류: {ex.Message}");
            }
        }

        // ──────────────────────────────────────────────────────────────────
        // 내부: 현재 캔들 리스트 → 피처 벡터 [4차원]
        // ──────────────────────────────────────────────────────────────────
        // 벡터 구성:
        //   [0] RSI/100         : 0~1
        //   [1] BB 위치          : 0(하단)~1(상단)
        //   [2] MACD 히스토 정규화 : -1~1
        //   [3] 거래량 비율 정규화 : 0~1 (5배 기준)
        // ──────────────────────────────────────────────────────────────────
        private static double[] ExtractFeatureVector(List<IBinanceKline> klines)
        {
            try
            {
                double rsi        = IndicatorCalculator.CalculateRSI(klines, 14);
                var    bb         = IndicatorCalculator.CalculateBB(klines, 20, 2.0);
                var    (_, _, h)  = IndicatorCalculator.CalculateMACD(klines);
                double lastClose  = (double)klines.Last().ClosePrice;

                // BB 내 상대 위치 [0,1]
                double bbRange = bb.Upper - bb.Lower;
                double bbPos   = bbRange > 0
                    ? Math.Clamp((lastClose - bb.Lower) / bbRange, 0, 1)
                    : 0.5;

                // 거래량 비율 (현재 봉 / 최근 20봉 평균)
                double avgVol  = klines.Count >= 20
                    ? (double)klines.TakeLast(20).Average(k => k.Volume)
                    : (double)klines.Average(k => k.Volume);
                double volRat  = avgVol > 0
                    ? Math.Clamp((double)klines.Last().Volume / avgVol, 0, VolRatioMaxScale)
                    : 1.0;

                // MACD 히스토 정규화
                double macdNorm = Math.Clamp(h / MacdNormScale, -1, 1);

                return new double[]
                {
                    Math.Clamp(rsi / 100.0,              0, 1),  // [0] RSI
                    bbPos,                                          // [1] BB Position
                    macdNorm,                                       // [2] MACD Hist
                    Math.Clamp(volRat / VolRatioMaxScale, 0, 1),  // [3] Volume Ratio
                };
            }
            catch
            {
                // 지표 계산 실패 시 중립값 반환
                return new double[] { 0.5, 0.5, 0.0, 0.2 };
            }
        }

        // ──────────────────────────────────────────────────────────────────
        // 내부: 과거 스냅샷 레코드 → 피처 벡터 [4차원]
        // ──────────────────────────────────────────────────────────────────
        private static double[] ExtractFeatureVectorFromSnapshot(TradePatternSnapshotRecord snap)
        {
            double rsi     = Math.Clamp(snap.Rsi / 100.0, 0, 1);
            double bbPos   = Math.Clamp(snap.BbPosition, 0, 1);
            double macd    = Math.Clamp(snap.MacdHist / MacdNormScale, -1, 1);
            double volRat  = Math.Clamp(snap.VolumeRatio / VolRatioMaxScale, 0, 1);
            return new double[] { rsi, bbPos, macd, volRat };
        }

        // ──────────────────────────────────────────────────────────────────
        // 내부: 코사인 유사도
        // ──────────────────────────────────────────────────────────────────
        private static double CosineSimilarity(double[] a, double[] b)
        {
            if (a.Length != b.Length) return 0;
            double dot = 0, normA = 0, normB = 0;
            for (int i = 0; i < a.Length; i++)
            {
                dot   += a[i] * b[i];
                normA += a[i] * a[i];
                normB += b[i] * b[i];
            }
            return (normA > 0 && normB > 0)
                ? dot / (Math.Sqrt(normA) * Math.Sqrt(normB))
                : 0;
        }

        // ──────────────────────────────────────────────────────────────────
        // 내부 헬퍼
        // ──────────────────────────────────────────────────────────────────
        private void UpdateUI(string status, string symbol, double winRate, bool activated)
        {
            _scanStatus          = status;
            _matchSymbol         = symbol;
            _matchWinProbability = winRate;
            _activated           = activated;
            OnUIUpdated?.Invoke(status, symbol, winRate, activated);
        }

        private void Log(string msg) => _onLog?.Invoke(msg);
    }
}
