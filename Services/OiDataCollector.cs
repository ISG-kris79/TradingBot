using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Binance.Net.Clients;
using Binance.Net.Enums;
using Binance.Net.Interfaces.Clients;
using TradingBot.Models;

namespace TradingBot.Services
{
    /// <summary>
    /// 바이낸스 선물 OI(미결제약정) / 펀딩비 데이터를 수집하여
    /// Transformer 학습 데이터로 변환하는 모듈.
    /// 
    /// 핵심 기능:
    /// 1. 과거 OI 히스토리 수집 (fapi/futures/data/openInterestHist)
    /// 2. 실시간 OI 스냅샷 캐시 (5분 주기)
    /// 3. 펀딩비 수집 (fapi/v1/fundingRate)
    /// 4. OI 변화율 계산 (5분 기준)
    /// </summary>
    public class OiDataCollector : IDisposable
    {
        private readonly HttpClient _httpClient;
        private readonly IBinanceRestClient _client;

        // 심볼별 OI 히스토리 캐시 (최근 500건)
        private readonly ConcurrentDictionary<string, List<OiSnapshot>> _oiCache = new();
        // 심볼별 최신 펀딩비
        private readonly ConcurrentDictionary<string, decimal> _fundingRateCache = new();

        private CancellationTokenSource? _cts;
        private Task? _collectionTask;

        public event Action<string>? OnLog;

        public OiDataCollector(IBinanceRestClient client)
        {
            _client = client;
            _httpClient = new HttpClient
            {
                BaseAddress = new Uri("https://fapi.binance.com"),
                Timeout = TimeSpan.FromSeconds(10)
            };
        }

        #region [ 과거 OI 데이터 수집 ]

        /// <summary>
        /// 과거 OI 히스토리를 수집합니다.
        /// period: 5m, 15m, 30m, 1h, 2h, 4h, 6h, 12h, 1d
        /// limit: 최대 500
        /// </summary>
        public async Task<List<OiSnapshot>> GetHistoricalOiAsync(
            string symbol, string period = "5m", int limit = 500, CancellationToken token = default)
        {
            try
            {
                string url = $"/futures/data/openInterestHist?symbol={symbol}&period={period}&limit={limit}";
                var response = await _httpClient.GetStringAsync(url, token);
                var rawData = JsonSerializer.Deserialize<List<BinanceOiHistoryResponse>>(response);

                if (rawData == null || rawData.Count == 0)
                    return new List<OiSnapshot>();

                var result = new List<OiSnapshot>();
                for (int i = 0; i < rawData.Count; i++)
                {
                    var item = rawData[i];
                    double oi = double.TryParse(item.SumOpenInterest, out var v) ? v : 0;
                    double oiValue = double.TryParse(item.SumOpenInterestValue, out var v2) ? v2 : 0;

                    // OI 변화율 계산 (이전 대비)
                    double oiChangePct = 0;
                    if (i > 0)
                    {
                        double prevOi = double.TryParse(rawData[i - 1].SumOpenInterest, out var prev) ? prev : 0;
                        if (prevOi > 0)
                            oiChangePct = (oi - prevOi) / prevOi * 100;
                    }

                    result.Add(new OiSnapshot
                    {
                        Symbol = symbol,
                        Timestamp = DateTimeOffset.FromUnixTimeMilliseconds(item.Timestamp).UtcDateTime,
                        OpenInterest = oi,
                        OpenInterestValue = oiValue,
                        OiChangePct = oiChangePct
                    });
                }

                // 캐시 업데이트
                _oiCache[symbol] = result;

                OnLog?.Invoke($"📊 [OI] {symbol} 히스토리 수집 완료: {result.Count}건 (period={period})");
                return result;
            }
            catch (Exception ex)
            {
                OnLog?.Invoke($"⚠️ [OI] {symbol} 히스토리 수집 실패: {ex.Message}");
                return new List<OiSnapshot>();
            }
        }

