using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Binance.Net.Clients;
using Binance.Net.Objects.Models.Futures.Socket;
using CryptoExchange.Net.Objects;
using CryptoExchange.Net.Sockets;
using TradingBot.Models;

namespace TradingBot.Services
{
    /// <summary>
    /// [v4.2.0] 틱 밀도 모니터 — AggTrade WebSocket 기반
    ///
    /// 1. 초당 체결 횟수(TPS) 추적 → 평소 대비 5배+ = 급등 시작
    /// 2. 실시간 1분봉 조립 (REST 없이)
    /// 3. 매수/매도 비율 추적 (Taker Buy Volume)
    /// 4. 2단계 트리거: 틱 밀도 급증 시에만 AI 모델 가동
    /// </summary>
    public class TickDensityMonitor
    {
        // 심볼별 틱 데이터
        private readonly ConcurrentDictionary<string, SymbolTickState> _states = new(StringComparer.OrdinalIgnoreCase);
        private readonly ConcurrentDictionary<string, DateTime> _alertCooldown = new(StringComparer.OrdinalIgnoreCase);

        public event Action<string>? OnLog;
        /// <summary>급등 시작 신호: (symbol, tpsRatio, buyRatio, currentPrice)</summary>
        public event Action<string, double, double, decimal>? OnTickSurgeDetected;
        /// <summary>BB 스퀴즈 브레이크아웃: (symbol, currentPrice, bbWidth)</summary>
        public event Action<string, decimal, double>? OnSqueezeBreakout;

        private class SymbolTickState
        {
            // 틱 밀도 추적
            public int TickCountCurrentSec;
            public int TickCountPrevSec;
            public readonly Queue<int> TpsHistory = new(); // 최근 60초 TPS
            public DateTime CurrentSecond = DateTime.MinValue;

            // 매수/매도 추적
            public decimal BuyVolume;
            public decimal SellVolume;
            public DateTime VolumeResetTime = DateTime.MinValue;

            // 실시간 1분봉 조립
            public decimal M1Open, M1High, M1Low, M1Close;
            public decimal M1Volume;
            public DateTime M1StartTime = DateTime.MinValue;
            public readonly List<(decimal close, decimal volume, DateTime time)> RecentM1Candles = new();

            // BB 스퀴즈 감시
            public double LastBBWidth;
            public double MinBBWidth = double.MaxValue;
            public int SqueezeCount; // 연속 좁은 봉 카운트
        }

        /// <summary>AggTrade 데이터 처리 (WebSocket 콜백)</summary>
        public void ProcessAggTrade(string symbol, decimal price, decimal quantity, bool isBuyerMaker, DateTime tradeTime)
        {
            var state = _states.GetOrAdd(symbol, _ => new SymbolTickState());
            var now = DateTime.Now;
            int currentSec = now.Second + now.Minute * 60;

            // 초당 체결 횟수 카운트
            int stateSec = state.CurrentSecond.Second + state.CurrentSecond.Minute * 60;
            if (currentSec != stateSec)
            {
                // 새 초 시작
                state.TpsHistory.Enqueue(state.TickCountCurrentSec);
                while (state.TpsHistory.Count > 60) state.TpsHistory.Dequeue();
                state.TickCountPrevSec = state.TickCountCurrentSec;
                state.TickCountCurrentSec = 0;
                state.CurrentSecond = now;
            }
            state.TickCountCurrentSec++;

            // 매수/매도 분류
            if ((now - state.VolumeResetTime).TotalSeconds >= 30)
            {
                state.BuyVolume = 0;
                state.SellVolume = 0;
                state.VolumeResetTime = now;
            }
            decimal notional = price * quantity;
            if (!isBuyerMaker) // taker buy
                state.BuyVolume += notional;
            else
                state.SellVolume += notional;

            // 1분봉 조립
            int minuteKey = now.Hour * 60 + now.Minute;
            int stateMinKey = state.M1StartTime.Hour * 60 + state.M1StartTime.Minute;
            if (minuteKey != stateMinKey || state.M1StartTime == DateTime.MinValue)
            {
                // 이전 1분봉 완성
                if (state.M1StartTime != DateTime.MinValue && state.M1Volume > 0)
                {
                    state.RecentM1Candles.Add((state.M1Close, state.M1Volume, state.M1StartTime));
                    while (state.RecentM1Candles.Count > 30) state.RecentM1Candles.RemoveAt(0);

                    // BB 스퀴즈 체크 (20봉 이상)
                    CheckBBSqueeze(symbol, state);
                }
                // 새 1분봉 시작
                state.M1Open = price;
                state.M1High = price;
                state.M1Low = price;
                state.M1Close = price;
                state.M1Volume = 0;
                state.M1StartTime = now;
            }
            state.M1High = Math.Max(state.M1High, price);
            state.M1Low = Math.Min(state.M1Low, price);
            state.M1Close = price;
            state.M1Volume += notional;

            // 틱 밀도 급증 감지
            CheckTickSurge(symbol, state, price);
        }

