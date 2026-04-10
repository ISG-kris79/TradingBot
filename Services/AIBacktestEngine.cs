using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Binance.Net.Clients;
using Binance.Net.Enums;
using Binance.Net.Interfaces;
using Microsoft.ML;
using Microsoft.ML.Data;

namespace TradingBot.Services
{
    /// <summary>
    /// [AI 백테스트 엔진]
    ///
    /// 흐름:
    ///   1. 5분봉 데이터 수집 (메이저 + 펌프 8종, 6개월)
    ///   2. 각 캔들에 특성(Feature) 부착 (RSI, MACD, BB, ATR, 볼륨, EMA정렬, 캔들패턴 등 25개)
    ///   3. "진입했으면 수익이었는지" 자동 라벨링 (미래 데이터 참조)
    ///   4. 데이터의 앞 70%로 LightGBM 학습, 뒤 30%로 검증
    ///   5. 승리 패턴 스냅샷 저장 (15차원 벡터)
    ///   6. 검증 구간에서 ML예측 + 패턴매칭 이중확인 백테스트
    ///   7. 결과 출력 + 자동 반복 최적화
    /// </summary>
    public class AIBacktestEngine
    {
        private const decimal LEVERAGE       = 20m;
        private const decimal MARGIN_PERCENT = 0.12m;
        private const decimal FEE_RATE       = 0.0004m;
        private const int     COOLDOWN_BARS  = 12;   // 5분봉 12개 = 1시간

        // 라벨링 파라미터: '빠른 파동' 학습용
        // 단순 '가격 상승'이 아닌 '진입 후 15분 내 +1% 도달 여부'를 학습
        // → AI가 느린 추세가 아닌 빠른 모멘텀 파동을 찾게 됨
        private const int     LABEL_LOOKAHEAD  = 15;  // 5분 × 15 = 75분 (빠른 파동 감지)
        private const double  LABEL_TP_PCT     = 1.0; // 가격 +1.0% = ROE +20% (빠른 달성)
        private const double  LABEL_SL_PCT     = 0.5; // 가격 -0.5% = ROE -10% (타이트 손절)

        // ═══ ML 특성 정의 (40개: 5분봉 25 + 15분봉 5 + 1시간봉 5 + 4시간봉 5) ═══
        public class EntryFeature
        {
            // ── 5분봉 피처 (25개) ──
            public float RSI { get; set; }
            public float RSI_Slope { get; set; }
            public float MACD_Hist { get; set; }
            public float MACD_Rising { get; set; }
            public float BB_Position { get; set; }
            public float BB_Width_Pct { get; set; }
            public float ATR_Pct { get; set; }
            public float Volume_Ratio { get; set; }
            public float Volume_Spike { get; set; }
            public float EMA9_Slope { get; set; }
            public float EMA21_Slope { get; set; }
            public float EMA_Alignment { get; set; }
            public float Price_To_EMA21 { get; set; }
            public float Price_To_EMA50 { get; set; }
            public float Candle_Body_Ratio { get; set; }
            public float Candle_Direction { get; set; }
            public float Upper_Shadow { get; set; }
            public float Lower_Shadow { get; set; }
            public float Price_Change_3Bar { get; set; }
            public float Price_Change_12Bar { get; set; }
            public float High_Break { get; set; }
            public float Low_Break { get; set; }
            public float Momentum_Score { get; set; }
            public float HourOfDay { get; set; }
            public float DayOfWeek { get; set; }

            // ── 15분봉 핵심 4대 지표 (18개) ──
            // [1] 볼린저 밴드
            public float M15_BB_Position { get; set; }
            public float M15_BB_Width { get; set; }          // 밴드폭 %
            public float M15_BB_Squeeze { get; set; }        // 스퀴즈 강도
            public float M15_BB_Breakout { get; set; }       // 돌파 방향
            // [2] RSI
            public float M15_RSI { get; set; }
            public float M15_RSI_Slope { get; set; }         // 기울기 (반등 강도)
            public float M15_RSI_VTurn { get; set; }         // V-Turn 감지
            public float M15_RSI_Divergence { get; set; }    // 다이버전스
            // [3] EMA 20/60
            public float M15_EMA_Trend { get; set; }
            public float M15_EMA_Cross { get; set; }         // 골든/데드 크로스
            public float M15_EMA_Gap { get; set; }           // 이격도 (에너지 잔량)
            public float M15_Price_Above_EMA20 { get; set; } // 가격 EMA20 위 지지
            // [4] 피보나치
            public float M15_Fib_Position { get; set; }      // 피보 위치 (0~1)
            public float M15_Fib_AtGoldenZone { get; set; }  // 0.618~0.786 존 내
            public float M15_Fib_Bounce { get; set; }        // 골든존 반등
            //
            public float M15_MACD_Rising { get; set; }
            public float M15_Volume_Ratio { get; set; }

            // ── 선행성·정규화 강화 피처 (5개) ──
            public float VWAP_Position { get; set; }      // (Price-VWAP)/Price
            public float Price_To_EMA200 { get; set; }    // (Price-EMA200)/Price
            public float RSI_Divergence { get; set; }     // 5분봉 다이버전스
            public float OBV_Slope { get; set; }          // OBV 변화율
            public float Pivot_Position { get; set; }     // (Price-Pivot)/(R1-S1)

            // ── 추세 강도·국면 분류 (4개) ──
            public float ADX { get; set; }                  // ADX 14 / 100
            public float Ichimoku_Above_Kijun { get; set; } // 가격 > 기준선(26)
            public float FundingRate { get; set; }          // 펀딩비 (정규화)
            public float SymbolCategory { get; set; }       // 종목 카테고리

            // ── 1시간봉 피처 (5개) ── 실제 1시간봉 데이터에서 추출
            public float H1_RSI { get; set; }
            public float H1_MACD_Rising { get; set; }
            public float H1_BB_Position { get; set; }
            public float H1_EMA_Trend { get; set; }      // 1H EMA20>EMA50 = +1
            public float H1_Volume_Ratio { get; set; }

            // ── 4시간봉 피처 (5개) ── 실제 4시간봉 데이터에서 추출
            public float H4_RSI { get; set; }
            public float H4_MACD_Rising { get; set; }
            public float H4_BB_Position { get; set; }
            public float H4_EMA_Trend { get; set; }      // 4H EMA20>EMA50 = +1 (장기 추세)
            public float H4_Volume_Ratio { get; set; }

            // 라벨
            public bool Label { get; set; }
        }

        public class EntryPrediction
        {
            [ColumnName("PredictedLabel")] public bool Predicted { get; set; }
            [ColumnName("Probability")] public float Probability { get; set; }
            [ColumnName("Score")] public float Score { get; set; }
        }

        // ═══ 승리 패턴 스냅샷 (유사도 매칭용) ═══
        public class PatternSnapshot
        {
            public double[] Vector { get; set; } = Array.Empty<double>(); // 40차원
            public string Direction { get; set; } = "";
            public string Symbol { get; set; } = "";
            public DateTime Time { get; set; }
            public decimal PnlPct { get; set; }
            public bool IsWin { get; set; }
        }

        // ═══ 내부 캔들 ═══
        private class Bar
        {
            public DateTime Time;
            public double O, H, L, C, Vol;
            // 지표
            public double RSI, RSIPrev, EMA9, EMA21, EMA50, EMA200, EMA9Prev, EMA21Prev;
            public double MACD, MACDPrev, ATR, BBUpper, BBLower, BBMid;
            public double VolMA, VolRatio;
            // 선행성·정규화 강화
            public double VWAP;        // 거래량 가중 평균 가격
            public double OBV;         // On-Balance Volume
            public double OBVPrev;     // 이전 OBV (기울기 계산용)
            // 추세 강도·국면
            public double ADX;         // Average Directional Index
            public double KijunSen;    // 이치모쿠 기준선 (26봉)
        }