        /// <summary>
        /// 현재 OI를 조회합니다 (실시간 스냅샷).
        /// </summary>
        public async Task<OiSnapshot?> GetCurrentOiAsync(string symbol, CancellationToken token = default)
        {
            try
            {
                var result = await _client.UsdFuturesApi.ExchangeData.GetOpenInterestAsync(symbol, token);
                if (!result.Success || result.Data == null)
                    return null;

                double currentOi = (double)result.Data.OpenInterest;

                // 이전 캐시와 비교하여 변화율 계산
                double oiChangePct = 0;
                if (_oiCache.TryGetValue(symbol, out var history) && history.Count > 0)
                {
                    var lastOi = history.Last().OpenInterest;
                    if (lastOi > 0)
                        oiChangePct = (currentOi - lastOi) / lastOi * 100;
                }

                return new OiSnapshot
                {
                    Symbol = symbol,
                    Timestamp = DateTime.UtcNow,
                    OpenInterest = currentOi,
                    OpenInterestValue = 0, // 실시간 API에서는 Value 미제공
                    OiChangePct = oiChangePct
                };
            }
            catch (Exception ex)
            {
                OnLog?.Invoke($"⚠️ [OI] {symbol} 현재 OI 조회 실패: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// 현재 펀딩비를 조회합니다.
        /// </summary>
        public async Task<decimal> GetCurrentFundingRateAsync(string symbol, CancellationToken token = default)
        {
            try
            {
                var result = await _client.UsdFuturesApi.ExchangeData.GetFundingRatesAsync(symbol, limit: 1, ct: token);
                if (result.Success && result.Data != null && result.Data.Any())
                {
                    var rate = result.Data.First().FundingRate;
                    _fundingRateCache[symbol] = rate;
                    return rate;
                }
                return _fundingRateCache.GetValueOrDefault(symbol, 0m);
            }
            catch
            {
                return _fundingRateCache.GetValueOrDefault(symbol, 0m);
            }
        }

        #endregion

        #region [ OI 특정 시점 조회 (학습 데이터용) ]

        /// <summary>
        /// 특정 시점의 OI 변화율을 가져옵니다 (캐시에서 시간 기준 보간).
        /// </summary>
        public double GetOiChangeAtTime(string symbol, DateTime time)
        {
            if (!_oiCache.TryGetValue(symbol, out var history) || history.Count == 0)
                return 0;

            // 가장 가까운 시점의 데이터 찾기
            var closest = history
                .OrderBy(h => Math.Abs((h.Timestamp - time).TotalSeconds))
                .FirstOrDefault();

            return closest?.OiChangePct ?? 0;
        }

        /// <summary>
        /// 특정 시점의 OI 절대값을 가져옵니다.
        /// </summary>
        public double GetOiAtTime(string symbol, DateTime time)
        {
            if (!_oiCache.TryGetValue(symbol, out var history) || history.Count == 0)
                return 0;

            var closest = history
                .OrderBy(h => Math.Abs((h.Timestamp - time).TotalSeconds))
                .FirstOrDefault();

            return closest?.OpenInterest ?? 0;
        }

        /// <summary>
        /// 특정 시점의 펀딩비를 가져옵니다.
        /// </summary>
        public decimal GetFundingRateForSymbol(string symbol)
        {
            return _fundingRateCache.GetValueOrDefault(symbol, 0m);
        }

        #endregion

        #region [ 실시간 OI 수집 루프 (5분 주기) ]

        /// <summary>
        /// 백그라운드 OI/펀딩비 수집 시작 (5분 주기).
        /// </summary>
        public void StartCollection(IEnumerable<string> symbols)
        {
            _cts = new CancellationTokenSource();
            _collectionTask = Task.Run(async () =>
            {
                OnLog?.Invoke($"📊 [OI] 실시간 수집 시작 ({symbols.Count()}개 심볼)");

                while (!_cts.Token.IsCancellationRequested)
                {
                    try
                    {
                        foreach (var symbol in symbols)
                        {
                            if (_cts.Token.IsCancellationRequested) break;

                            // 1. 현재 OI 스냅샷
                            var snapshot = await GetCurrentOiAsync(symbol, _cts.Token);
                            if (snapshot != null)
                            {
                                if (!_oiCache.ContainsKey(symbol))
                                    _oiCache[symbol] = new List<OiSnapshot>();

                                _oiCache[symbol].Add(snapshot);

                                // 캐시 크기 제한 (최근 500건)
                                if (_oiCache[symbol].Count > 500)
                                    _oiCache[symbol] = _oiCache[symbol].TakeLast(500).ToList();
                            }

                            // 2. 현재 펀딩비
                            await GetCurrentFundingRateAsync(symbol, _cts.Token);

                            await Task.Delay(200, _cts.Token); // API 제한 고려
                        }
                    }
                    catch (OperationCanceledException) { break; }
                    catch (Exception ex)
                    {
                        OnLog?.Invoke($"⚠️ [OI] 수집 루프 오류: {ex.Message}");
                    }

                    // 5분 대기
                    await Task.Delay(TimeSpan.FromMinutes(5), _cts.Token);
                }

                OnLog?.Invoke("📊 [OI] 실시간 수집 중지됨");
            }, _cts.Token);
        }

        /// <summary>
        /// 수집 중지.
        /// </summary>
        public void StopCollection()
        {
            _cts?.Cancel();
        }

        #endregion

        public void Dispose()
        {
            _cts?.Cancel();
            _httpClient.Dispose();
        }
    }

    #region [ OI 데이터 모델 ]

    /// <summary>
    /// OI 스냅샷 (수집된 미결제약정 데이터)
    /// </summary>
    public class OiSnapshot
    {
        public string Symbol { get; set; } = "";
        public DateTime Timestamp { get; set; }
        public double OpenInterest { get; set; }
        public double OpenInterestValue { get; set; }
        public double OiChangePct { get; set; } // 이전 대비 변화율 (%)
    }

    /// <summary>
    /// 바이낸스 OI 히스토리 API 응답
    /// </summary>
    public class BinanceOiHistoryResponse
    {
        [JsonPropertyName("symbol")]
        public string Symbol { get; set; } = "";

        [JsonPropertyName("sumOpenInterest")]
        public string SumOpenInterest { get; set; } = "0";

        [JsonPropertyName("sumOpenInterestValue")]
        public string SumOpenInterestValue { get; set; } = "0";

        [JsonPropertyName("timestamp")]
        public long Timestamp { get; set; }
    }

    #endregion
}
