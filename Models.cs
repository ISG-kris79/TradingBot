﻿using Binance.Net.Enums;
using System.Collections.Generic;
using Microsoft.ML.Data;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Media;
using ExchangeType = TradingBot.Shared.Models.ExchangeType;

namespace TradingBot.Models
{
    public enum StrategyType { Major, Scanner, Listing }

    public class PumpScanSettings
    {
        public decimal MinPriceChangePercentage { get; set; } = 1.0m;
        public double MinVolumeRatio { get; set; } = 2.5;
        public double MinOrderBookRatio { get; set; } = 0.8;
        public double MinTakerBuyRatio { get; set; } = 1.2;
        public double MinVolumeRatio5m { get; set; } = 2.0;
    }

    public class TradingSettings
    {
        public int DefaultLeverage { get; set; } = 15;  // [v5.10.97] 25→15 하향 (수수료/슬리피지 영향 감소)
        public decimal DefaultMargin { get; set; } = 200.0m;
        public decimal SidewaysTakeProfitRoe { get; set; } = 5.0m;
        // [v5.21.0] 90일 백테스트 결과: TP 1.0% × 15x = ROE 15%, SL 3.0% × 15x = ROE 45%
        //   카테고리별 PnL (90일): MAJOR +$11,459 / SQUEEZE +$13,054 / BB_WALK +$4,299 / PUMP -$9 / SPIKE 차단
        //   합계 +$28,803 (이전 1.5/0.7 비대칭 = -$16,408 적자)
        public decimal TargetRoe { get; set; } = 15.0m;
        public decimal StopLossRoe { get; set; } = 45.0m;
        public decimal TrailingStartRoe { get; set; } = 20.0m;
        public decimal TrailingDropRoe { get; set; } = 5.0m;
        public string MajorTrendProfile { get; set; } = string.Empty;

        // [Phase 12: PUMP 전략 지원] PUMP 전략 전용 레버리지
        public int PumpLeverage { get; set; } = 15;  // [v5.10.97] 20→15 하향

        // [v5.21.0] PUMP — 90일 백테스트 기반 ROE 15%/45% 통일 (15x 기준 TP 1.0% / SL 3.0%)
        public decimal PumpTp1Roe { get; set; } = 15.0m;          // 1차 부분익절 ROI 기준 (포지션 30% 청산)
        public decimal PumpTp2Roe { get; set; } = 100.0m;          // 2차 익절 ROE (미사용, 레거시)
        public decimal PumpTimeStopMinutes { get; set; } = 120.0m; // 시간 손절(분)
        public decimal PumpStopDistanceWarnPct { get; set; } = 1.0m; // 손절거리 경고(비중축소)
        public decimal PumpStopDistanceBlockPct { get; set; } = 1.3m; // 손절거리 차단(진입취소)

        // ─── [Meme Coin Mode] PUMP 전용 포지션 관리 ───────────────────────────
        // 1차 부분익절: ROI +20% → 포지션 30% 청산
        // 2차 트레일링: ROI +40% 시작 → 최고점 대비 ROI 5% 하락 시 50% 청산
        // 3차 나머지: 2차에서 +5% 내려가면 스탑로스
        // 초기 손절: ROI -40% (가격 -2%, 20x) — 진입 품질 개선으로 넓은 손절 유지 (찍고 날라가는 경우 대비)
        public decimal PumpStopLossRoe { get; set; } = 45.0m;      // [v5.21.0] 90일 백테스트 기반 SL 3.0% × 15x
        public decimal PumpMargin { get; set; } = 200.0m;           // PUMP 전용 기본 진입 증거금 $200 고정
        public decimal PumpBreakEvenRoe { get; set; } = 25.0m;     // ROI +25% 시 본절 이동 (슬리피지 대응)
        // 주의: 0.15% 오프셋(슬리피지 방어)이 적용되어 실제 손절은 진입가 + 0.15% 근처로 설정됨
        public decimal PumpTrailingStartRoe { get; set; } = 40.0m; // 2차 트레일링 시작 ROI +40% (변경 없음)
        public decimal PumpTrailingGapRoe { get; set; } = 20.0m;    // 2차에서 최고점 대비 ROI 20% 하락 시 청산
        public decimal PumpFirstTakeProfitRatioPct { get; set; } = 40.0m; // [v3.9.3] 1차 부분익절 15→40% (수익 확보 강화)
        public decimal PumpStairStep1Roe { get; set; } = 50.0m;     // 계단식 1단계 트리거 ROE
        public decimal PumpStairStep2Roe { get; set; } = 100.0m;    // 계단식 2단계 트리거 ROE
        public decimal PumpStairStep3Roe { get; set; } = 200.0m;    // 계단식 3단계 트리거 ROE

        // ─── [Major Coin Mode] 메이저 전용 포지션 관리 ────────────────────────────
        // 1차 부분익절: ROI +20% → 포지션 30% 청산
        // 2차 트레일링: ROI +20% 시작 → 최고점 대비 ROI 5% 하락 시 50% 청산
        // 3차 나머지: 2차에서 +5% 내려가면 스탑로스
        // 초기 손절: ROI -20%
        public int    MajorLeverage          { get; set; } = 15;       // [v5.10.97] 20→15 하향 (DefaultLeverage와 독립)
        public decimal MajorMargin           { get; set; } = 200.0m;   // (레거시) 메이저 고정 증거금
        public decimal MajorMarginPercent    { get; set; } = 10.0m;    // 메이저 진입 시 계좌 Equity 대비 증거금 비율(%)
        public decimal MajorBreakEvenRoe     { get; set; } = 7.0m;    // 1단계: 본절 이동 기준 ROE (변경 없음)
        // [v5.21.0] MAJOR — 90일 검증: TP 1.0% × 15x = ROE 15%, WR 97.82%, +$11,459 (n=1376)
        public decimal MajorTp1Roe           { get; set; } = 15.0m;   // 1차 부분익절 ROI +15%
        public decimal MajorTp2Roe           { get; set; } = 30.0m;   // 2차 수익 확정 구간
        public decimal MajorTrailingStartRoe { get; set; } = 30.0m;   // 타이트 트레일링 시작 ROI +30%
        public decimal MajorTrailingGapRoe   { get; set; } = 5.0m;    // 트레일링 간격
        public decimal MajorStopLossRoe      { get; set; } = 45.0m;   // [v5.21.0] SL 3.0% × 15x = ROE 45% (청산선 -6.7%까지 1.7% 버퍼)

        // ─── [설정 연동] 슬롯 / 메이저 활성화 / 하루 진입 횟수 ──────────────────────
        /// <summary>메이저 코인 전략 활성화 (false 시 메이저 진입 차단)</summary>
        public bool EnableMajorTrading { get; set; } = true;
        /// <summary>메이저 최대 동시 포지션 수 (기본 4)</summary>
        public int MaxMajorSlots { get; set; } = 4;
        /// <summary>PUMP 최대 동시 포지션 수 (기본 3)</summary>
        public int MaxPumpSlots { get; set; } = 3;
        /// <summary>하루 최대 PUMP 진입 횟수 (기본 60, 자정 KST 리셋)</summary>
        public int MaxDailyEntries { get; set; } = 60;

        // ─── [Sniper Mode] 스나이퍼 모드 설정 (v2.5) ────────────────────────────
        // 종목당 일일 1~2회 진입으로 정조준하는 모드
        public bool IsSniperModeEnabled { get; set; } = true;         // 스나이퍼 모드 활성화 여부
        public double MinimumEntryScore { get; set; } = 80.0;         // 최소 진입 점수 (노이즈 차단)
        public int MaxTradesPerSymbolPerDay { get; set; } = 2;        // 심볼당 하루 최대 진입 횟수
        public int MaxActivePositions { get; set; } = 5;             // 최대 활성 포지션 수 (메이저 3 + 밈 2)
        public int EntryCooldownMinutes { get; set; } = 120;          // 진입 쿨다운 (분, 한 파동 먹고 2시간 휴식)

        // ─── [급변 감지] 시장 CRASH/PUMP 자동 대응 ───────────────────────
        public bool CrashDetectorEnabled { get; set; } = true;
        public decimal CrashThresholdPct { get; set; } = -1.5m;     // 1분 -1.5% → CRASH
        public decimal PumpDetectThresholdPct { get; set; } = 1.5m;  // 1분 +1.5% → PUMP
        public int CrashMinCoinCount { get; set; } = 2;              // 최소 동시 급변 코인 수
        public decimal CrashReverseSizeRatio { get; set; } = 0.5m;   // 리버스 진입 사이즈 (50%)
        public int CrashCooldownSeconds { get; set; } = 120;         // 발동 후 쿨다운 (초)

    }

    public enum PerformanceTuningProfile
    {
        Default = 0,      // 기본 균형형 프로파일
        Conservative = 1, // 안정형: AUTOTUNE 빈도↓, 변화폭↓
        Aggressive = 2    // 공격형: AUTOTUNE 빈도↑, 변화폭↑
    }