        // ═══ 결과 ═══
        public class AIBacktestResult
        {
            public decimal InitialBalance { get; set; }
            public decimal FinalBalance { get; set; }
            public decimal TotalPnl => FinalBalance - InitialBalance;
            public decimal TotalPnlPct => InitialBalance > 0 ? TotalPnl / InitialBalance * 100m : 0;
            public int TotalTrades { get; set; }
            public int Wins { get; set; }
            public decimal WinRate => TotalTrades > 0 ? (decimal)Wins / TotalTrades * 100m : 0;
            public decimal AvgDailyPct { get; set; }
            public decimal MaxDrawdown { get; set; }
            public decimal ProfitFactor { get; set; }
            public int TrainSamples { get; set; }
            public int WinPatterns { get; set; }
            public double ModelAccuracy { get; set; }
            public double ModelAUC { get; set; }
            public string Details { get; set; } = "";
            public Dictionary<string, decimal> ByMonth { get; set; } = new();
            public Dictionary<string, (int t, int w, decimal pnl)> BySymbol { get; set; } = new();
        }

        // ═══ 메인 실행 ═══
        public async Task<AIBacktestResult> RunAsync(
            decimal initialBalance = 2500m,
            int months = 36,
            Action<string>? onLog = null)
        {
            var symbols = new[] { "BTCUSDT", "ETHUSDT", "SOLUSDT", "XRPUSDT",
                                  "DOGEUSDT", "PEPEUSDT", "WIFUSDT", "BONKUSDT" };
            var endDate   = DateTime.UtcNow.Date;
            var startDate = endDate.AddMonths(-months);

            onLog?.Invoke("╔══════════════════════════════════════════════════════════════════╗");
            onLog?.Invoke("║     AI 학습 백테스트 엔진 (ML + 패턴 매칭)                      ║");
            onLog?.Invoke("╠══════════════════════════════════════════════════════════════════╣");
            onLog?.Invoke($"║  기간: {startDate:yyyy-MM-dd} ~ {endDate:yyyy-MM-dd} ({months}개월)");
            onLog?.Invoke($"║  심볼: {string.Join(", ", symbols)}");
            onLog?.Invoke($"║  라벨링: {LABEL_LOOKAHEAD}봉 내 TP +{LABEL_TP_PCT}% / SL -{LABEL_SL_PCT}%");
            onLog?.Invoke("╚══════════════════════════════════════════════════════════════════╝");

            // ═══ Step 1: 다중 타임프레임 데이터 수집 (캐시 지원) ═══
            onLog?.Invoke("\n[Step 1/5] 다중 타임프레임 데이터 수집 (5m + 15m + 1H + 4H)...");
            onLog?.Invoke($"  캐시 폴더: {CacheDir}");

            using var client = new BinanceRestClient();
            var allBars5m  = new Dictionary<string, List<Bar>>();
            var allBars15m = new Dictionary<string, List<Bar>>();
            var allBars1h  = new Dictionary<string, List<Bar>>();
            var allBars4h  = new Dictionary<string, List<Bar>>();

            var intervals = new (KlineInterval interval, string label, Dictionary<string, List<Bar>> dict)[]
            {
                (KlineInterval.FiveMinutes,     "5m",  allBars5m),
                (KlineInterval.FifteenMinutes,  "15m", allBars15m),
                (KlineInterval.OneHour,         "1H",  allBars1h),
                (KlineInterval.FourHour,        "4H",  allBars4h),
            };

            for (int si = 0; si < symbols.Length; si++)
            {
                var sym = symbols[si];
                onLog?.Invoke($"\n  [{si+1}/{symbols.Length}] [{sym}]");

                bool symbolOk = true;
                foreach (var (interval, label, dict) in intervals)
                {
                    // 캐시에서 로드 + 신규분만 API 수집
                    var cached = LoadCache(sym, label);
                    DateTime fetchFrom = cached.Count > 0
                        ? cached.Max(c => c.Time).AddMinutes(1)
                        : startDate;

                    int newCount = 0;
                    if (fetchFrom < endDate)
                    {
                        onLog?.Invoke($"    {label}: 캐시 {cached.Count:N0}개, {fetchFrom:yyyy-MM-dd HH:mm} 이후 수집...");
                        var newKlines = await FetchKlinesAsync(client, sym, interval, fetchFrom, endDate, onLog);
                        if (newKlines.Count > 0)
                        {
                            var newBars = newKlines.Select(k => new Bar
                            {
                                Time = k.OpenTime, O = (double)k.OpenPrice, H = (double)k.HighPrice,
                                L = (double)k.LowPrice, C = (double)k.ClosePrice, Vol = (double)k.Volume
                            }).ToList();

                            // 중복 제거 후 합치기
                            var lastCachedTime = cached.Count > 0 ? cached[^1].Time : DateTime.MinValue;
                            var deduped = newBars.Where(b => b.Time > lastCachedTime).ToList();
                            cached.AddRange(deduped);
                            newCount = deduped.Count;
                        }
                        SaveCache(sym, label, cached);
                    }
                    else
                    {
                        onLog?.Invoke($"    {label}: 캐시 {cached.Count:N0}개 (최신, 수집 불필요)");
                    }

                    // 요청 기간 필터
                    var filtered = cached.Where(b => b.Time >= startDate && b.Time <= endDate).ToList();

                    if (label == "5m" && filtered.Count < 500)
                    {
                        onLog?.Invoke($"    {label}: 부족({filtered.Count}), 이 심볼 스킵");
                        symbolOk = false;
                        break;
                    }

                    dict[sym] = filtered;
                    onLog?.Invoke($"    {label}: {filtered.Count:N0}개 준비 (신규 {newCount:N0}개)");
                }

                if (!symbolOk) continue;

                // 지표 계산
                allBars5m[sym]  = BuildBars(allBars5m[sym]);
                allBars15m[sym] = BuildBars(allBars15m[sym]);
                allBars1h[sym]  = BuildBars(allBars1h[sym]);
                allBars4h[sym]  = BuildBars(allBars4h[sym]);
            }
            var allBars = allBars5m; // 메인 루프는 5분봉 기준

            // ═══ Step 2: 라벨링 + 특성 추출 ═══
            onLog?.Invoke("\n[Step 2/5] 라벨링 + 특성 추출...");
            var allFeatures = new List<(EntryFeature feat, string symbol, string dir, int barIdx)>();
            var allSnapshots = new List<PatternSnapshot>();

            foreach (var (sym, bars) in allBars)
            {
                var bars15m = allBars15m.GetValueOrDefault(sym) ?? new List<Bar>();
                var bars1h  = allBars1h.GetValueOrDefault(sym) ?? new List<Bar>();
                var bars4h  = allBars4h.GetValueOrDefault(sym) ?? new List<Bar>();

                // 종목 카테고리: 종목마다 파동 성격(기울기, 눌림 깊이)이 다름
                float symCat = sym switch
                {
                    "BTCUSDT" => 0f,
                    "ETHUSDT" => 0.25f,
                    "SOLUSDT" or "XRPUSDT" => 0.50f,
                    "DOGEUSDT" or "PEPEUSDT" => 0.75f,
                    _ => 1.0f // WIFUSDT, BONKUSDT 등 MicroCap
                };

                int warmup = 55;
                for (int i = warmup; i < bars.Count - LABEL_LOOKAHEAD; i++)
                {
                    var b = bars[i];
                    if (b.ATR <= 0 || b.VolMA <= 0) continue;

                    // 상위 TF에서 현재 시점에 해당하는 캔들 찾기
                    var htf15m = FindBarAtTime(bars15m, b.Time);
                    var htf1h  = FindBarAtTime(bars1h, b.Time);
                    var htf4h  = FindBarAtTime(bars4h, b.Time);

                    // LONG 라벨링
                    var longLabel = LabelEntry(bars, i, true);
                    if (longLabel.HasValue)
                    {
                        var feat = ExtractFeatures(bars, i, true, symCat);
                        AttachHTFFeatures(feat, htf15m, htf1h, htf4h);
                        feat.Label = longLabel.Value;
                        allFeatures.Add((feat, sym, "LONG", i));

                        if (longLabel.Value)
                        {
                            allSnapshots.Add(new PatternSnapshot
                            {
                                Vector = FeatureToVector(feat),
                                Direction = "LONG", Symbol = sym, Time = b.Time,
                                PnlPct = (decimal)LABEL_TP_PCT, IsWin = true
                            });
                        }
                    }

                    // SHORT 라벨링
                    var shortLabel = LabelEntry(bars, i, false);
                    if (shortLabel.HasValue)
                    {
                        var feat = ExtractFeatures(bars, i, false, symCat);
                        AttachHTFFeatures(feat, htf15m, htf1h, htf4h);
                        feat.Label = shortLabel.Value;
                        allFeatures.Add((feat, sym, "SHORT", i));

                        if (shortLabel.Value)
                        {
                            allSnapshots.Add(new PatternSnapshot
                            {
                                Vector = FeatureToVector(feat),
                                Direction = "SHORT", Symbol = sym, Time = b.Time,
                                PnlPct = (decimal)LABEL_TP_PCT, IsWin = true
                            });
                        }
                    }
                }
            }

            int totalSamples = allFeatures.Count;
            int winSamples = allFeatures.Count(f => f.feat.Label);
            onLog?.Invoke($"  총 {totalSamples}개 샘플 | 승리: {winSamples} ({(totalSamples > 0 ? winSamples * 100.0 / totalSamples : 0):F1}%)");
            onLog?.Invoke($"  승리 패턴 스냅샷: {allSnapshots.Count}개 저장");

            if (totalSamples < 100)
            {
                onLog?.Invoke("샘플 부족, 종료");
                return new AIBacktestResult { InitialBalance = initialBalance, FinalBalance = initialBalance };
            }

            // ═══ Step 3: ML 학습 (앞 70% 학습, 뒤 30% 검증) ═══
            onLog?.Invoke("\n[Step 3/5] LightGBM 학습...");
            int splitIdx = (int)(totalSamples * 0.70);
            var trainData = allFeatures.Take(splitIdx).Select(f => f.feat).ToList();
            var testData = allFeatures.Skip(splitIdx).Select(f => f.feat).ToList();

            var mlContext = new MLContext(seed: 42);
            var trainDV = mlContext.Data.LoadFromEnumerable(trainData);
            var testDV = mlContext.Data.LoadFromEnumerable(testData);

            string[] featureCols = {
                // 5분봉 25개
                "RSI", "RSI_Slope", "MACD_Hist", "MACD_Rising", "BB_Position",
                "BB_Width_Pct", "ATR_Pct", "Volume_Ratio", "Volume_Spike", "EMA9_Slope", "EMA21_Slope",
                "EMA_Alignment", "Price_To_EMA21", "Price_To_EMA50", "Candle_Body_Ratio", "Candle_Direction",
                "Upper_Shadow", "Lower_Shadow", "Price_Change_3Bar", "Price_Change_12Bar",
                "High_Break", "Low_Break", "Momentum_Score", "HourOfDay", "DayOfWeek",
                // 15분봉 5개
                "M15_BB_Position", "M15_BB_Width", "M15_BB_Squeeze", "M15_BB_Breakout",
                "M15_RSI", "M15_RSI_Slope", "M15_RSI_VTurn", "M15_RSI_Divergence",
                "M15_EMA_Trend", "M15_EMA_Cross", "M15_EMA_Gap", "M15_Price_Above_EMA20",
                "M15_Fib_Position", "M15_Fib_AtGoldenZone", "M15_Fib_Bounce",
                "M15_MACD_Rising", "M15_Volume_Ratio",
                // 선행성·정규화 강화 5개
                "VWAP_Position", "Price_To_EMA200", "RSI_Divergence", "OBV_Slope", "Pivot_Position",
                // 추세 강도·국면 분류 4개
                "ADX", "Ichimoku_Above_Kijun", "FundingRate", "SymbolCategory",
                // 1시간봉 5개
                "H1_RSI", "H1_MACD_Rising", "H1_BB_Position", "H1_EMA_Trend", "H1_Volume_Ratio",
                // 4시간봉 5개
                "H4_RSI", "H4_MACD_Rising", "H4_BB_Position", "H4_EMA_Trend", "H4_Volume_Ratio"
            };

            var pipeline = mlContext.Transforms.Concatenate("Features", featureCols)
                .Append(mlContext.Transforms.NormalizeMinMax("Features"))
                .Append(mlContext.BinaryClassification.Trainers.LightGbm(
                    labelColumnName: "Label",
                    featureColumnName: "Features",
                    numberOfLeaves: 31,
                    learningRate: 0.05,
                    numberOfIterations: 300,
                    minimumExampleCountPerLeaf: 20));

            var model = pipeline.Fit(trainDV);

            // 검증
            var predictions = model.Transform(testDV);
            var metrics = mlContext.BinaryClassification.Evaluate(predictions, "Label");
            onLog?.Invoke($"  학습 완료: Accuracy={metrics.Accuracy:F4} AUC={metrics.AreaUnderRocCurve:F4} F1={metrics.F1Score:F4}");
            onLog?.Invoke($"  학습 {trainData.Count}건 | 검증 {testData.Count}건");

            var predictor = mlContext.Model.CreatePredictionEngine<EntryFeature, EntryPrediction>(model);

            // ═══ Step 4: 승리 패턴 인덱스 구축 ═══
            onLog?.Invoke("\n[Step 4/5] 승리 패턴 인덱스 구축...");
            // 학습 구간의 승리 패턴만 사용 (검증 데이터 유출 방지)
            var trainSnapshots = allSnapshots.Where(s =>
                allFeatures.Take(splitIdx).Any(f => f.symbol == s.Symbol && f.dir == s.Direction)).ToList();
            onLog?.Invoke($"  학습 구간 승리 패턴: {trainSnapshots.Count}개");

            // ═══ Step 5: 검증 구간 실제 가격 시뮬레이션 ═══
            // ML이 "진입"이라 판단 → 실제 가격으로 진입 → 5분봉 순회 → SL/TP 실제 체결
            onLog?.Invoke("\n[Step 5/5] 검증 구간 실제 가격 시뮬레이션...");
            var testFeatures = allFeatures.Skip(splitIdx).ToList();

            decimal balance = initialBalance;
            decimal peak = initialBalance, maxDD = 0;
            var trades = new List<(string sym, string dir, decimal pnl, DateTime time, decimal entryPx, decimal exitPx, string reason, int holdBars)>();
            var symbolCooldown = new Dictionary<string, DateTime>();

            for (int ti = 0; ti < testFeatures.Count; ti++)
            {
                var (feat, sym, dir, barIdx) = testFeatures[ti];
                if (balance < 50m) break;
                if (!allBars.ContainsKey(sym) || barIdx >= allBars[sym].Count - 1) continue;

                // 심볼별 쿨다운 (시간 기반)
                var entryBar = allBars[sym][barIdx];
                if (symbolCooldown.TryGetValue(sym, out var coolUntil) && entryBar.Time < coolUntil)
                    continue;

                // [1] ML 예측
                var pred = predictor.Predict(feat);
                if (!pred.Predicted || pred.Probability < 0.55f) continue;

                // [2] 패턴 매칭
                var currentVec = FeatureToVector(feat);
                var matchResult = MatchPattern(currentVec, dir, trainSnapshots);
                if (matchResult.similarity < 0.55 || matchResult.matchCount < 3) continue;

                // [3] 실제 가격으로 진입
                decimal entryPrice = (decimal)entryBar.C;
                decimal atr = (decimal)entryBar.ATR;
                if (atr <= 0 || entryPrice <= 0) continue;
                atr = Math.Clamp(atr, entryPrice * 0.003m, entryPrice * 0.03m);

                // 확신도 기반 동적 SL/TP (연속 함수)
                float confidence = pred.Probability;
                double c = Math.Clamp(confidence, 0.55, 0.90);
                double t = (c - 0.55) / 0.35;
                decimal slDist    = atr * (decimal)(1.5 + t * 1.0);  // 1.5x ~ 2.5x
                decimal tp1Dist   = atr * (decimal)(3.0 + t * 2.0);  // 3.0x ~ 5.0x
                decimal tp2Dist   = atr * (decimal)(6.0 + t * 4.0);  // 6.0x ~ 10.0x

                // R:R 체크
                if (tp1Dist / slDist < 1.5m) continue;

                // 포지션 사이징 (확신도 기반 연속 함수)
                float combined = confidence * 0.6f + (float)matchResult.similarity * 0.4f;
                decimal sizePct = (decimal)(0.05 + (Math.Clamp(combined, 0.50f, 0.90f) - 0.50) / 0.40 * 0.15);
                decimal margin = Math.Min(balance * sizePct, balance * 0.20m);
                decimal notional = margin * LEVERAGE;
                decimal qty = notional / entryPrice;

                decimal slPrice, tp1Price, tp2Price;
                if (dir == "LONG")
                {
                    slPrice = entryPrice - slDist;
                    tp1Price = entryPrice + tp1Dist;
                    tp2Price = entryPrice + tp2Dist;
                }
                else
                {
                    slPrice = entryPrice + slDist;
                    tp1Price = entryPrice - tp1Dist;
                    tp2Price = entryPrice - tp2Dist;
                }

                // 수수료 차감 (진입)
                balance -= notional * FEE_RATE;

                // [4] 실제 가격으로 시뮬레이션 (5분봉 순회)
                bool posOpen = true;
                bool tp1Done = false;
                decimal remainQty = qty;
                decimal totalPnl = 0;
                int maxHold = Math.Min(barIdx + LABEL_LOOKAHEAD * 2, allBars[sym].Count); // 최대 10시간
                string exitReason = "TIME";
                decimal exitPrice = entryPrice;
                int holdBars = 0;

                for (int j = barIdx + 1; j < maxHold && posOpen; j++)
                {
                    var bar = allBars[sym][j];
                    decimal bHigh = (decimal)bar.H;
                    decimal bLow = (decimal)bar.L;
                    decimal bClose = (decimal)bar.C;
                    holdBars = j - barIdx;

                    // TP1 체크 (40% 부분익절)
                    if (!tp1Done)
                    {
                        bool tp1Hit = dir == "LONG" ? bHigh >= tp1Price : bLow <= tp1Price;
                        if (tp1Hit)
                        {
                            decimal partQty = qty * 0.40m;
                            decimal partPnl = dir == "LONG"
                                ? (tp1Price - entryPrice) * partQty
                                : (entryPrice - tp1Price) * partQty;
                            partPnl -= tp1Price * partQty * FEE_RATE;
                            totalPnl += partPnl;
                            remainQty = qty - partQty;
                            tp1Done = true;
                            slPrice = entryPrice; // 본절 이동
                        }
                    }

                    // TP2 체크 (전량)
                    bool tp2Hit = dir == "LONG" ? bHigh >= tp2Price : bLow <= tp2Price;
                    if (tp2Hit)
                    {
                        decimal pnl2 = dir == "LONG"
                            ? (tp2Price - entryPrice) * remainQty
                            : (entryPrice - tp2Price) * remainQty;
                        pnl2 -= tp2Price * remainQty * FEE_RATE;
                        totalPnl += pnl2;
                        exitPrice = tp2Price;
                        exitReason = "TP2";
                        posOpen = false;
                        break;
                    }

                    // SL 체크
                    bool slHit = dir == "LONG" ? bLow <= slPrice : bHigh >= slPrice;
                    if (slHit)
                    {
                        decimal slPnl = dir == "LONG"
                            ? (slPrice - entryPrice) * remainQty
                            : (entryPrice - slPrice) * remainQty;
                        slPnl -= slPrice * remainQty * FEE_RATE;
                        totalPnl += slPnl;
                        exitPrice = slPrice;
                        exitReason = tp1Done ? "BE" : "SL";
                        posOpen = false;
                        break;
                    }
                }

                // 시간 초과 시 현재가로 강제 청산
                if (posOpen)
                {
                    int lastIdx = Math.Min(maxHold - 1, allBars[sym].Count - 1);
                    exitPrice = (decimal)allBars[sym][lastIdx].C;
                    decimal timePnl = dir == "LONG"
                        ? (exitPrice - entryPrice) * remainQty
                        : (entryPrice - exitPrice) * remainQty;
                    timePnl -= exitPrice * remainQty * FEE_RATE;
                    totalPnl += timePnl;
                    exitReason = "TIME";
                }

                balance += totalPnl;
                peak = Math.Max(peak, balance);
                if (peak > 0) maxDD = Math.Max(maxDD, (peak - balance) / peak * 100m);

                trades.Add((sym, dir, totalPnl, entryBar.Time, entryPrice, exitPrice, exitReason, holdBars));

                // 쿨다운: 1시간
                symbolCooldown[sym] = entryBar.Time.AddHours(1);
            }

            // 결과 집계
            onLog?.Invoke($"  시뮬레이션 완료: {trades.Count}건 거래");
            int wins = trades.Count(t => t.pnl > 0);
            var byDay = trades.GroupBy(t => t.time.ToString("yyyy-MM-dd"))
                .ToDictionary(g => g.Key, g => g.Sum(t => t.pnl));
            var byMonth = trades.GroupBy(t => t.time.ToString("yyyy-MM"))
                .ToDictionary(g => g.Key, g => g.Sum(t => t.pnl));
            var bySymbol = trades.GroupBy(t => t.sym)
                .ToDictionary(g => g.Key, g => (g.Count(), g.Count(t => t.pnl > 0), g.Sum(t => t.pnl)));

            // 청산 사유별 집계
            var byReason = trades.GroupBy(t => t.reason)
                .ToDictionary(g => g.Key, g => (g.Count(), g.Sum(t => t.pnl)));
            foreach (var (reason, (cnt, pnl)) in byReason)
                onLog?.Invoke($"    {reason}: {cnt}건 PnL ${pnl:+#,##0.00;-#,##0.00}");

            decimal avgDaily = byDay.Count > 0 ? byDay.Values.Sum() / byDay.Count / initialBalance * 100m : 0;
            decimal grossProfit = trades.Where(t => t.pnl > 0).Sum(t => t.pnl);
            decimal grossLoss = Math.Abs(trades.Where(t => t.pnl <= 0).Sum(t => t.pnl));

            var result = new AIBacktestResult
            {
                InitialBalance = initialBalance,
                FinalBalance = balance,
                TotalTrades = trades.Count,
                Wins = wins,
                AvgDailyPct = avgDaily,
                MaxDrawdown = maxDD,
                ProfitFactor = grossLoss > 0 ? grossProfit / grossLoss : 0,
                TrainSamples = trainData.Count,
                WinPatterns = trainSnapshots.Count,
                ModelAccuracy = metrics.Accuracy,
                ModelAUC = metrics.AreaUnderRocCurve,
                ByMonth = byMonth,
                BySymbol = bySymbol
            };

            onLog?.Invoke(FormatResult(result));
            SaveResult(result, onLog);

            return result;
        }

