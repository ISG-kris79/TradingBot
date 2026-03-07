using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Binance.Net.Clients;
using Binance.Net.Enums;
using TradingBot.Models;

namespace TradingBot.Services
{
    /// <summary>
    /// 15분봉 120�?30?�간) 과거 ?�이?��? ?�볼별로 관리하???�비??
    /// </summary>
    public class FifteenMinCandleManager : IDisposable
    {
        private readonly ConcurrentDictionary<string, CircularBuffer<CandleData>> _candleBuffers = new();
        private readonly int _maxCandleCount = 120; // 15�?* 120 = 30?�간
        private readonly SemaphoreSlim _updateLock = new(1, 1);
        private readonly BinanceRestClient? _restClient;
        private bool _disposed = false;

        public event Action<string, List<CandleData>>? OnCandleBufferUpdated; // ?�볼, 최신 120�??�이??

        public FifteenMinCandleManager(BinanceRestClient? restClient = null)
        {
            _restClient = restClient;
        }

        /// <summary>
        /// ?�볼??15분봉 120�??�이??초기??(REST?�서 가?�오�?
        /// </summary>
        public async Task<bool> InitializeSymbolAsync(string symbol, CancellationToken ct = default)
        {
            if (_restClient == null)
            {
                LoggerService.Warning($"[FifteenMinCandleManager] RestClient가 null?��?�?{symbol} 초기??불�?");
                return false;
            }

            try
            {
                await _updateLock.WaitAsync(ct);

                var result = await _restClient.UsdFuturesApi.ExchangeData.GetKlinesAsync(
                    symbol: symbol,
                    interval: KlineInterval.FifteenMinutes,
                    limit: _maxCandleCount,
                    ct: ct);

                if (!result.Success || result.Data == null)
                {
                    LoggerService.Error($"[FifteenMinCandleManager] {symbol} 15분봉 조회 ?�패: {result.Error?.Message}");
                    return false;
                }

                var buffer = new CircularBuffer<CandleData>(_maxCandleCount);
                foreach (var kline in result.Data)
                {
                    var candle = new CandleData
                    {
                        Symbol = symbol,
                        Interval = "15m",
                        OpenTime = kline.OpenTime,
                        CloseTime = kline.CloseTime,
                        Open = kline.OpenPrice,
                        High = kline.HighPrice,
                        Low = kline.LowPrice,
                        Close = kline.ClosePrice,
                        Volume = (float)kline.Volume
                    };
                    buffer.Add(candle);
                }

                _candleBuffers[symbol] = buffer;
                LoggerService.Info($"[FifteenMinCandleManager] {symbol} 15분봉 {buffer.Count}�?초기???�료");
                return true;
            }
            catch (Exception ex)
            {
                LoggerService.Error($"[FifteenMinCandleManager] {symbol} 초기???�패: {ex.Message}");
                return false;
            }
            finally
            {
                _updateLock.Release();
            }
        }

        /// <summary>
        /// ??15분봉 캔들 추�? (?�시�??�데?�트??
        /// </summary>
        public async Task AddCandleAsync(string symbol, CandleData candle)
        {
            if (candle.Interval != "15m")
            {
                LoggerService.Warning($"[FifteenMinCandleManager] {symbol} 15m ?�외 Interval({candle.Interval}) 무시");
                return;
            }

            await _updateLock.WaitAsync();
            try
            {
                if (!_candleBuffers.ContainsKey(symbol))
                {
                    _candleBuffers[symbol] = new CircularBuffer<CandleData>(_maxCandleCount);
                }

                _candleBuffers[symbol].Add(candle);

                // ?�벤??발생 (?�측 ?�스?�에 ?�림)
                var snapshot = GetCandlesSnapshot(symbol);
                if (snapshot.Count >= 60) // 최소 60�??�상???�만 ?�측 가??
                {
                    OnCandleBufferUpdated?.Invoke(symbol, snapshot);
                }
            }
            finally
            {
                _updateLock.Release();
            }
        }

        /// <summary>
        /// ?�볼???�재 15분봉 120�??�냅??가?�오�?(최신??
        /// </summary>
        public List<CandleData> GetCandlesSnapshot(string symbol)
        {
            if (!_candleBuffers.TryGetValue(symbol, out var buffer))
                return new List<CandleData>();

            return buffer.ToList();
        }

        /// <summary>
        /// ?�볼???�재 캔들 개수
        /// </summary>
        public int GetCandleCount(string symbol)
        {
            return _candleBuffers.TryGetValue(symbol, out var buffer) ? buffer.Count : 0;
        }

        /// <summary>
        /// 관�?중인 ?�볼 목록
        /// </summary>
        public List<string> GetManagedSymbols()
        {
            return _candleBuffers.Keys.ToList();
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _updateLock?.Dispose();
        }
    }

    /// <summary>
    /// 고정 ?�기 ?�환 버퍼 (FIFO)
    /// </summary>
    public class CircularBuffer<T>
    {
        private readonly Queue<T> _queue;
        private readonly int _capacity;

        public int Count => _queue.Count;

        public CircularBuffer(int capacity)
        {
            _capacity = capacity;
            _queue = new Queue<T>(capacity);
        }

        public void Add(T item)
        {
            if (_queue.Count >= _capacity)
                _queue.Dequeue();
            _queue.Enqueue(item);
        }

        public List<T> ToList() => _queue.ToList();
    }
}