    public class PerformanceMonitoringSettings
    {
        public bool EnableMetrics { get; set; } = true;
        public bool EnableAutoTune { get; set; } = true;
        public PerformanceTuningProfile Profile { get; set; } = PerformanceTuningProfile.Aggressive;

        public int LiveLogFlushWarnMs { get; set; } = 40;
        public int LiveLogPerfLogIntervalSec { get; set; } = 10;
        public int MainLoopWarnMs { get; set; } = 1500;
        public int MainLoopPerfLogIntervalSec { get; set; } = 20;

        public int AutoTuneSampleWindow { get; set; } = 60;
        public int AutoTuneMinIntervalSec { get; set; } = 30;

        public int LiveLogFlushWarnMinMs { get; set; } = 20;
        public int LiveLogFlushWarnMaxMs { get; set; } = 250;
        public int MainLoopWarnMinMs { get; set; } = 700;
        public int MainLoopWarnMaxMs { get; set; } = 8000;
        public int PerfLogIntervalMinSec { get; set; } = 5;
        public int PerfLogIntervalMaxSec { get; set; } = 60;

        // 프로파일에 따른 multiplier 반환 (LiveLog용)
        public double GetLiveLogMultiplier()
        {
            return Profile switch
            {
                PerformanceTuningProfile.Conservative => 1.5,  // 안정형: 변화폭 낮음
                PerformanceTuningProfile.Aggressive => 1.2,     // 공격형: 변화폭 높음
                _ => 1.35                                        // 기본
            };
        }

        // 프로파일에 따른 multiplier 반환 (MainLoop용)
        public double GetMainLoopMultiplier()
        {
            return Profile switch
            {
                PerformanceTuningProfile.Conservative => 1.45,  // 안정형: 변화폭 낮음
                PerformanceTuningProfile.Aggressive => 1.15,    // 공격형: 변화폭 높음
                _ => 1.30                                        // 기본
            };
        }

        // 프로파일 적용 (사전정의 프리셋)
        public void ApplyProfile()
        {
            switch (Profile)
            {
                case PerformanceTuningProfile.Conservative:
                    // 안정형: AUTOTUNE 빈도 낮음, 변화폭 낮음
                    AutoTuneSampleWindow = 120;      // 샘플 더 많이 수집
                    AutoTuneMinIntervalSec = 60;     // 1분마다 튜닝
                    LiveLogFlushWarnMs = 50;         // 초기값 더 보수적
                    MainLoopWarnMs = 1800;           // 초기값 더 보수적
                    LiveLogPerfLogIntervalSec = 15;  // 로그 빈도 낮춤
                    MainLoopPerfLogIntervalSec = 30; // 로그 빈도 낮춤
                    break;

                case PerformanceTuningProfile.Aggressive:
                    // 공격형: AUTOTUNE 빈도 높음, 변화폭 높음
                    AutoTuneSampleWindow = 30;       // 빠르게 반응
                    AutoTuneMinIntervalSec = 20;     // 20초마다 튜닝
                    LiveLogFlushWarnMs = 30;         // 초기값 더 공격적
                    MainLoopWarnMs = 1200;           // 초기값 더 공격적
                    LiveLogPerfLogIntervalSec = 8;   // 로그 빈도 높임
                    MainLoopPerfLogIntervalSec = 15; // 로그 빈도 높임
                    break;

                default:
                    // 기본 프로파일 (이미 설정된 기본값 유지)
                    break;
            }
        }
    }

    public class DualAIPredictorSettings
    {
        public bool EnableDualAI { get; set; } = true;
        public int MinCandleCount { get; set; } = 60;
        public int TransformerSeqLen { get; set; } = 50;
        public float MLNetWeight { get; set; } = 0.4f;
        public float TransformerWeight { get; set; } = 0.6f;
        public float StrongSignalThreshold { get; set; } = 70f;
        public int RetrainIntervalMinutes { get; set; } = 180;
        public bool AutoRetrainEnabled { get; set; } = false; // 기본값 false (수동 제어)
    }

    public class GridStrategySettings
    {
        public int GridLevels { get; set; } = 10;
        public decimal GridSpacingPercentage { get; set; } = 0.5m;
        public decimal AmountPerGrid { get; set; } = 20.0m;
    }

    public class ArbitrageSettings
    {
        public decimal MinSpreadPercentage { get; set; } = 0.5m;
        public bool AutoHedge { get; set; } = true;
        // [Phase 13] ArbitrageExecutionService를 위한 추가 속성
        public decimal MinProfitPercent { get; set; } = 0.2m;  // 최소 수익률
        public bool AutoExecute { get; set; } = false;         // 자동 실행 여부
        public int ScanIntervalSeconds { get; set; } = 60;     // 스캔 간격
        public decimal DefaultQuantity { get; set; } = 100m;   // 기본 수량 (USDT)
        public bool SimulationMode { get; set; } = true;       // [Phase 14] 시뮬레이션 모드 (안전)
    }

    // [Phase 14] 자금 이동 서비스 설정
    public class FundTransferSettings
    {
        public int CheckIntervalMinutes { get; set; } = 60;    // 체크 간격 (분)
        public decimal MinTransferAmount { get; set; } = 100m;  // 최소 이동 금액 (USDT)
        public decimal TargetBalanceRatio { get; set; } = 0.5m; // 목표 잔고 비율
        public bool SimulationMode { get; set; } = true;        // [Phase 14] 시뮬레이션 모드 (안전)
    }

    // [Phase 14] 포트폴리오 리밸런싱 설정
    public class PortfolioRebalancingSettings
    {
        public int CheckIntervalHours { get; set; } = 24;       // 체크 간격 (시간)
        public decimal RebalanceThreshold { get; set; } = 5.0m; // 리밸런싱 임계값 (%)
        public bool SimulationMode { get; set; } = true;        // [Phase 14] 시뮬레이션 모드 (안전)
        public Dictionary<string, decimal> TargetAllocations { get; set; } = new()
        {
            { "BTC", 40m },
            { "ETH", 30m },
            { "SOL", 20m },
            { "USDT", 10m }
        };
    }

    // [이동] TradingEngine에서 사용하던 캐시 아이템
    public class TickerCacheItem
    {
        public string? Symbol { get; set; }
        public decimal LastPrice { get; set; }
        public decimal HighPrice { get; set; }
        public decimal OpenPrice { get; set; }
        public decimal QuoteVolume { get; set; }
        public decimal PriceChangePercent { get; set; } // [v4.5.5] 24h 변동률 (%)
    }

    // [v5.10.77 Phase 5-A] WebSocket bookTicker 캐시 — Bid/Ask Imbalance 선행 지표용
    public class BookTickerCacheItem
    {
        public string? Symbol { get; set; }
        public decimal BestBidPrice { get; set; }
        public decimal BestBidQty { get; set; }
        public decimal BestAskPrice { get; set; }
        public decimal BestAskQty { get; set; }
        public DateTime UpdatedAt { get; set; }
    }

    // [v5.10.79 Phase 5-C] aggTrade 1분 슬라이딩 윈도우 통계 — 체결 매수/매도 비율 학습용
    public class AggTradeStatsItem
    {
        public string? Symbol { get; set; }
        public decimal BuyVolume1m { get; set; }   // 1분 누적 매수 볼륨 (taker buy)
        public decimal SellVolume1m { get; set; }  // 1분 누적 매도 볼륨 (taker sell)
        public DateTime UpdatedAt { get; set; }
    }

    // [v5.10.79 Phase 5-C] markPrice (Funding Rate 포함) 캐시
    public class MarkPriceCacheItem
    {
        public string? Symbol { get; set; }
        public decimal MarkPrice { get; set; }
        public decimal FundingRate { get; set; }       // 8h funding rate (예: 0.0001 = 0.01%)
        public DateTime NextFundingTime { get; set; }
        public DateTime UpdatedAt { get; set; }
    }

    // [v5.10.80 Phase 5-D] Open Interest 1분 주기 REST 캐시 + 15분 변화율 추적
    public class OpenInterestCacheItem
    {
        public string? Symbol { get; set; }
        public decimal OpenInterest { get; set; }                // 현재 OI (계약 수량)
        public decimal OpenInterest15mAgo { get; set; }          // 15분 전 OI
        public DateTime UpdatedAt { get; set; }
        public DateTime LastSnapshotAt { get; set; }
    }

    // [v5.10.80 Phase 5-D] OrderBook depth 5단계 누적 — 더 깊은 매수/매도 압력
    public class DepthCacheItem
    {
        public string? Symbol { get; set; }
        public decimal Top5_BidVolume { get; set; }              // 매수 5단계 누적 수량
        public decimal Top5_AskVolume { get; set; }              // 매도 5단계 누적 수량
        public decimal Top5_BidValue { get; set; }               // 매수 5단계 누적 명목가치 (USDT)
        public decimal Top5_AskValue { get; set; }
        public DateTime UpdatedAt { get; set; }
    }