        // ═══ 라벨링: 미래 데이터 참조하여 진입 성공 여부 판단 ═══
        private bool? LabelEntry(List<Bar> bars, int idx, bool isLong)
        {
            double entryPrice = bars[idx].C;
            double tpPrice = isLong ? entryPrice * (1 + LABEL_TP_PCT / 100) : entryPrice * (1 - LABEL_TP_PCT / 100);
            double slPrice = isLong ? entryPrice * (1 - LABEL_SL_PCT / 100) : entryPrice * (1 + LABEL_SL_PCT / 100);

            for (int j = idx + 1; j <= Math.Min(idx + LABEL_LOOKAHEAD, bars.Count - 1); j++)
            {
                if (isLong)
                {
                    if (bars[j].H >= tpPrice) return true;  // TP 먼저 도달 → 승리
                    if (bars[j].L <= slPrice) return false;  // SL 먼저 도달 → 패배
                }
                else
                {
                    if (bars[j].L <= tpPrice) return true;
                    if (bars[j].H >= slPrice) return false;
                }
            }
            return null; // 시간 내 미도달 → 라벨 없음 (제외)
        }

        // ═══ 특성 추출 ═══
        private EntryFeature ExtractFeatures(List<Bar> bars, int idx, bool isLong, float symbolCategory = 0.5f)
        {
            var b = bars[idx];
            var b1 = bars[idx - 1];
            var b3 = idx >= 3 ? bars[idx - 3] : b;
            var b12 = idx >= 12 ? bars[idx - 12] : b;

            double range = b.H - b.L;
            double body = Math.Abs(b.C - b.O);
            double dirMult = isLong ? 1.0 : -1.0;

            // 12봉 고점/저점
            double high12 = 0, low12 = double.MaxValue;
            for (int j = Math.Max(0, idx - 12); j < idx; j++)
            {
                high12 = Math.Max(high12, bars[j].H);
                low12 = Math.Min(low12, bars[j].L);
            }

            // 모멘텀 점수: EMA정렬 + RSI방향 + MACD방향 + 볼륨
            double momentum = 0;
            if (b.EMA9 > b.EMA21 && b.EMA21 > b.EMA50) momentum += 2;
            else if (b.EMA9 < b.EMA21 && b.EMA21 < b.EMA50) momentum -= 2;
            if (b.RSI > b.RSIPrev) momentum += 1;
            if (b.MACD > b.MACDPrev) momentum += 1;
            if (b.VolRatio > 1.5) momentum += 1;
            momentum *= dirMult;

            double bbWidth = b.BBUpper - b.BBLower;
            double bbPos = bbWidth > 0 ? (b.C - b.BBLower) / bbWidth : 0.5;

            return new EntryFeature
            {
                RSI = (float)(b.RSI / 100.0),
                RSI_Slope = (float)((b.RSI - (idx >= 3 ? bars[idx - 3].RSI : b.RSI)) / 100.0),
                MACD_Hist = (float)(b.MACD / (b.C * 0.01)),
                MACD_Rising = b.MACD > b.MACDPrev ? 1f : 0f,
                BB_Position = (float)bbPos,
                BB_Width_Pct = (float)(b.C > 0 ? bbWidth / b.C : 0),
                ATR_Pct = (float)(b.C > 0 ? b.ATR / b.C : 0),
                Volume_Ratio = (float)Math.Min(5.0, b.VolRatio),
                Volume_Spike = b.VolRatio > 2.0 ? 1f : 0f,
                EMA9_Slope = (float)(b.EMA9Prev > 0 ? (b.EMA9 - b.EMA9Prev) / b.EMA9Prev * 100 : 0),
                EMA21_Slope = (float)(b.EMA21Prev > 0 ? (b.EMA21 - b.EMA21Prev) / b.EMA21Prev * 100 : 0),
                EMA_Alignment = b.EMA9 > b.EMA21 && b.EMA21 > b.EMA50 ? 1f
                    : b.EMA9 < b.EMA21 && b.EMA21 < b.EMA50 ? -1f : 0f,
                Price_To_EMA21 = (float)(b.EMA21 > 0 ? (b.C - b.EMA21) / b.EMA21 * 100 : 0),
                Price_To_EMA50 = (float)(b.EMA50 > 0 ? (b.C - b.EMA50) / b.EMA50 * 100 : 0),
                Candle_Body_Ratio = (float)(range > 0 ? body / range : 0),
                Candle_Direction = b.C > b.O ? 1f : -1f,
                Upper_Shadow = (float)(range > 0 ? (b.H - Math.Max(b.O, b.C)) / range : 0),
                Lower_Shadow = (float)(range > 0 ? (Math.Min(b.O, b.C) - b.L) / range : 0),
                Price_Change_3Bar = (float)(b3.C > 0 ? (b.C - b3.C) / b3.C * 100 : 0),
                Price_Change_12Bar = (float)(b12.C > 0 ? (b.C - b12.C) / b12.C * 100 : 0),
                High_Break = b.H > high12 ? 1f : 0f,
                Low_Break = b.L < low12 ? 1f : 0f,
                Momentum_Score = (float)Math.Clamp(momentum / 5.0, -1.0, 1.0),
                HourOfDay = b.Time.Hour / 24f,
                DayOfWeek = (int)b.Time.DayOfWeek / 7f,

                // ── 선행성·정규화 강화 5피처 ──
                // [①] VWAP: (Price - VWAP) / Price
                VWAP_Position = (float)(b.C > 0 && b.VWAP > 0 ? (b.C - b.VWAP) / b.C : 0),
                // [②] EMA200 이격도
                Price_To_EMA200 = (float)(b.EMA200 > 0 ? (b.C - b.EMA200) / b.C : 0),
                // [③] RSI 다이버전스 (5분봉): 가격↓+RSI↑ = +1(상승시그널)
                RSI_Divergence = (float)(
                    b.C < b3.C && b.RSI > b3.RSI ?  1.0 * dirMult : // 가격↓ RSI↑ = 상승 다이버전스
                    b.C > b3.C && b.RSI < b3.RSI ? -1.0 * dirMult : // 가격↑ RSI↓ = 하락 다이버전스
                    0),
                // [④] OBV 기울기 (5봉 변화율, 정규화)
                OBV_Slope = (float)(b.OBVPrev != 0 ? Math.Clamp((b.OBV - b.OBVPrev) / Math.Abs(b.OBVPrev), -1.0, 1.0) : 0),
                // [⑤] 피보나치 피봇: (Price - Pivot) / (R1 - S1)
                Pivot_Position = (float)ComputePivotPosition(bars, idx),

                // ── 추세 강도·국면 분류 ──
                // [⑥] ADX: 추세 강도 (>25 = 강한 추세, <20 = 횡보)
                ADX = (float)(b.ADX / 100.0),
                // [⑦] 이치모쿠: 가격 > 기준선(26) = 상승국면
                Ichimoku_Above_Kijun = b.KijunSen > 0 && b.C > b.KijunSen ? 1f : 0f,
                // [⑧] 펀딩비: 백테스트에서는 0 (실시간에서만 사용)
                FundingRate = 0f,
                // [⑨] 종목 카테고리: 호출 시 설정
                SymbolCategory = symbolCategory
            };
        }