        private void CheckTickSurge(string symbol, SymbolTickState state, decimal currentPrice)
        {
            if (state.TpsHistory.Count < 10) return; // 최소 10초 데이터
            if (_alertCooldown.TryGetValue(symbol, out var cd) && DateTime.Now < cd) return;

            double avgTps = state.TpsHistory.Average();
            if (avgTps < 1) return;

            double currentTps = state.TickCountCurrentSec;
            double tpsRatio = currentTps / avgTps;

            // TPS 5배+ 급증 = 급등 시작 신호
            if (tpsRatio >= 5.0 && currentTps >= 10)
            {
                double totalVol = (double)(state.BuyVolume + state.SellVolume);
                double buyRatio = totalVol > 0 ? (double)state.BuyVolume / totalVol : 0.5;

                // 매수 비율 60%+ = 매수세 우위
                if (buyRatio >= 0.55)
                {
                    _alertCooldown[symbol] = DateTime.Now.AddMinutes(5);
                    OnLog?.Invoke($"⚡ [틱급증] {symbol} TPS={currentTps:F0} ({tpsRatio:F1}x avg) 매수비={buyRatio:P0} → 급등 시작 신호");
                    OnTickSurgeDetected?.Invoke(symbol, tpsRatio, buyRatio, currentPrice);
                }
            }
        }

        private void CheckBBSqueeze(string symbol, SymbolTickState state)
        {
            if (state.RecentM1Candles.Count < 20) return;

            var closes = state.RecentM1Candles.TakeLast(20).Select(c => (double)c.close).ToList();
            double sma = closes.Average();
            double stdDev = Math.Sqrt(closes.Average(v => Math.Pow(v - sma, 2)));
            double bbUpper = sma + 2 * stdDev;
            double bbLower = sma - 2 * stdDev;
            double bbWidth = sma > 0 ? (bbUpper - bbLower) / sma * 100 : 0;

            state.LastBBWidth = bbWidth;
            if (bbWidth < state.MinBBWidth) state.MinBBWidth = bbWidth;

            // 스퀴즈 판정: BB 폭이 최소값의 1.2배 이내
            if (bbWidth <= state.MinBBWidth * 1.2 && bbWidth < 1.0)
            {
                state.SqueezeCount++;
            }
            else
            {
                // 스퀴즈 해제 + 브레이크아웃 체크
                if (state.SqueezeCount >= 5) // 5분+ 스퀴즈 후
                {
                    var lastCandle = state.RecentM1Candles[^1];
                    if ((double)lastCandle.close > bbUpper) // 상단 돌파
                    {
                        if (_alertCooldown.TryGetValue($"sq_{symbol}", out var sqCd) && DateTime.Now < sqCd) return;
                        _alertCooldown[$"sq_{symbol}"] = DateTime.Now.AddMinutes(10);

                        OnLog?.Invoke($"🔥 [BB스퀴즈] {symbol} {state.SqueezeCount}분 스퀴즈 후 상단 돌파 | BBWidth={bbWidth:F2}%");
                        OnSqueezeBreakout?.Invoke(symbol, lastCandle.close, bbWidth);
                    }
                }
                state.SqueezeCount = 0;
                // 최소 BB폭 서서히 리셋
                state.MinBBWidth = Math.Min(state.MinBBWidth * 1.01, bbWidth);
            }
        }

        /// <summary>감시 대상 심볼의 현재 상태 조회</summary>
        public (double tpsRatio, double buyRatio, int squeezeCount, double bbWidth) GetState(string symbol)
        {
            if (!_states.TryGetValue(symbol, out var state)) return (0, 0.5, 0, 0);
            double avgTps = state.TpsHistory.Count > 0 ? state.TpsHistory.Average() : 1;
            double tpsRatio = avgTps > 0 ? state.TickCountCurrentSec / avgTps : 0;
            double totalVol = (double)(state.BuyVolume + state.SellVolume);
            double buyRatio = totalVol > 0 ? (double)state.BuyVolume / totalVol : 0.5;
            return (tpsRatio, buyRatio, state.SqueezeCount, state.LastBBWidth);
        }
    }
}