    // [이동] 볼린저 밴드 결과 구조체
    public struct BBResult
    {
        public double Upper; public double Mid; public double Lower;
    }


    // Removed TradeLog - use TradingBot.Shared.Models.TradeLog instead
    // Removed CandleModel - use TradingBot.Shared.Models.CandleModel instead

    /// <summary>
    /// Batch Order Request Model for Grid Strategy
    /// </summary>
    public class BatchOrderRequest
    {
        public required string Symbol { get; set; }
        public required string Side { get; set; } // "BUY" or "SELL"
        public decimal Quantity { get; set; }
        public decimal? Price { get; set; }
        public string? OrderType { get; set; } = "LIMIT"; // "LIMIT" or "MARKET"
    }

    public class BatchOrderResult
    {
        public int SuccessCount { get; set; }
        public int FailureCount { get; set; }
        public List<string> OrderIds { get; set; } = new();
        public List<string> Errors { get; set; } = new();
    }

    public class CandleData
    {
        // ML.NET LoadFromEnumerable 비호환 타입 → [NoColumn] 처리
        [NoColumn] public string? Symbol { get; set; }
        [NoColumn] public decimal Open { get; set; }
        [NoColumn] public decimal High { get; set; }
        [NoColumn] public decimal Low { get; set; }
        [NoColumn] public decimal Close { get; set; }
        public float Volume { get; set; }
        [NoColumn] public string? Interval { get; set; } // 1m, 5m, 15m, 1h, 2h, 4h, 1d
        [NoColumn] public DateTime OpenTime { get; set; }
        [NoColumn] public DateTime CloseTime { get; set; }

        // OI / Funding / Squeeze (통합 호환 필드)
        public float OpenInterest { get; set; }
        public float OI_Change_Pct { get; set; }
        public float FundingRate { get; set; }
        [NoColumn] public int SqueezeLabel { get; set; }

        // ──────────────────── 보조지표 Features ────────────────────
        // 1. 기본 보조지표
        public float RSI { get; set; }
        public float BollingerUpper { get; set; }
        public float BollingerLower { get; set; }
        public float MACD { get; set; }
        public float MACD_Signal { get; set; }
        public float MACD_Hist { get; set; }
        public float MACD_Hist_ChangeRate { get; set; } // (현재Hist-이전Hist)/|이전Hist| — 골크 예측용
        public float MACD_DeadCrossAngle { get; set; } // (MACD-Sig)[t]-(MACD-Sig)[t-1] — 음수→급하락
        public float MACD_GoldenCross { get; set; }   // 1=골든크로스 발생, 0=아님
        public float MACD_DeadCross { get; set; }      // 1=데드크로스 발생, 0=아님
        public float ATR { get; set; }

        // 추가 지표
        public float ADX { get; set; }
        public float PlusDI { get; set; }
        public float MinusDI { get; set; }
        public float Stoch_K { get; set; }
        public float Stoch_D { get; set; }

        // 2. 피보나치 레벨
        public float Fib_236 { get; set; }
        public float Fib_382 { get; set; }
        public float Fib_500 { get; set; }
        public float Fib_618 { get; set; }

        // 3. 엘리엇 파동
        public float ElliottWaveState { get; set; } // 1~5파동 상태 수치화
        [NoColumn] public int ElliottWaveStep { get; set; } // 1, 2, 3, 4, 5 파동 번호

        // 4. 이동평균 정렬 상태
        public float SMA_20 { get; set; }
        public float SMA_60 { get; set; }
        public float SMA_120 { get; set; }

        // [v4.6.2] 단타 보조지표 — 트레이딩뷰 검증된 단타 핵심
        public float EMA_9 { get; set; }                    // EMA 9 (단기)
        public float EMA_21 { get; set; }                   // EMA 21 (중기)
        public float EMA_50 { get; set; }                   // EMA 50 (장기)
        public float EMA_Cross_State { get; set; }          // 1=정배열(9>21>50), -1=역배열(9<21<50), 0=중립
        public float VWAP { get; set; }                     // 거래량 가중 평균가 (당일 누적)
        public float Price_VWAP_Distance_Pct { get; set; }  // (Close - VWAP) / VWAP * 100
        public float StochRSI_K { get; set; }               // Stochastic RSI %K
        public float StochRSI_D { get; set; }               // Stochastic RSI %D
        public float StochRSI_Cross { get; set; }           // 1=K>D 골든, -1=K<D 데드

        // ──────────────────── 파생 Features (AI 핵심) ────────────────────
        // 5. 정규화된 가격 파생 지표
        public float Price_Change_Pct { get; set; }        // (Close - Open) / Open * 100
        public float Price_To_BB_Mid { get; set; }          // (Close - BB_Mid) / BB_Mid * 100 (볼린저 밴드 이격도)
        public float BB_Width { get; set; }                  // (BB_Upper - BB_Lower) / BB_Mid * 100 (변동성)
        public float Price_To_SMA20_Pct { get; set; }       // (Close - SMA20) / SMA20 * 100 (MA 이격도)
        public float Candle_Body_Ratio { get; set; }        // |Close - Open| / (High - Low) (캔들 실체 비율)
        public float Upper_Shadow_Ratio { get; set; }       // 윗꼬리 비율
        public float Lower_Shadow_Ratio { get; set; }       // 아랫꼬리 비율
        public float Is15mBearishTail { get; set; }         // 15분봉 위꼬리 음봉 여부 (0 or 1)
        public float TrendAlignment { get; set; }           // 상위봉 추세 정렬 (1=하락, 0=중립, -1=상승)

        // 6. 거래량 분석
        public float Volume_Ratio { get; set; }              // 현재 거래량 / 20봉 평균 거래량
        public float Volume_Change_Pct { get; set; }         // (현재 거래량 - 이전 거래량) / 이전 거래량 * 100

        // 7. 피보나치 포지션
        public float Fib_Position { get; set; }              // 0~1 사이 (현재 가격이 Fib 0.236~0.618 어디인지)

        // 8. 추세 강도
        public float Trend_Strength { get; set; }            // SMA 정렬 상태 기반 (-1 ~ +1)
        public float RSI_Divergence { get; set; }           // RSI와 가격 방향 괴리

        // 9. 계단식 패턴 + 모멘텀 (v3.2.5)
        public float HigherLows_Count { get; set; }      // 연속 저점 상승 횟수 (0~5)
        public float LowerHighs_Count { get; set; }      // 연속 고점 하락 횟수 (0~5)
        public float Price_Momentum_30m { get; set; }    // 30분(6봉) 가격 변화율 %
        public float Bounce_From_Low_Pct { get; set; }   // 1시간 저점 대비 반등률 %
        public float Drop_From_High_Pct { get; set; }    // 1시간 고점 대비 하락률 %

        // 뉴스 감성
        public float SentimentScore { get; set; }

        // ──────────────────── Labels ────────────────────
        public bool Label { get; set; }                      // 기존 호환용 (다음 봉 상승 여부)

        // [NEW] 레버리지 기반 레이블 (핵심!)
        // 진입 후 10봉(50분) 이내: 목표가(+2.5%=ROE+50%)에 먼저 도달 → 1(Long Success)
        //                          손절가(-1.0%=ROE-20%)에 먼저 도달 → 0(Fail)
        public float LabelLong { get; set; }                 // LONG 성공 확률 (0 or 1)
        public float LabelShort { get; set; }                // SHORT 성공 확률 (0 or 1)
        public float LabelHold { get; set; }                 // HOLD (진입하지 않는 게 나은 구간, 1 or 0)

        // 기존 호환 (ML.NET에서 double은 Concatenate와 타입 충돌 가능 → [NoColumn])
        [NoColumn] public double BB_Upper { get; set; }
        [NoColumn] public double BB_Lower { get; set; }
    }

    public class ElliottPoints
    {
        public decimal P1 { get; set; } // 1파 고점
        public decimal P2 { get; set; } // 2파 저점
        public decimal P3 { get; set; } // 3파 고점 (진행 중일 땐 현재가 후보)
    }
    public class PredictionResult
    {
        [ColumnName("PredictedLabel")]
        public bool Prediction { get; set; } // 상승(True) / 하락(False)

        public float Probability { get; set; } // 확신도 (0.0 ~ 1.0)
        public float Score { get; set; }
    }

    // AI 예측 검증 대기 항목 (AIPredictionValidationService 호환)
    public class AIPrediction
    {
        public long Id { get; set; }
        public string Symbol { get; set; } = string.Empty;
        public decimal PriceAtPrediction { get; set; }
        public string PredictedDirection { get; set; } = string.Empty; // "UP" / "DOWN"
        public string ModelName { get; set; } = string.Empty;
        public float Confidence { get; set; }
    }