        private static double ComputePivotPosition(List<Bar> bars, int idx)
        {
            // 전일 고가/저가/종가로 피봇 계산
            var today = bars[idx].Time.Date;
            double prevH = 0, prevL = double.MaxValue, prevC = 0;
            bool found = false;
            for (int j = idx - 1; j >= 0; j--)
            {
                if (bars[j].Time.Date < today)
                {
                    if (!found) { prevC = bars[j].C; found = true; }
                    if (bars[j].Time.Date < today.AddDays(-1)) break;
                    prevH = Math.Max(prevH, bars[j].H);
                    prevL = Math.Min(prevL, bars[j].L);
                }
            }
            if (!found || prevH <= prevL) return 0;
            double pivot = (prevH + prevL + prevC) / 3.0;
            double r1 = 2.0 * pivot - prevL;
            double s1 = 2.0 * pivot - prevH;
            double span = r1 - s1;
            return span > 0 ? Math.Clamp((bars[idx].C - pivot) / span, -2.0, 2.0) : 0;
        }

        // ═══ 패턴 벡터 변환 (40차원) ═══
        private double[] FeatureToVector(EntryFeature f)
        {
            return new double[] {
                f.RSI, f.RSI_Slope, f.MACD_Hist, f.MACD_Rising, f.BB_Position,
                f.BB_Width_Pct, f.ATR_Pct, f.Volume_Ratio, f.Volume_Spike,
                f.EMA9_Slope, f.EMA21_Slope, f.EMA_Alignment, f.Price_To_EMA21,
                f.Price_To_EMA50, f.Candle_Body_Ratio, f.Candle_Direction,
                f.Upper_Shadow, f.Lower_Shadow, f.Price_Change_3Bar, f.Price_Change_12Bar,
                f.High_Break, f.Low_Break, f.Momentum_Score, f.HourOfDay, f.DayOfWeek,
                // 상위 TF
                f.M15_BB_Position, f.M15_BB_Width, f.M15_BB_Squeeze, f.M15_BB_Breakout,
                f.M15_RSI, f.M15_RSI_Slope, f.M15_RSI_VTurn, f.M15_RSI_Divergence,
                f.M15_EMA_Trend, f.M15_EMA_Cross, f.M15_EMA_Gap, f.M15_Price_Above_EMA20,
                f.M15_Fib_Position, f.M15_Fib_AtGoldenZone, f.M15_Fib_Bounce,
                f.M15_MACD_Rising, f.M15_Volume_Ratio,
                // 선행성·정규화 강화
                f.VWAP_Position, f.Price_To_EMA200, f.RSI_Divergence, f.OBV_Slope, f.Pivot_Position,
                f.ADX, f.Ichimoku_Above_Kijun, f.FundingRate, f.SymbolCategory,
                f.H1_RSI, f.H1_MACD_Rising, f.H1_BB_Position, f.H1_EMA_Trend, f.H1_Volume_Ratio,
                f.H4_RSI, f.H4_MACD_Rising, f.H4_BB_Position, f.H4_EMA_Trend, f.H4_Volume_Ratio
            };
        }

        // ═══ 상위 TF 피처 부착 ═══
        private void AttachHTFFeatures(EntryFeature feat, Bar? m15, Bar? h1, Bar? h4)
        {
            // ═══ 15분봉 핵심 4대 지표 ═══
            if (m15 != null)
            {
                double m15bbW = m15.BBUpper - m15.BBLower;
                double m15bbMid = m15.BBMid;

                // [1] 볼린저 밴드 — 스퀴즈 + 돌파
                feat.M15_BB_Position = (float)(m15bbW > 0 ? (m15.C - m15.BBLower) / m15bbW : 0.5);
                feat.M15_BB_Width = (float)(m15.C > 0 ? m15bbW / m15.C : 0);
                // 스퀴즈 강도: ATR 대비 BB폭 (작을수록 응축)
                feat.M15_BB_Squeeze = (float)(m15.ATR > 0 ? m15bbW / (m15.ATR * 4.0) : 1.0);
                // 돌파: 상단 뚫으면 +1, 하단 뚫으면 -1
                feat.M15_BB_Breakout = m15.C > m15.BBUpper ? 1f : m15.C < m15.BBLower ? -1f : 0f;

                // [2] RSI — V-Turn + 기울기 + 다이버전스
                feat.M15_RSI = (float)(m15.RSI / 100.0);
                feat.M15_RSI_Slope = (float)((m15.RSI - m15.RSIPrev) / 100.0); // RSI 변화율
                // V-Turn: 이전 RSI < 30이었고 현재 반등 중
                feat.M15_RSI_VTurn = (m15.RSIPrev < 30.0 && m15.RSI > m15.RSIPrev + 3.0) ? 1f : 0f;
                // 다이버전스: 가격은 하락(-) 인데 RSI는 상승(+) = 상승 다이버전스
                double priceDir = m15.C - m15.O; // 양수=상승, 음수=하락
                double rsiDir = m15.RSI - m15.RSIPrev;
                feat.M15_RSI_Divergence = (priceDir < 0 && rsiDir > 2.0) ? 1f  // 상승 다이버전스
                                        : (priceDir > 0 && rsiDir < -2.0) ? -1f // 하락 다이버전스
                                        : 0f;

                // [3] EMA 20/60 — 골든크로스 + 이격도
                // EMA9 = EMA20 역할, EMA50 = EMA60 역할 (Bar 구조에서)
                feat.M15_EMA_Trend = m15.EMA9 > m15.EMA50 ? 1f : m15.EMA9 < m15.EMA50 ? -1f : 0f;
                // 골든크로스: 이전에는 EMA9 < EMA50이었는데 지금은 EMA9 > EMA50
                bool prevBelow = m15.EMA9Prev <= m15.EMA50; // EMA50Prev 없으니 현재 EMA50 참조
                bool nowAbove = m15.EMA9 > m15.EMA50;
                feat.M15_EMA_Cross = (prevBelow && nowAbove) ? 1f     // 골든크로스
                                   : (!prevBelow && !nowAbove) ? -1f  // 데드크로스 (위→아래)
                                   : 0f;
                // 이격도 = (EMA20 - EMA60) / 가격 × 100 (에너지 잔량)
                feat.M15_EMA_Gap = (float)(m15.C > 0 ? (m15.EMA9 - m15.EMA50) / m15.C * 100 : 0);
                // 가격이 EMA20 위에서 지지받는지
                feat.M15_Price_Above_EMA20 = (m15.C > m15.EMA9 && m15.L >= m15.EMA9 * 0.998) ? 1f : 0f;

                // [4] 피보나치 되돌림
                // 최근 스윙 고점/저점 추정 (BB 기반 근사)
                double swingHigh = m15.BBUpper;  // 최근 고점 근사
                double swingLow = m15.BBLower;   // 최근 저점 근사
                double swingRange = swingHigh - swingLow;
                if (swingRange > 0)
                {
                    feat.M15_Fib_Position = (float)((m15.C - swingLow) / swingRange);
                    // 0.618~0.786 골든존 (되돌림 기준: 고점에서 0.618~0.786 하락 = 위치 0.214~0.382)
                    double fib618 = swingHigh - swingRange * 0.618;
                    double fib786 = swingHigh - swingRange * 0.786;
                    bool inGoldenZone = m15.C >= fib786 && m15.C <= fib618;
                    feat.M15_Fib_AtGoldenZone = inGoldenZone ? 1f : 0f;
                    // 골든존 터치 후 반등: 골든존 내에서 양봉
                    feat.M15_Fib_Bounce = (inGoldenZone && m15.C > m15.O) ? 1f : 0f;
                }

                feat.M15_MACD_Rising = m15.MACD > m15.MACDPrev ? 1f : 0f;
                feat.M15_Volume_Ratio = (float)Math.Min(5, m15.VolRatio);
            }

            // ═══ 1시간봉 ═══
            if (h1 != null)
            {
                feat.H1_RSI = (float)(h1.RSI / 100.0);
                feat.H1_MACD_Rising = h1.MACD > h1.MACDPrev ? 1f : 0f;
                double h1bbW = h1.BBUpper - h1.BBLower;
                feat.H1_BB_Position = (float)(h1bbW > 0 ? (h1.C - h1.BBLower) / h1bbW : 0.5);
                feat.H1_EMA_Trend = h1.EMA9 > h1.EMA50 ? 1f : h1.EMA9 < h1.EMA50 ? -1f : 0f;
                feat.H1_Volume_Ratio = (float)Math.Min(5, h1.VolRatio);
            }

            // ═══ 4시간봉 ═══
            if (h4 != null)
            {
                feat.H4_RSI = (float)(h4.RSI / 100.0);
                feat.H4_MACD_Rising = h4.MACD > h4.MACDPrev ? 1f : 0f;
                double h4bbW = h4.BBUpper - h4.BBLower;
                feat.H4_BB_Position = (float)(h4bbW > 0 ? (h4.C - h4.BBLower) / h4bbW : 0.5);
                feat.H4_EMA_Trend = h4.EMA9 > h4.EMA50 ? 1f : h4.EMA9 < h4.EMA50 ? -1f : 0f;
                feat.H4_Volume_Ratio = (float)Math.Min(5, h4.VolRatio);
            }
        }