    // AI 모니터링: 예측 추적 레코드
    public class AIPredictionRecord
    {
        public DateTime Timestamp { get; set; }
        public string Symbol { get; set; } = string.Empty;
        public string ModelName { get; set; } = string.Empty;
        public decimal PredictedPrice { get; set; }
        public decimal ActualPrice { get; set; }
        public bool PredictedDirection { get; set; } // true=상승, false=하락
        public bool ActualDirection { get; set; }
        public float Confidence { get; set; }
        public bool IsCorrect { get; set; }
    }

    // AI 모델 성능 통계
    public class AIModelPerformance : INotifyPropertyChanged
    {
        private string _modelName = string.Empty;
        public string ModelName
        {
            get => _modelName;
            set { _modelName = value; OnPropertyChanged(); }
        }

        private double _accuracy;
        public double Accuracy
        {
            get => _accuracy;
            set { _accuracy = value; OnPropertyChanged(); }
        }

        private int _totalPredictions;
        public int TotalPredictions
        {
            get => _totalPredictions;
            set { _totalPredictions = value; OnPropertyChanged(); }
        }

        private int _correctPredictions;
        public int CorrectPredictions
        {
            get => _correctPredictions;
            set { _correctPredictions = value; OnPropertyChanged(); }
        }

        private double _avgConfidence;
        public double AvgConfidence
        {
            get => _avgConfidence;
            set { _avgConfidence = value; OnPropertyChanged(); }
        }

        private Brush _statusColor = Brushes.White;
        public Brush StatusColor
        {
            get => _statusColor;
            set { _statusColor = value; OnPropertyChanged(); }
        }

        public event PropertyChangedEventHandler? PropertyChanged;
        protected void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }

    // Removed PositionInfo - use TradingBot.Shared.Models.PositionInfo instead
    public class ExchangeInfo
    {
        public List<SymbolInfo> Symbols { get; set; } = new List<SymbolInfo>();
    }

    public class SymbolInfo
    {
        public string Name { get; set; } = string.Empty;
        public SymbolFilter? LotSizeFilter { get; set; }
        public SymbolFilter? PriceFilter { get; set; }
    }

    public class SymbolFilter
    {
        public decimal StepSize { get; set; }
        public decimal TickSize { get; set; }
    }
    // UI 업데이트를 위한 클래스
    public class StrategySignal
    {
        public string? Symbol { get; set; }
        public double RSI { get; set; }
        public float AIScore { get; set; }
        public string? Wave { get; set; }
        public string? BBStatus { get; set; }
        public string? Decision { get; set; } // 진입 가능 여부
    }
    // 상태 아이콘 열거형
    public enum PositionStatus { None, Monitoring, TakeProfitReady, Danger }

    public class MultiTimeframeViewModel : INotifyPropertyChanged
    {
        // [병목 해결] Brush 정적 캐시 — PropertyChanged("") 에서 매번 new SolidColorBrush 생성 방지
        private static readonly Brush s_closeStatusBgRed = Freeze(new SolidColorBrush(Color.FromRgb(185, 28, 28)));
        private static readonly Brush s_closeStatusBgSlate = Freeze(new SolidColorBrush(Color.FromRgb(51, 65, 85)));
        private static readonly Brush s_syncBgDbFail = s_closeStatusBgRed;
        private static readonly Brush s_syncBgExternalClose = Freeze(new SolidColorBrush(Color.FromRgb(30, 64, 175)));
        private static readonly Brush s_syncBgExternalPartial = Freeze(new SolidColorBrush(Color.FromRgb(180, 83, 9)));
        private static readonly Brush s_syncBgExternalIncrease = Freeze(new SolidColorBrush(Color.FromRgb(22, 101, 52)));
        private static readonly Brush s_syncBgExternalRestore = Freeze(new SolidColorBrush(Color.FromRgb(109, 40, 217)));
        private static readonly Brush s_syncBgDefault = s_closeStatusBgSlate;
        private static readonly Brush s_chartStrokeGreen = Freeze(new SolidColorBrush(Color.FromRgb(0, 230, 118)));
        private static readonly Brush s_chartStrokeRed = Freeze(new SolidColorBrush(Color.FromRgb(255, 82, 82)));
        private static readonly Brush s_chartFillGreen = Freeze(new SolidColorBrush(Color.FromArgb(30, 0, 230, 118)));
        private static readonly Brush s_chartFillRed = Freeze(new SolidColorBrush(Color.FromArgb(30, 255, 82, 82)));
        private static readonly Brush s_symbolColorGreen = Freeze(new SolidColorBrush(Color.FromRgb(0, 230, 118)));
        private static readonly Brush s_strategyBgBlue = Freeze(new SolidColorBrush(Color.FromRgb(37, 99, 235)));
        private static readonly Brush s_strategyBgRed = Freeze(new SolidColorBrush(Color.FromRgb(220, 38, 38)));
        private static readonly Brush s_decisionBgLong = s_strategyBgBlue;
        private static readonly Brush s_decisionBgShort = s_strategyBgRed;
        private static readonly Brush s_decisionBgWait = s_closeStatusBgSlate;
        private static readonly Brush s_rowBackground = Freeze(new SolidColorBrush(Color.FromRgb(22, 25, 37)));
        private static readonly HashSet<string> s_majorSymbols = new(StringComparer.OrdinalIgnoreCase)
        {
            "BTCUSDT", "ETHUSDT", "SOLUSDT", "XRPUSDT"
        };

        private static Brush Freeze(SolidColorBrush b) { b.Freeze(); return b; }

        private decimal _entryPrice;
        public decimal EntryPrice
        {
            get => _entryPrice;
            set { _entryPrice = value; OnPropertyChanged(nameof(EntryPrice)); OnPropertyChanged(nameof(ProfitRate)); OnPropertyChanged(nameof(ProfitColor)); OnPropertyChanged(nameof(PriceColor)); OnPropertyChanged(nameof(Status)); OnPropertyChanged(nameof(EntryMarginUsdt)); OnPropertyChanged(nameof(EntryNotionalUsdt)); OnPropertyChanged(nameof(ProfitUsdt)); }
        }
        private bool _isPositionActive;
        public bool IsPositionActive
        {
            get => _isPositionActive;
            set
            {
                _isPositionActive = value;
                OnPropertyChanged(nameof(IsPositionActive));
                OnPropertyChanged(nameof(SortPriority));
                OnPropertyChanged(nameof(PriceColor));
                OnPropertyChanged(nameof(Status));
                OnPropertyChanged(nameof(RiskSummary));
                OnPropertyChanged(nameof(DisplayDecision));
                OnPropertyChanged(nameof(EntryStatus));
                OnPropertyChanged(nameof(EntryStatusColor));
                OnPropertyChanged(nameof(EntryStatusIcon));
                OnPropertyChanged(nameof(EntryMarginUsdt));
                OnPropertyChanged(nameof(EntryNotionalUsdt));
                OnPropertyChanged(nameof(ProfitUsdt));
            }
        }

        private bool _isInPosition;
        public bool IsInPosition
        {
            get => _isInPosition;
            set { _isInPosition = value; OnPropertyChanged(nameof(IsInPosition)); OnPropertyChanged(nameof(SymbolColor)); }
        }
        private decimal _quantity;
        public decimal Quantity
        {
            get => _quantity;
            set
            {
                _quantity = value;
                OnPropertyChanged(nameof(Quantity));
                // 수량이 바뀌면 수익률 계산 방식(롱/숏)도 바뀔 수 있으므로 함께 통지
                OnPropertyChanged(nameof(ProfitRate));
                OnPropertyChanged(nameof(Status)); // 상태 아이콘도 갱신
                OnPropertyChanged(nameof(EntryMarginUsdt));
                OnPropertyChanged(nameof(EntryNotionalUsdt));
                OnPropertyChanged(nameof(ProfitUsdt));
            }
        }

        // 포지션 방향 ("LONG", "SHORT", "")
        private string _positionSide = "";
        public string PositionSide
        {
            get => _positionSide;
            set
            {
                if (_positionSide != value)
                {
                    _positionSide = value;
                    OnPropertyChanged(nameof(PositionSide));
                    OnPropertyChanged(nameof(PositionSideColor)); // 포지션 타입 색상도 갱신
                    OnPropertyChanged(nameof(ProfitRate)); // 방향이 바뀌면 ROI 재계산
                    OnPropertyChanged(nameof(Status)); // 상태 아이콘도 갱신
                }
            }
        }

        // 레버리지 (기본값 20배)
        public int Leverage { get; set; } = 20;
        // 1. 실제 데이터를 저장할 변수
        private double _profitPercent;

        // 2. UI에서 바인딩해서 사용하는 속성
        public double ProfitPercent
        {
            get => _profitPercent;
            set
            {
                value = SanitizeProfitPercent(value);

                if (_profitPercent != value)
                {
                    _profitPercent = value;
                    OnPropertyChanged(nameof(ProfitPercent));
                    OnPropertyChanged(nameof(ProfitRate));
                    OnPropertyChanged(nameof(ProfitUsdt));

                    OnPropertyChanged(nameof(ProfitColor));
                    OnPropertyChanged(nameof(ChartStroke));
                    OnPropertyChanged(nameof(ChartFill));
                    OnPropertyChanged(nameof(Status)); // 🔍, 💰, ⚠️ 아이콘 갱신
                }
            }
        }