        // ═══ 상위 TF에서 현재 시점 캔들 찾기 ═══
        private Bar? FindBarAtTime(List<Bar> bars, DateTime time)
        {
            if (bars.Count == 0) return null;
            // 바이너리 서치로 해당 시점 이하의 가장 가까운 캔들 찾기
            int lo = 0, hi = bars.Count - 1;
            while (lo < hi)
            {
                int mid = (lo + hi + 1) / 2;
                if (bars[mid].Time <= time) lo = mid;
                else hi = mid - 1;
            }
            return bars[lo].Time <= time ? bars[lo] : null;
        }

        // ═══ 패턴 매칭 (코사인 유사도) ═══
        private (double similarity, int matchCount) MatchPattern(
            double[] current, string dir, List<PatternSnapshot> snapshots)
        {
            var matching = snapshots.Where(s => s.Direction == dir).ToList();
            if (matching.Count < 3) return (0, matching.Count);

            var similarities = matching.Select(s => CosineSimilarity(current, s.Vector)).OrderByDescending(s => s).ToList();
            // Top-5 평균
            double topAvg = similarities.Take(Math.Min(5, similarities.Count)).Average();
            return (topAvg, matching.Count);
        }

        private double CosineSimilarity(double[] a, double[] b)
        {
            double dot = 0, magA = 0, magB = 0;
            for (int i = 0; i < a.Length && i < b.Length; i++)
            {
                dot += a[i] * b[i];
                magA += a[i] * a[i];
                magB += b[i] * b[i];
            }
            double denom = Math.Sqrt(magA) * Math.Sqrt(magB);
            return denom > 0 ? dot / denom : 0;
        }