        // 3. (중요) 기존에 ProfitRate를 참조하던 로직들을 ProfitPercent로 통합
        // 바이낸스/바이비트 표준 ROI 계산 (앱 표시 방식과 동일)
        public double ProfitRate
        {
            get
            {
                // 포지션이 활성화되어 있고 진입가가 있을 때만 계산
                if (!IsPositionActive || EntryPrice == 0 || LastPrice == 0)
                    return SanitizeProfitPercent(_profitPercent);

                // 바이낸스/바이비트 표준 ROI 계산:
                // LONG: ROI% = (Mark Price - Entry Price) / Entry Price × Leverage × 100
                // SHORT: ROI% = (Entry Price - Mark Price) / Entry Price × Leverage × 100

                decimal priceChange;
                if (string.Equals(PositionSide, "SHORT", StringComparison.OrdinalIgnoreCase))
                {
                    // 숏: 진입가 - 현재가 (가격이 내려가면 이익)
                    priceChange = EntryPrice - LastPrice;
                }
                else if (string.Equals(PositionSide, "LONG", StringComparison.OrdinalIgnoreCase) || string.IsNullOrEmpty(PositionSide))
                {
                    // 롱: 현재가 - 진입가 (가격이 올라가면 이익)
                    priceChange = LastPrice - EntryPrice;
                }
                else
                {
                    // 예상치 못한 값이면 기본값 반환
                    return SanitizeProfitPercent(_profitPercent);
                }

                double roi = (double)(priceChange / EntryPrice) * Leverage * 100;
                if (double.IsNaN(roi) || double.IsInfinity(roi))
                    return SanitizeProfitPercent(_profitPercent);

                // 계산된 값을 내부 필드에도 저장 (UI 업데이트를 위해)
                if (Math.Abs(_profitPercent - roi) > 0.001)
                {
                    _profitPercent = SanitizeProfitPercent(roi);
                }

                return SanitizeProfitPercent(roi);
            }
            set => ProfitPercent = value;
        }

        /// <summary>[v3.2.45] ROI 금액 (USDT) — 진입가 × 수량 × ROI%</summary>
        public string ProfitUsdt
        {
            get
            {
                if (!IsPositionActive || EntryPrice == 0 || Quantity == 0)
                    return "";
                decimal margin = EntryPrice * Math.Abs(Quantity) / Math.Max(1, Leverage);
                double pnl = (double)margin * ProfitRate / 100.0;
                if (double.IsNaN(pnl) || double.IsInfinity(pnl)) return "";
                return $"{pnl:+0.00;-0.00} USDT";
            }
        }

        /// <summary>[v5.10.87] 진입 마진 금액 (USDT) — 진입가 × 수량 / 레버리지</summary>
        public string EntryMarginUsdt
        {
            get
            {
                if (!IsPositionActive) return "";
                // [v5.20.5] race: WebSocket UserData lag 시 EntryPrice/Quantity 0 → "..." 표시 (0 USDT 오인 방지)
                if (EntryPrice == 0 || Quantity == 0)
                    return "...";
                decimal lev = Leverage > 0 ? Leverage : 10;  // leverage 미설정 시 10x 가정
                decimal margin = EntryPrice * Math.Abs(Quantity) / lev;
                if (margin <= 0) return "...";
                return $"${margin:N2}";
            }
        }

        /// <summary>[v5.10.87] 진입 명목금액 (Notional) = 진입가 × 수량</summary>
        public string EntryNotionalUsdt
        {
            get
            {
                if (!IsPositionActive || EntryPrice == 0 || Quantity == 0)
                    return "";
                decimal notional = EntryPrice * Math.Abs(Quantity);
                if (notional <= 0) return "";
                return $"${notional:N2}";
            }
        }

        private static double SanitizeProfitPercent(double value)
        {
            return double.IsNaN(value) || double.IsInfinity(value) ? 0d : value;
        }

        // 4. 수익률에 따른 색상 로직
        public Brush ProfitColor => ProfitPercent > 0 ? Brushes.LimeGreen :
                                    ProfitPercent < 0 ? Brushes.Crimson :
                                    Brushes.White;

        // [추가] 현재가 vs 진입가 비교 색상 (실시간) - LONG/SHORT 포지션 방향에 따라 색상 변경
        public Brush PriceColor
        {
            get
            {
                if (!IsPositionActive || EntryPrice == 0) return Brushes.White;

                // SHORT 포지션: 가격이 내려가면 수익 (녹색), 올라가면 손실 (빨강)
                if (string.Equals(PositionSide, "SHORT", StringComparison.OrdinalIgnoreCase))
                {
                    if (LastPrice < EntryPrice) return Brushes.LimeGreen;  // 가격 하락 = 수익
                    if (LastPrice > EntryPrice) return Brushes.Tomato;     // 가격 상승 = 손실
                }
                // LONG 포지션: 가격이 올라가면 수익 (녹색), 내려가면 손실 (빨강)
                else
                {
                    if (LastPrice > EntryPrice) return Brushes.LimeGreen;  // 가격 상승 = 수익
                    if (LastPrice < EntryPrice) return Brushes.Tomato;     // 가격 하락 = 손실
                }

                return Brushes.White;
            }
        }

        // [추가] 포지션 타입 색상 (LONG=파랑, SHORT=오렌지)
        public Brush PositionSideColor
        {
            get
            {
                if (!IsPositionActive) return Brushes.Gray;

                if (string.Equals(PositionSide, "LONG", StringComparison.OrdinalIgnoreCase))
                    return Brushes.DeepSkyBlue;  // 파랑 계열
                else if (string.Equals(PositionSide, "SHORT", StringComparison.OrdinalIgnoreCase))
                    return Brushes.Orange;       // 오렌지 계열

                return Brushes.Gray;
            }
        }

        private bool _hasCloseIncomplete;
        public bool HasCloseIncomplete
        {
            get => _hasCloseIncomplete;
            set
            {
                if (_hasCloseIncomplete != value)
                {
                    _hasCloseIncomplete = value;
                    OnPropertyChanged(nameof(HasCloseIncomplete));
                    OnPropertyChanged(nameof(CloseStatusText));
                    OnPropertyChanged(nameof(CloseStatusBackground));
                    OnPropertyChanged(nameof(CloseStatusForeground));
                    OnPropertyChanged(nameof(Status));
                }
            }
        }

        private string? _closeIncompleteDetail;
        public string? CloseIncompleteDetail
        {
            get => _closeIncompleteDetail;
            set
            {
                if (_closeIncompleteDetail != value)
                {
                    _closeIncompleteDetail = value;
                    OnPropertyChanged(nameof(CloseIncompleteDetail));
                    OnPropertyChanged(nameof(CloseStatusText));
                }
            }
        }

        private bool HasDbSyncFailure =>
            string.Equals(ExternalSyncStatus, "DB실패", StringComparison.OrdinalIgnoreCase);

        public string CloseStatusText => HasDbSyncFailure
            ? "DB불일치"
            : (HasCloseIncomplete && IsPositionActive ? "청산미완료" : "-");

        public Brush CloseStatusBackground => HasDbSyncFailure
            ? s_closeStatusBgRed
            : (HasCloseIncomplete && IsPositionActive
                ? s_closeStatusBgRed
                : s_closeStatusBgSlate);

        public Brush CloseStatusForeground => (HasDbSyncFailure || (HasCloseIncomplete && IsPositionActive))
            ? Brushes.White
            : Brushes.LightGray;

        private string? _externalSyncStatus;
        public string? ExternalSyncStatus
        {
            get => _externalSyncStatus;
            set
            {
                if (_externalSyncStatus != value)
                {
                    _externalSyncStatus = value;
                    OnPropertyChanged(nameof(ExternalSyncStatus));
                    OnPropertyChanged(nameof(SyncStatusText));
                    OnPropertyChanged(nameof(SyncStatusBackground));
                    OnPropertyChanged(nameof(SyncStatusForeground));
                    OnPropertyChanged(nameof(CloseStatusText));
                    OnPropertyChanged(nameof(CloseStatusBackground));
                    OnPropertyChanged(nameof(CloseStatusForeground));
                }
            }
        }

        private string? _externalSyncDetail;
        public string? ExternalSyncDetail
        {
            get => _externalSyncDetail;
            set
            {
                if (_externalSyncDetail != value)
                {
                    _externalSyncDetail = value;
                    OnPropertyChanged(nameof(ExternalSyncDetail));
                }
            }
        }

        public string SyncStatusText => string.IsNullOrWhiteSpace(ExternalSyncStatus) ? "-" : ExternalSyncStatus!;

        public Brush SyncStatusBackground
        {
            get
            {
                return ExternalSyncStatus switch
                {
                    "DB실패" => s_syncBgDbFail,
                    "외부청산" => s_syncBgExternalClose,
                    "외부부분" => s_syncBgExternalPartial,
                    "외부증가" => s_syncBgExternalIncrease,
                    "외부복원" => s_syncBgExternalRestore,
                    _ => s_syncBgDefault
                };
            }
        }

        public Brush SyncStatusForeground => string.IsNullOrWhiteSpace(ExternalSyncStatus)
            ? Brushes.LightGray
            : Brushes.White;

        // [Phase 7] Transformer 예측 결과
        private decimal _transformerPrice;
        public decimal TransformerPrice
        {
            get => _transformerPrice;
            set { _transformerPrice = value; OnPropertyChanged(nameof(TransformerPrice)); }
        }

        private double _transformerChange;
        public double TransformerChange
        {
            get => _transformerChange;
            set { _transformerChange = value; OnPropertyChanged(nameof(TransformerChange)); OnPropertyChanged(nameof(TransformerChangeColor)); }
        }

        public Brush TransformerChangeColor => TransformerChange > 0 ? Brushes.LimeGreen : (TransformerChange < 0 ? Brushes.Tomato : Brushes.Gray);

        // 5. 차트 색상 (ProfitPercent 기준)
        public Brush ChartStroke => ProfitPercent >= 0 ? s_chartStrokeGreen : s_chartStrokeRed;

        public Brush ChartFill => ProfitPercent >= 0 ? s_chartFillGreen : s_chartFillRed;

        // 6. 상태 아이콘 (ProfitPercent 기준)

        // 포지션 보유 중이면 심볼 색상을 다르게 표시 (예: 금색)
        public Brush SymbolColor => IsInPosition ? Brushes.Gold : s_symbolColorGreen; // 형광 초록
        private string? _symbol;
        public string? Symbol
        {
            get => _symbol;
            set
            {
                if (_symbol != value)
                {
                    _symbol = value;
                    OnPropertyChanged(nameof(Symbol));
                    OnPropertyChanged(nameof(SortPriority));
                }
            }
        }

        public int SortPriority
        {
            get
            {
                if (!string.IsNullOrWhiteSpace(Symbol) && s_majorSymbols.Contains(Symbol))
                    return 0;
                if (IsPositionActive)
                    return 1;
                return 2;
            }
        }
        private decimal _lastPrice;
        public decimal LastPrice
        {
            get => _lastPrice;
            set
            {
                if (_lastPrice != value)
                {
                    _lastPrice = value;
                    OnPropertyChanged(nameof(LastPrice));
                    // [병목 해결] 포지션 활성 시에만 파생 속성 갱신 (비활성 시 불필요)
                    if (IsPositionActive)
                    {
                        OnPropertyChanged(nameof(ProfitRate));
                        OnPropertyChanged(nameof(ProfitColor));
                        OnPropertyChanged(nameof(Status));
                        OnPropertyChanged(nameof(PriceColor));
                    }
                }
            }
        }

        // 모니터링 강화 항목
        public string? Trend4H { get; set; }       // 예: "UPSTREAK", "DOWNSTREAK"
        public SolidColorBrush TrendColor4H => Trend4H == "UP" ? Brushes.LimeGreen : Brushes.Red;

        public string? BBPosition { get; set; } // 예: "Upper", "Lower", "Mid"
        public string? VolumeRatio { get; set; }   // 예: "3.5x" (평균대비 거래량)

        private string _signalSource = "-";
        public string SignalSource
        {
            get => _signalSource;
            set { _signalSource = value; OnPropertyChanged(nameof(SignalSource)); }
        }

        private double _shortLongScore;
        public double ShortLongScore
        {
            get => _shortLongScore;
            set { _shortLongScore = value; OnPropertyChanged(nameof(ShortLongScore)); OnPropertyChanged(nameof(LsScoreText)); }
        }

        private double _shortShortScore;
        public double ShortShortScore
        {
            get => _shortShortScore;
            set { _shortShortScore = value; OnPropertyChanged(nameof(ShortShortScore)); OnPropertyChanged(nameof(LsScoreText)); }
        }

        private double _macdHist;
        public double MacdHist
        {
            get => _macdHist;
            set { _macdHist = value; OnPropertyChanged(nameof(MacdHist)); OnPropertyChanged(nameof(ShortTermContext)); }
        }

        private string _elliottTrend = "-";
        public string ElliottTrend
        {
            get => _elliottTrend;
            set { _elliottTrend = value; OnPropertyChanged(nameof(ElliottTrend)); OnPropertyChanged(nameof(ShortTermContext)); }
        }

        private string _maState = "-";
        public string MAState
        {
            get => _maState;
            set { _maState = value; OnPropertyChanged(nameof(MAState)); OnPropertyChanged(nameof(ShortTermContext)); }
        }

        private string _fibPosition = "-";
        public string FibPosition
        {
            get => _fibPosition;
            set { _fibPosition = value; OnPropertyChanged(nameof(FibPosition)); OnPropertyChanged(nameof(ShortTermContext)); }
        }

        private double _volumeRatioValue;
        public double VolumeRatioValue
        {
            get => _volumeRatioValue;
            set { _volumeRatioValue = value; OnPropertyChanged(nameof(VolumeRatioValue)); OnPropertyChanged(nameof(ShortTermContext)); }
        }

        public string LsScoreText => $"L:{ShortLongScore:F0} / S:{ShortShortScore:F0}";

        public string ShortTermContext =>
            $"E:{ElliottTrend} | MA:{MAState} | Fib:{FibPosition} | MACD:{MacdHist:F3} | Vol:{VolumeRatioValue:F2}x";

        public string? StrategyName { get; set; }  // "Major Scalp" or "Pump Breakout"
        public Brush StrategyBg
        {
            get
            {
                if (StrategyName == "Major Scalp") return s_strategyBgBlue; // 진한 파랑
                if (StrategyName == "Pump Breakout") return s_strategyBgRed; // 진한 빨강
                return Brushes.Gray;
            }
        }
        public double RSI_4H { get; set; }
        private double _rsi1h;
        public double RSI_1H
        {
            get => _rsi1h;
            set { _rsi1h = value; OnPropertyChanged(nameof(RSI_1H)); }
        }
        private float _aiScore; // Changed from int to float
        public float AIScore
        {
            get => _aiScore;
            set
            {
                if (_aiScore != value)
                {
                    _aiScore = value;
                    // 1. 점수 자체를 업데이트
                    OnPropertyChanged(nameof(AIScore));

                    // 2. 중요: 점수에 따라 결정(Decision)과 배경색도 변하므로 함께 갱신 알림
                    OnPropertyChanged(nameof(Decision));
                    OnPropertyChanged(nameof(DecisionBg));
                }
            }
        }

        private DateTime? _aiScoreUpdatedAt;
        public DateTime? AIScoreUpdatedAt
        {
            get => _aiScoreUpdatedAt;
            set
            {
                if (_aiScoreUpdatedAt != value)
                {
                    _aiScoreUpdatedAt = value;
                    OnPropertyChanged(nameof(AIScoreUpdatedAt));
                    OnPropertyChanged(nameof(AIScoreUpdatedText));
                }
            }
        }

        public string AIScoreUpdatedText => AIScoreUpdatedAt?.ToString("HH:mm:ss") ?? "-";

        public void TouchAIScoreUpdatedAt()
        {
            AIScoreUpdatedAt = DateTime.Now;
        }

        public Brush ScoreColor
        {
            get
            {
                if (AIScore >= 90) return Brushes.Crimson;    // 90점 이상: 진한 빨강
                if (AIScore >= 75) return Brushes.OrangeRed;  // 75점 이상: 주황색
                if (AIScore >= 50) return Brushes.Gold;       // 50점 이상: 금색
                return Brushes.White;                         // 기본: 흰색
            }
        }
        public string? BB_Status { get; set; }
        // AIScore는 이미 0-100 스케일이므로 *100 제거
        public string AIScoreText => $"{AIScore:F1}%";

        // ═══ AI 진입 예측 확률 (DoubleCheck Gate) ═══
        private float _aiEntryProb = -1f; // -1 = 미계산
        public float AiEntryProb
        {
            get => _aiEntryProb;
            set
            {
                if (Math.Abs(_aiEntryProb - value) > 0.001f)
                {
                    _aiEntryProb = value;
                    OnPropertyChanged(nameof(AiEntryProb));
                    OnPropertyChanged(nameof(AiEntryProbText));
                    OnPropertyChanged(nameof(AiEntryProbColor));
                }
            }
        }

        /// <summary>진입 확률 표시 텍스트 (ML+TF 평균)</summary>
        public string AiEntryProbText
        {
            get
            {
                if (_aiEntryProb < 0) return "-";
                return $"{_aiEntryProb:P0}";
            }
        }