        // ═══ 캐시 경로 ═══
        private static readonly string CacheDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "TradingBot", "KlineCache");

        // ═══ 캐시 저장 (CSV) ═══
        // [v4.6.2] 파일 잠금 충돌 해결 — 임시 파일에 쓰고 atomic rename, 실패 시 3회 재시도
        // 기존: StreamWriter로 직접 쓰기 → 다른 프로세스 접근 시 IOException
        private static void SaveCache(string symbol, string interval, List<Bar> bars)
        {
            try
            {
                Directory.CreateDirectory(CacheDir);
                string path = Path.Combine(CacheDir, $"{symbol}_{interval}.csv");
                string tmpPath = path + $".tmp.{Guid.NewGuid():N}";

                // 임시 파일에 쓰기 (FileShare.Read로 다른 프로세스 읽기 허용)
                using (var fs = new FileStream(tmpPath, FileMode.Create, FileAccess.Write, FileShare.Read))
                using (var sw = new StreamWriter(fs, Encoding.UTF8))
                {
                    foreach (var b in bars)
                        sw.WriteLine($"{b.Time:o},{b.O},{b.H},{b.L},{b.C},{b.Vol}");
                }

                // Atomic rename — 3회 재시도 (다른 프로세스가 잠시 점유 중일 수 있음)
                Exception? lastEx = null;
                for (int retry = 0; retry < 3; retry++)
                {
                    try
                    {
                        if (File.Exists(path)) File.Delete(path);
                        File.Move(tmpPath, path);
                        return; // 성공
                    }
                    catch (IOException ex)
                    {
                        lastEx = ex;
                        System.Threading.Thread.Sleep(100 * (retry + 1)); // 100ms / 200ms / 300ms
                    }
                }

                // 3회 모두 실패 — 임시 파일 정리 후 무시 (캐시 저장 실패는 치명적이지 않음)
                try { File.Delete(tmpPath); } catch { }
                Console.WriteLine($"[AIBacktestEngine] SaveCache 실패 ({symbol} {interval}): {lastEx?.Message}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[AIBacktestEngine] SaveCache 예외 ({symbol} {interval}): {ex.Message}");
            }
        }

        // ═══ 캐시 로드 ═══
        // [v4.6.2] FileShare.ReadWrite로 다른 프로세스 동시 쓰기 허용 + try-catch
        private static List<Bar> LoadCache(string symbol, string interval)
        {
            string path = Path.Combine(CacheDir, $"{symbol}_{interval}.csv");
            if (!File.Exists(path)) return new List<Bar>();

            var bars = new List<Bar>();
            try
            {
                using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                using var sr = new StreamReader(fs, Encoding.UTF8);
                string? line;
                while ((line = sr.ReadLine()) != null)
                {
                    var p = line.Split(',');
                    if (p.Length < 6) continue;
                    if (!DateTime.TryParse(p[0], CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var time)) continue;
                    bars.Add(new Bar
                    {
                        Time = time,
                        O = double.TryParse(p[1], NumberStyles.Any, CultureInfo.InvariantCulture, out var o) ? o : 0,
                        H = double.TryParse(p[2], NumberStyles.Any, CultureInfo.InvariantCulture, out var h) ? h : 0,
                        L = double.TryParse(p[3], NumberStyles.Any, CultureInfo.InvariantCulture, out var l) ? l : 0,
                        C = double.TryParse(p[4], NumberStyles.Any, CultureInfo.InvariantCulture, out var c) ? c : 0,
                        Vol = double.TryParse(p[5], NumberStyles.Any, CultureInfo.InvariantCulture, out var v) ? v : 0,
                    });
                }
            }
            catch (IOException ex)
            {
                Console.WriteLine($"[AIBacktestEngine] LoadCache 잠금 충돌 ({symbol} {interval}): {ex.Message}");
                return new List<Bar>();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[AIBacktestEngine] LoadCache 예외 ({symbol} {interval}): {ex.Message}");
                return new List<Bar>();
            }
            return bars;
        }

        // ═══ 캔들에 지표 부착 (Bar 리스트 입력) ═══
        private List<Bar> BuildBars(List<Bar> bars)
        {

            // EMA
            ComputeEma(bars, 9, b => b.C, (b, v) => b.EMA9 = v);
            ComputeEma(bars, 21, b => b.C, (b, v) => b.EMA21 = v);
            ComputeEma(bars, 50, b => b.C, (b, v) => b.EMA50 = v);
            for (int i = 1; i < bars.Count; i++)
            {
                bars[i].EMA9Prev = bars[i - 1].EMA9;
                bars[i].EMA21Prev = bars[i - 1].EMA21;
            }

            // RSI
            for (int i = 15; i < bars.Count; i++)
            {
                double gain = 0, loss = 0;
                for (int j = i - 13; j <= i; j++)
                {
                    double d = bars[j].C - bars[j - 1].C;
                    if (d > 0) gain += d; else loss -= d;
                }
                double rs = loss > 0 ? gain / 14.0 / (loss / 14.0) : 100;
                bars[i].RSI = 100.0 - 100.0 / (1 + rs);
                bars[i].RSIPrev = bars[i - 1].RSI;
            }

            // ATR
            for (int i = 1; i < bars.Count; i++)
            {
                double tr = Math.Max(bars[i].H - bars[i].L,
                    Math.Max(Math.Abs(bars[i].H - bars[i - 1].C), Math.Abs(bars[i].L - bars[i - 1].C)));
                bars[i].ATR = i < 15 ? tr : bars[i - 1].ATR * 13.0 / 14.0 + tr / 14.0;
            }

            // BB
            for (int i = 20; i < bars.Count; i++)
            {
                var slice = bars.Skip(i - 19).Take(20).Select(b => b.C).ToList();
                double mid = slice.Average();
                double std = Math.Sqrt(slice.Average(c => (c - mid) * (c - mid)));
                bars[i].BBMid = mid;
                bars[i].BBUpper = mid + 2.0 * std;
                bars[i].BBLower = mid - 2.0 * std;
            }

            // Vol MA
            for (int i = 20; i < bars.Count; i++)
            {
                bars[i].VolMA = bars.Skip(i - 19).Take(20).Average(b => b.Vol);
                bars[i].VolRatio = bars[i].VolMA > 0 ? bars[i].Vol / bars[i].VolMA : 1;
            }

            // MACD
            for (int i = 1; i < bars.Count; i++)
            {
                bars[i].MACD = bars[i].EMA9 - bars[i].EMA21;
                bars[i].MACDPrev = bars[i - 1].EMA9 - bars[i - 1].EMA21;
            }

            // EMA200 — 장기추세 기준점
            ComputeEma(bars, 200, b => b.C, (b, v) => b.EMA200 = v);

            // VWAP — 거래량 가중 평균 가격 (당일 기준 리셋)
            {
                double cumVP = 0, cumVol = 0;
                DateTime lastDate = DateTime.MinValue;
                for (int i = 0; i < bars.Count; i++)
                {
                    // 날짜 변경시 리셋 (당일 VWAP)
                    if (bars[i].Time.Date != lastDate)
                    {
                        cumVP = 0; cumVol = 0;
                        lastDate = bars[i].Time.Date;
                    }
                    double typicalPrice = (bars[i].H + bars[i].L + bars[i].C) / 3.0;
                    cumVP += typicalPrice * bars[i].Vol;
                    cumVol += bars[i].Vol;
                    bars[i].VWAP = cumVol > 0 ? cumVP / cumVol : bars[i].C;
                }
            }

            // OBV — On-Balance Volume (가짜 상승 감지)
            for (int i = 0; i < bars.Count; i++)
            {
                if (i == 0) { bars[i].OBV = bars[i].Vol; continue; }
                double delta = bars[i].C > bars[i - 1].C ? bars[i].Vol
                             : bars[i].C < bars[i - 1].C ? -bars[i].Vol
                             : 0;
                bars[i].OBV = bars[i - 1].OBV + delta;
                bars[i].OBVPrev = bars[i - 1].OBV;
            }

            // ADX (Average Directional Index) — 추세 강도 판별
            {
                double prevPlusDM = 0, prevMinusDM = 0, prevTR = 0, prevADX = 0;
                for (int i = 1; i < bars.Count; i++)
                {
                    double hi = bars[i].H, lo = bars[i].L, prevC = bars[i - 1].C;
                    double prevHi = bars[i - 1].H, prevLo = bars[i - 1].L;
                    double tr = Math.Max(hi - lo, Math.Max(Math.Abs(hi - prevC), Math.Abs(lo - prevC)));
                    double plusDM = hi - prevHi > prevLo - lo && hi - prevHi > 0 ? hi - prevHi : 0;
                    double minusDM = prevLo - lo > hi - prevHi && prevLo - lo > 0 ? prevLo - lo : 0;

                    if (i <= 14)
                    {
                        prevPlusDM += plusDM; prevMinusDM += minusDM; prevTR += tr;
                        if (i == 14)
                        {
                            double plusDI = prevTR > 0 ? prevPlusDM / prevTR * 100 : 0;
                            double minusDI = prevTR > 0 ? prevMinusDM / prevTR * 100 : 0;
                            double dx = (plusDI + minusDI) > 0 ? Math.Abs(plusDI - minusDI) / (plusDI + minusDI) * 100 : 0;
                            prevADX = dx;
                        }
                    }
                    else
                    {
                        prevPlusDM = prevPlusDM - prevPlusDM / 14.0 + plusDM;
                        prevMinusDM = prevMinusDM - prevMinusDM / 14.0 + minusDM;
                        prevTR = prevTR - prevTR / 14.0 + tr;
                        double plusDI = prevTR > 0 ? prevPlusDM / prevTR * 100 : 0;
                        double minusDI = prevTR > 0 ? prevMinusDM / prevTR * 100 : 0;
                        double dx = (plusDI + minusDI) > 0 ? Math.Abs(plusDI - minusDI) / (plusDI + minusDI) * 100 : 0;
                        prevADX = (prevADX * 13 + dx) / 14.0;
                    }
                    bars[i].ADX = prevADX;
                }
            }

            // 이치모쿠 기준선 (Kijun-sen = 26봉 최고+최저 / 2)
            for (int i = 26; i < bars.Count; i++)
            {
                double hi26 = 0, lo26 = double.MaxValue;
                for (int j = i - 25; j <= i; j++)
                {
                    hi26 = Math.Max(hi26, bars[j].H);
                    lo26 = Math.Min(lo26, bars[j].L);
                }
                bars[i].KijunSen = (hi26 + lo26) / 2.0;
            }

            return bars;
        }