        private DateTime? _aiEntryForecastTime;
        public DateTime? AiEntryForecastTime
        {
            get => _aiEntryForecastTime;
            set
            {
                if (_aiEntryForecastTime != value)
                {
                    _aiEntryForecastTime = value;
                    OnPropertyChanged(nameof(AiEntryForecastTime));
                    OnPropertyChanged(nameof(AiEntryForecastTimeText));
                    OnPropertyChanged(nameof(AiEntryForecastDetailText));
                }
            }
        }

        private int? _aiEntryForecastOffsetMinutes;
        public int? AiEntryForecastOffsetMinutes
        {
            get => _aiEntryForecastOffsetMinutes;
            set
            {
                if (_aiEntryForecastOffsetMinutes != value)
                {
                    _aiEntryForecastOffsetMinutes = value;
                    OnPropertyChanged(nameof(AiEntryForecastOffsetMinutes));
                    OnPropertyChanged(nameof(AiEntryForecastTimeText));
                    OnPropertyChanged(nameof(AiEntryForecastDetailText));
                }
            }
        }

        public string AiEntryForecastTimeText
        {
            get
            {
                if (_aiEntryProb < 0) return "-";
                if (!_aiEntryForecastTime.HasValue) return "대기";
                if (!_aiEntryForecastOffsetMinutes.HasValue || _aiEntryForecastOffsetMinutes.Value <= 1) return "지금";
                return _aiEntryForecastTime.Value.ToString("HH:mm");
            }
        }

        public string AiEntryForecastDetailText
        {
            get
            {
                if (_aiEntryProb < 0) return string.Empty;
                if (!_aiEntryForecastTime.HasValue) return $"{_aiEntryProb:P0} · 대기";

                if (_aiEntryProb < 0.35f)
                {
                    if (!_aiEntryForecastOffsetMinutes.HasValue || _aiEntryForecastOffsetMinutes.Value <= 1) return $"관망 · {_aiEntryProb:P0}";
                    return $"관망 · {_aiEntryProb:P0} · +{_aiEntryForecastOffsetMinutes.Value}m";
                }

                if (!_aiEntryForecastOffsetMinutes.HasValue || _aiEntryForecastOffsetMinutes.Value <= 1) return $"{_aiEntryProb:P0} · 즉시";
                return $"{_aiEntryProb:P0} · +{_aiEntryForecastOffsetMinutes.Value}m";
            }
        }

        /// <summary>진입 확률에 따른 색상 (높을수록 녹색)</summary>
        public Brush AiEntryProbColor
        {
            get
            {
                if (_aiEntryProb < 0) return Brushes.Gray;
                if (_aiEntryProb >= 0.7f) return Brushes.LimeGreen;
                if (_aiEntryProb >= 0.5f) return Brushes.Gold;
                if (_aiEntryProb >= 0.3f) return Brushes.Orange;
                return Brushes.OrangeRed;
            }
        }

        private DateTime? _aiEntryProbUpdatedAt;
        public DateTime? AiEntryProbUpdatedAt
        {
            get => _aiEntryProbUpdatedAt;
            set { _aiEntryProbUpdatedAt = value; OnPropertyChanged(nameof(AiEntryProbUpdatedAt)); OnPropertyChanged(nameof(AiEntryProbUpdatedText)); }
        }
        public string AiEntryProbUpdatedText => AiEntryProbUpdatedAt?.ToString("HH:mm") ?? "";

        private string? _decision;
        public string? Decision
        {
            get => _decision;
            set { _decision = value; OnPropertyChanged(nameof(Decision)); OnPropertyChanged(nameof(DisplayDecision)); OnPropertyChanged(nameof(DecisionBg)); }
        }

        // RSI 값에 따른 동적 색상
        public Brush RSI_Color_4H => RSI_4H <= 30 ? Brushes.SkyBlue : (RSI_4H >= 70 ? Brushes.OrangeRed : Brushes.LightGray);

        // 결정 사항에 따른 배경색 (BUY=초록, SELL=빨강)
        private static readonly SolidColorBrush s_decisionBgActive = new(Color.FromRgb(0x1B, 0x5E, 0x20)); // 진한 초록 (진행중)

        public Brush DecisionBg
        {
            get
            {
                // [v3.2.37] 포지션 활성 시 진한 초록 배경
                if (IsPositionActive)
                    return s_decisionBgActive;

                if (string.IsNullOrEmpty(Decision)) return Brushes.Transparent;

                if (Decision.Contains("LONG"))
                    return s_decisionBgLong;

                if (Decision.Contains("SHORT"))
                    return s_decisionBgShort;

                return s_decisionBgWait;
            }
        }

        // Display용 Decision (포지션 활성화 시 "진행중" 표시)
        public string DisplayDecision => IsPositionActive ? "진행중" : (Decision ?? "-");

        // [병목 해결] 배치 업데이트 지원 - BeginUpdate() / EndUpdate() 사이에서는 PropertyChanged 억제
        private int _updateSuspendCount;
        private readonly HashSet<string> _pendingPropertyChanges = new(StringComparer.Ordinal);

        /// <summary>배치 업데이트 시작. PropertyChanged 이벤트가 억제됩니다.</summary>
        public void BeginUpdate() { _updateSuspendCount++; }

        /// <summary>배치 업데이트 종료. 보류 중인 변경이 있으면 한 번에 전체 Refresh를 발생시킵니다.</summary>
        public void EndUpdate()
        {
            if (_updateSuspendCount > 0) _updateSuspendCount--;
            if (_updateSuspendCount == 0 && _pendingPropertyChanges.Count > 0)
            {
                var pendingNames = new List<string>(_pendingPropertyChanges);
                _pendingPropertyChanges.Clear();

                foreach (var propertyName in pendingNames)
                    PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
            }
        }

        public event PropertyChangedEventHandler? PropertyChanged;
        protected void OnPropertyChanged(string name)
        {
            if (_updateSuspendCount > 0)
            {
                if (!string.IsNullOrWhiteSpace(name))
                    _pendingPropertyChanges.Add(name);
                return;
            }
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        }

        // 전체 행의 기본 배경색 (매우 어두운 네이비/블랙)
        public Brush RowBackground => s_rowBackground;
        private decimal _targetPrice;
        public decimal TargetPrice
        {
            get => _targetPrice;
            set
            {
                _targetPrice = value;
                OnPropertyChanged(nameof(TargetPrice));
                OnPropertyChanged(nameof(ExitStrategySummary));
                OnPropertyChanged(nameof(RiskSummary));
            }
        }

        private decimal _stopLossPrice;
        public decimal StopLossPrice
        {
            get => _stopLossPrice;
            set
            {
                _stopLossPrice = value;
                OnPropertyChanged(nameof(StopLossPrice));
                OnPropertyChanged(nameof(ExitStrategySummary));
                OnPropertyChanged(nameof(RiskSummary));
            }
        }

        private decimal _trailingStopPrice;
        public decimal TrailingStopPrice
        {
            get => _trailingStopPrice;
            set
            {
                _trailingStopPrice = value;
                OnPropertyChanged(nameof(TrailingStopPrice));
                OnPropertyChanged(nameof(RiskSummary));
            }
        }
        // 1. 감시 가격 요약 (예: "TP: 2.5% | SL: -1.5%")
        public string ExitStrategySummary => IsPositionActive
            ? $"TP: {TargetPrice:F2} | SL: {StopLossPrice:F2}"
            : "-";

        // 2. 상태 아이콘 결정 로직
        public PositionStatus Status
        {
            get
            {
                if (!IsPositionActive) return PositionStatus.None;
                if (HasCloseIncomplete) return PositionStatus.Danger;
                if (ProfitRate <= -1.0) return PositionStatus.Danger; // 손절 임박
                if (ProfitRate >= 2.0) return PositionStatus.TakeProfitReady; // 익절 구간 진입
                return PositionStatus.Monitoring; // 일반 감시 중
            }
        }

        // TargetPrice, StopLossPrice 등 값이 바뀔 때 OnPropertyChanged("ExitStrategySummary") 호출 필요
        
        // [NEW] 진입 상태 표시
        private string _entryStatus = "대기";
        public string EntryStatus
        {
            get => _entryStatus;
            set
            {
                if (_entryStatus != value)
                {
                    _entryStatus = value;
                    OnPropertyChanged(nameof(EntryStatus));
                    OnPropertyChanged(nameof(EntryStatusColor));
                    OnPropertyChanged(nameof(EntryStatusIcon));
                }
            }
        }

        // 진입 상태에 따른 색상
        public Brush EntryStatusColor
        {
            get
            {
                if (IsPositionActive) return Brushes.Gold;  // 진입 중
                if (_entryStatus.Contains("평가")) return Brushes.DeepSkyBlue;  // 진입 평가 중
                if (_entryStatus.Contains("감시")) return Brushes.LightSteelBlue; // 감시 중
                if (_entryStatus.Contains("RSI")) return Brushes.Orange;  // RSI 부족
                if (_entryStatus.Contains("AI")) return Brushes.OrangeRed;  // AI 부족
                if (_entryStatus.Contains("박스권")) return Brushes.SkyBlue;  // 박스권 대기
                if (_entryStatus.Contains("게이트")) return Brushes.Red;  // 게이트 차단
                if (_entryStatus.Contains("볼륨")) return Brushes.Yellow;  // 볼륨 부족
                if (_entryStatus.Contains("대기")) return Brushes.LightGray;  // 일반 대기
                return Brushes.White;
            }
        }