        private void ComputeEma(List<Bar> bars, int period, Func<Bar, double> get, Action<Bar, double> set)
        {
            if (bars.Count < period) return;
            double k = 2.0 / (period + 1);
            double ema = bars.Take(period).Average(b => get(b));
            for (int i = 0; i < bars.Count; i++)
            {
                if (i < period) set(bars[i], bars.Take(i + 1).Average(b => get(b)));
                else { ema = get(bars[i]) * k + ema * (1 - k); set(bars[i], ema); }
            }
        }

        // ═══ 결과 포맷 ═══
        public string FormatResult(AIBacktestResult r)
        {
            var sb = new StringBuilder();
            sb.AppendLine("\n╔══════════════════════════════════════════════════════════════════╗");
            sb.AppendLine("║          AI 학습 백테스트 결과                                   ║");
            sb.AppendLine("╠══════════════════════════════════════════════════════════════════╣");
            sb.AppendLine($"║  ML 모델  : Accuracy={r.ModelAccuracy:F4} AUC={r.ModelAUC:F4}");
            sb.AppendLine($"║  학습      : {r.TrainSamples}건 학습 | {r.WinPatterns}개 승리패턴");
            sb.AppendLine("╠══════════════════════════════════════════════════════════════════╣");
            sb.AppendLine($"║  초기 잔고  : ${r.InitialBalance,10:N2}");
            sb.AppendLine($"║  최종 잔고  : ${r.FinalBalance,10:N2}");
            sb.AppendLine($"║  총 수익    : ${r.TotalPnl,+10:N2}  ({r.TotalPnlPct:+0.00;-0.00}%)");
            sb.AppendLine($"║  거래수     : {r.TotalTrades,6}건  승률: {r.WinRate:F1}%");
            sb.AppendLine($"║  프로핏팩터 : {r.ProfitFactor:F3}");
            sb.AppendLine($"║  최대 낙폭  : {r.MaxDrawdown:F2}%");
            sb.AppendLine($"║  ★ 일평균   : {r.AvgDailyPct:+0.00;-0.00}%");
            sb.AppendLine("╠══════════════════════════════════════════════════════════════════╣");
            sb.AppendLine("║  [심볼별 성과]");
            foreach (var (sym, (t, w, pnl)) in r.BySymbol.OrderByDescending(kv => kv.Value.pnl))
                sb.AppendLine($"║   {sym,-10} {t,4}건 승률{(t > 0 ? w * 100.0 / t : 0),5:F0}% PnL ${pnl,+9:N2}");
            sb.AppendLine("╠══════════════════════════════════════════════════════════════════╣");
            sb.AppendLine("║  [월별 수익]");
            foreach (var (m, pnl) in r.ByMonth.OrderBy(kv => kv.Key))
            {
                decimal pct = r.InitialBalance > 0 ? pnl / r.InitialBalance * 100m : 0;
                sb.AppendLine($"║   {m}  ${pnl,+8:N0} ({pct:+0.0;-0.0}%)");
            }
            sb.AppendLine("╚══════════════════════════════════════════════════════════════════╝");
            return sb.ToString();
        }

        public string SaveResult(AIBacktestResult r, Action<string>? onLog = null)
        {
            string name = $"ai_backtest_{DateTime.Now:yyyyMMdd_HHmmss}.txt";
            string path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, name);
            File.WriteAllText(path, FormatResult(r), Encoding.UTF8);
            onLog?.Invoke($"결과 저장: {path}");
            return path;
        }

        // ═══ 데이터 수집 (대용량 지원, 진행률 표시) ═══
        private static async Task<List<IBinanceKline>> FetchKlinesAsync(
            BinanceRestClient client, string symbol, KlineInterval interval,
            DateTime startUtc, DateTime endUtc, Action<string>? onLog = null)
        {
            var result = new List<IBinanceKline>();
            var cursor = startUtc;
            var totalSpan = (endUtc - startUtc).TotalDays;
            int batchCount = 0;

            while (cursor < endUtc)
            {
                var resp = await client.UsdFuturesApi.ExchangeData.GetKlinesAsync(
                    symbol, interval, startTime: cursor, endTime: endUtc, limit: 1500);

                if (!resp.Success || resp.Data == null || !resp.Data.Any())
                {
                    if (!resp.Success)
                        await Task.Delay(2000); // API 에러시 대기 후 재시도
                    break;
                }

                result.AddRange(resp.Data);
                cursor = resp.Data.Last().CloseTime.AddMilliseconds(1);
                batchCount++;

                // 진행률 표시 (50배치마다)
                if (batchCount % 50 == 0 && onLog != null)
                {
                    double pct = totalSpan > 0 ? (cursor - startUtc).TotalDays / totalSpan * 100 : 0;
                    onLog($"    {symbol} {interval}: {result.Count:N0}개 ({pct:F0}%)...");
                }

                await Task.Delay(80); // 속도 제한 방지
            }
            return result.OrderBy(k => k.OpenTime).ToList();
        }
    }
}