        // 상태 아이콘
        public string EntryStatusIcon
        {
            get
            {
                if (IsPositionActive) return "🟢";  // 진녹색 원 - 진입 중
                if (_entryStatus.Contains("평가")) return "🔍";  // 진입 평가 중
                if (_entryStatus.Contains("감시")) return "📡";  // 감시 중
                if (_entryStatus.Contains("RSI")) return "🟡";  // 노란색 원 - RSI 부족
                if (_entryStatus.Contains("AI")) return "🔴";  // 빨간색 원 - AI 부족
                if (_entryStatus.Contains("박스권")) return "🔵";  // 파란색 원 - 박스권
                if (_entryStatus.Contains("게이트")) return "⛔";  // 진입 금지
                if (_entryStatus.Contains("볼륨")) return "🟠";  // 주황색 원
                return "⏸️";  // 일시정지 - 대기
            }
        }

        // 리스크 요약 (SL/TP/트레일링스탑 가격)
        public string RiskSummary
        {
            get
            {
                if (!IsPositionActive) return "-";

                // [v3.2.39] 가격 기반 소수점 자동 결정
                string FmtPrice(decimal p) => p >= 100 ? p.ToString("F2") : p >= 1 ? p.ToString("F4") : p >= 0.01m ? p.ToString("F6") : p.ToString("F8");

                var sl = StopLossPrice > 0 ? $"SL:{FmtPrice(StopLossPrice)}" : null;
                var tp = TargetPrice > 0 ? $"TP:{FmtPrice(TargetPrice)}" : null;
                var ts = TrailingStopPrice > 0 ? $"TS:{FmtPrice(TrailingStopPrice)}" : null;

                // 트레일링스탑이 활성화되면 SL 대신 TS 표시 (TS가 실질적 손절가)
                if (ts != null)
                {
                    return tp != null ? $"{ts} | {tp}" : ts;
                }

                if (sl != null && tp != null)
                    return $"{sl} | {tp}";
                if (sl != null) return sl;
                if (tp != null) return tp;
                return "-";
            }
        }

        // [NEW] ML/TF 확률 표시
        private float _mlProbability = -1f;
        public float MLProbability
        {
            get => _mlProbability;
            set
            {
                if (Math.Abs(_mlProbability - value) > 0.001f)
                {
                    _mlProbability = value;
                    OnPropertyChanged(nameof(MLProbability));
                    OnPropertyChanged(nameof(MLProbabilityText));
                }
            }
        }

        public string MLProbabilityText => _mlProbability > 0 ? $"ML: {_mlProbability:P0}" : "ML: 대기";

        // ML.NET 확률 요약
        public string MLTFSummary => _mlProbability <= 0 ? "-" : $"ML: {_mlProbability:P0}";
    }


    public class SymbolModel : INotifyPropertyChanged
    {
        private double _profitRate;
        public double ProfitRate
        {
            get => _profitRate;
            set
            {
                if (_profitRate != value)
                {
                    _profitRate = value;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(ProfitColor)); // 수익률이 변하면 색상도 변해야 함
                }
            }
        }

        // [추가] UI에서 바인딩할 색상 프로퍼티
        public Brush ProfitColor
        {
            get
            {
                if (ProfitRate > 0) return Brushes.LimeGreen;
                if (ProfitRate < 0) return Brushes.Tomato;
                return Brushes.White; // 0이거나 초기값일 때
            }
        }

        public event PropertyChangedEventHandler? PropertyChanged;
        protected void OnPropertyChanged([CallerMemberName] string? name = null) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name ?? string.Empty));
    }

    /// <summary>
    /// [v2.1.18] 지표 결합형 동적 익절 시스템
    /// 5개 지표(엘리엇, 피보나치, RSI, BB, MACD)의 신호를 통합하여 익절 스탑을 동적으로 조정
    /// </summary>
    public class TechnicalData
    {
        // 기본 가격 데이터
        public decimal CurrentPrice { get; set; }
        public decimal HighestPrice { get; set; }
        public decimal LowestPrice { get; set; }

        // ATR (Average True Range)
        public decimal Atr { get; set; }
        public decimal AtrMultiplier { get; set; } = 1.5m;

        // 엘리엇 파동
        public bool IsWave5 { get; set; }              // 5파동 완성 신호
        public bool IsWaveExtended { get; set; }       // 파동 연장 중
        public int CurrentWaveCount { get; set; }

        // RSI (Relative Strength Index)
        public double Rsi { get; set; }
        public bool IsRsiOverbought => Rsi > 75;       // 과매수 (75 이상)
        public bool IsRsiExtreme => Rsi > 80;          // 극단적 과매수 (80 이상)

        // MACD (Moving Average Convergence Divergence)
        public double MacdLine { get; set; }
        public double SignalLine { get; set; }
        public double MacdHistogram { get; set; }
        public double PrevMacdHistogram { get; set; }
        public bool IsMacdHistogramDecreasing => MacdHistogram < PrevMacdHistogram;
        public bool IsMacdDeadCross => (MacdLine > SignalLine && MacdHistogram < 0);

        // 볼린저 밴드 (Bollinger Bands)
        public decimal MidBand { get; set; }           // 20일 SMA
        public decimal UpperBand { get; set; }         // SMA + 2*StdDev
        public decimal LowerBand { get; set; }         // SMA - 2*StdDev
        public bool HighWasAboveUpperBand { get; set; }
        public bool IsAboveUpperBand => CurrentPrice > UpperBand;
        public bool IsReturningToMidBand => HighWasAboveUpperBand && CurrentPrice < UpperBand;

        // 피보나치 (Fibonacci Extensions)
        public decimal EntryPrice { get; set; }
        public decimal Fibo1618 { get; set; }          // 1.618
        public decimal Fibo2618 { get; set; }          // 2.618
        public bool IsFibo1618Hit => CurrentPrice >= Fibo1618;
    }

    /// <summary>
    /// [v2.1.18] 고급 익절 신호
    /// 여러 지표의 신호를 종합하여 익절 추천 여부를 판단
    /// </summary>
    public class AdvancedExitSignal
    {
        public decimal RecommendedStopPrice { get; set; }
        public double TightModifier { get; set; } = 1.0;
        public bool ShouldTakeProfitNow { get; set; }
        public bool ShouldExecutePartialExit { get; set; }

        // 활성화된 신호 (로그용)
        public List<string> ActiveSignals { get; set; } = new();

        public string SignalSummary => string.Join(", ", ActiveSignals);
    }

    public class MarketData : INotifyPropertyChanged
    {
        public string? Symbol { get; set; }
        public decimal EntryPrice { get; set; }
        public bool IsPositionActive { get; set; }

        private double _profitRate;
        public double ProfitRate
        {
            get => _profitRate;
            set { _profitRate = value; OnPropertyChanged(); }
        }

        // UI에서 수익률에 따라 색상을 바꿀 때 사용 (예: 양수면 Green, 음수면 Red)
        public string ProfitColor => ProfitRate >= 0 ? "#00FF00" : "#FF0000";

        public event PropertyChangedEventHandler? PropertyChanged;
        protected void OnPropertyChanged([CallerMemberName] string? name = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name ?? string.Empty));
    }

    /// <summary>[v3.2.49] Performance 달력 일별 PnL 엔트리</summary>
    public class DayPnlEntry
    {
        public string Label { get; set; } = "";
        public decimal PnlUsdt { get; set; }
        public int TradeCount { get; set; }
        public bool IsProfit { get; set; }
        public DateTime Date { get; set; }

        public string DayLabel => Date != default ? Date.ToString("M/d (ddd)") : Label;
        public string PnlDisplay => PnlUsdt == 0 ? "$0" : $"${PnlUsdt:+#,##0.00;-#,##0.00}";
        public string TradeCountDisplay => TradeCount > 0 ? $"{TradeCount}건" : "-";

        // [v3.7.0] 다크 테마 최적화 색상
        public System.Windows.Media.Brush PnlColor => PnlUsdt > 0
            ? new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x7E, 0xE7, 0x87)) // GitHub green
            : PnlUsdt < 0 ? new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xFF, 0x7B, 0x72)) // GitHub red
            : new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x48, 0x4F, 0x58));

        public System.Windows.Media.Brush CellBackground => PnlUsdt > 0
            ? new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromArgb(40, 0x7E, 0xE7, 0x87))
            : PnlUsdt < 0 ? new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromArgb(40, 0xFF, 0x7B, 0x72))
            : new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromArgb(15, 0x8B, 0x94, 0x9E));
    }
}
