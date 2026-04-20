using Binance.Net;
using Binance.Net.Clients;
using Binance.Net.Enums;
using Binance.Net.Interfaces;
using CryptoExchange.Net.Authentication;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using TradingBot.Models;
using TradingBot.Shared.Models;

namespace TradingBot.Services
{
    public class BinanceExchangeService : IExchangeService, IDisposable
    {
        private readonly BinanceRestClient _client;
        private bool _disposed = false;

        // [캐시] 심볼별 스텝사이즈/틱사이즈 - GetExchangeInfo 반복 호출 방지 (TTL 1시간)
        private static readonly ConcurrentDictionary<string, (decimal stepSize, decimal tickSize, DateTime cachedAt)> _symbolInfoCache = new();
        private static readonly TimeSpan _symbolInfoCacheTtl = TimeSpan.FromHours(1);

        // [Rate Limiter] 바이낸스 API Weight 제한 준수 (분당 2400 Weight)
        private static readonly SemaphoreSlim _apiThrottle = new(20, 20); // 동시 요청 20개 제한
        private static int _apiWeightUsed;
        private static DateTime _apiWeightResetTime = DateTime.UtcNow;

        public string ExchangeName => "Binance";

        // [추가] 로그 이벤트 (상위 레이어로 전달)
        public event Action<string>? OnLog;
        public event Action<string>? OnAlert;

        public bool IsTestnet { get; }

        // [v5.10.63] 직접 HttpClient — Binance.Net v12.8.1이 최신 /fapi/v1/algoOrder 미지원
        private readonly string _apiKey;
        private readonly string _apiSecret;
        private readonly HttpClient _http = new HttpClient();
        private string FuturesBase => IsTestnet ? "https://testnet.binancefuture.com" : "https://fapi.binance.com";

        public BinanceExchangeService(string apiKey, string apiSecret, bool useTestnet = false)
        {
            IsTestnet = useTestnet;
            _apiKey = apiKey ?? string.Empty;
            _apiSecret = apiSecret ?? string.Empty;
            _client = new BinanceRestClient(options =>
            {
                if (!string.IsNullOrWhiteSpace(apiKey) && !string.IsNullOrWhiteSpace(apiSecret))
                {
                    options.ApiCredentials = new ApiCredentials(apiKey, apiSecret);
                }

                if (useTestnet)
                {
                    options.Environment = BinanceEnvironment.Testnet;
                }
            });
        }

        /// <summary>[v5.10.63] HMAC-SHA256 서명 (Algo API 용)</summary>
        private string SignQuery(string query)
        {
            if (string.IsNullOrEmpty(_apiSecret)) return string.Empty;
            using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(_apiSecret));
            byte[] hash = hmac.ComputeHash(Encoding.UTF8.GetBytes(query));
            var sb = new StringBuilder(hash.Length * 2);
            foreach (byte b in hash) sb.Append(b.ToString("x2"));
            return sb.ToString();
        }

        /// <summary>[v5.10.63] Algo API POST — 성공 시 body 반환, 실패 시 null + OnLog</summary>
        private async Task<(bool ok, string body)> CallAlgoApiAsync(HttpMethod method, string endpoint, string queryParams, CancellationToken ct)
        {
            long ts = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            string qs = string.IsNullOrEmpty(queryParams) ? $"timestamp={ts}" : $"{queryParams}&timestamp={ts}";
            string sig = SignQuery(qs);
            string fullQs = $"{qs}&signature={sig}";

            HttpRequestMessage req;
            if (method == HttpMethod.Post)
            {
                req = new HttpRequestMessage(method, $"{FuturesBase}{endpoint}");
                req.Content = new StringContent(fullQs, Encoding.UTF8, "application/x-www-form-urlencoded");
            }
            else
            {
                req = new HttpRequestMessage(method, $"{FuturesBase}{endpoint}?{fullQs}");
            }
            req.Headers.Add("X-MBX-APIKEY", _apiKey);

            try
            {
                var resp = await _http.SendAsync(req, ct);
                string body = await resp.Content.ReadAsStringAsync(ct);
                if (!resp.IsSuccessStatusCode)
                {
                    OnLog?.Invoke($"⚠️ [AlgoAPI] {method} {endpoint} HTTP {(int)resp.StatusCode}: {body}");
                    return (false, body);
                }
                return (true, body);
            }
            catch (Exception ex)
            {
                OnLog?.Invoke($"⚠️ [AlgoAPI] {method} {endpoint} 예외: {ex.Message}");
                return (false, string.Empty);
            }
        }

        /// <summary>
        /// 선물 계좌 순 투입금 조회 (Transfer In - Transfer Out)
        /// Binance Income History API에서 TRANSFER 타입 내역 합산
        /// </summary>
        public async Task<decimal> GetNetTransferAmountAsync(CancellationToken ct = default)
        {
            try
            {
                decimal totalIn = 0m;
                decimal totalOut = 0m;
                DateTime startTime = DateTime.UtcNow.AddDays(-365); // 최근 1년

                // Income History에서 TRANSFER 타입 조회 (페이지네이션)
                while (true)
                {
                    var result = await _client.UsdFuturesApi.Account.GetIncomeHistoryAsync(
                        incomeType: "TRANSFER",
                        startTime: startTime,
                        limit: 1000,
                        ct: ct);

                    if (!result.Success || result.Data == null || !result.Data.Any())
                        break;

                    foreach (var income in result.Data)
                    {
                        if (income.Income > 0)
                            totalIn += income.Income;
                        else
                            totalOut += Math.Abs(income.Income);
                    }

                    // 1000건 미만이면 마지막 페이지
                    if (result.Data.Count() < 1000)
                        break;

                    // 다음 페이지: 마지막 건 이후부터
                    startTime = result.Data.Last().Timestamp.AddMilliseconds(1);
                }

                OnLog?.Invoke($"💰 [Transfer] 입금 합계: ${totalIn:N2}, 출금 합계: ${totalOut:N2}, 순 투입금: ${totalIn - totalOut:N2}");
                return totalIn - totalOut;
            }
            catch (Exception ex)
            {
                OnLog?.Invoke($"⚠️ [Transfer] 순 투입금 조회 실패: {ex.Message}");
                return 0m;
            }
        }

        /// <summary>
        /// [v5.1.8] WalletBalance 반환 (전체 잔고 — Equity 계산용)
        /// 기존: AvailableBalance (가용 = 전체 - 증거금) → $1,314 (실제 $8,044)
        /// 수정: WalletBalance (전체) → 정확한 Equity
        /// 가용 잔고가 필요한 곳은 GetAvailableBalanceAsync 사용
        /// </summary>
        public async Task<decimal> GetBalanceAsync(string asset, CancellationToken ct = default)
        {
            var result = await _client.UsdFuturesApi.Account.GetBalancesAsync(ct: ct);
            if (!result.Success)
            {
                OnLog?.Invoke($"❌ [계좌잔고] GetBalancesAsync 실패: {result.Error?.Message ?? "알 수 없는 오류"} (code={result.Error?.Code})");
                return 0;
            }

            var balance = result.Data.FirstOrDefault(b => b.Asset == asset);
            if (balance == null)
                OnLog?.Invoke($"⚠️ [계좌잔고] {asset} 잔고 항목 없음 — API 키 권한(Read) 또는 선물 계좌 확인 필요");
            return balance?.WalletBalance ?? 0;
        }

        /// <summary>[v5.1.8] 가용 잔고 (Available — 주문 가능 금액 체크용)</summary>
        public async Task<decimal> GetAvailableBalanceAsync(string asset, CancellationToken ct = default)
        {
            var result = await _client.UsdFuturesApi.Account.GetBalancesAsync(ct: ct);
            if (!result.Success)
            {
                OnLog?.Invoke($"❌ [가용잔고] GetBalancesAsync 실패: {result.Error?.Message ?? "알 수 없는 오류"} (code={result.Error?.Code})");
                return 0;
            }
            var balance = result.Data.FirstOrDefault(b => b.Asset == asset);
            return balance?.AvailableBalance ?? 0;
        }

        /// <summary>
        /// [v5.10.12] WalletBalance + AvailableBalance 단일 API 호출로 반환 — 대시보드용
        /// </summary>
        public async Task<(decimal Wallet, decimal Available)> GetBalancePairAsync(string asset, CancellationToken ct = default)
        {
            var result = await _client.UsdFuturesApi.Account.GetBalancesAsync(ct: ct);
            if (!result.Success)
            {
                OnLog?.Invoke($"❌ [계좌잔고] GetBalancesAsync 실패: {result.Error?.Message ?? "알 수 없는 오류"} (code={result.Error?.Code})");
                return (0, 0);
            }
            var balance = result.Data.FirstOrDefault(b => b.Asset == asset);
            if (balance == null)
                OnLog?.Invoke($"⚠️ [계좌잔고] {asset} 잔고 항목 없음 — API 키 권한(Read) 또는 선물 계좌 확인 필요");
            return (balance?.WalletBalance ?? 0, balance?.AvailableBalance ?? 0);
        }

        public async Task<decimal> GetPriceAsync(string symbol, CancellationToken ct = default)
        {
            var result = await _client.UsdFuturesApi.ExchangeData.GetPriceAsync(symbol, ct);
            return result.Success ? result.Data.Price : 0;
        }

        public async Task<bool> PlaceOrderAsync(string symbol, string side, decimal quantity, decimal? price = null, CancellationToken ct = default, bool reduceOnly = false)
        {
            // [FIX] 시뮬레이션 체크 제거 — 테스트넷 모드에서도 실제 API 호출 필요
            // 테스트넷 키로 초기화된 경우 _client가 테스트넷을 가리키므로 안전

            // [FIX] 부분청산도 ratio 계산 후 stepSize 안 맞을 수 있으므로 항상 보정
            {
                (decimal stepSize, decimal tickSize) = await GetSymbolPrecisionAsync(symbol, ct);
                if (stepSize > 0)
                    quantity = Math.Floor(quantity / stepSize) * stepSize;
                if (price.HasValue && tickSize > 0)
                    price = Math.Floor(price.Value / tickSize) * tickSize;
            }

            if (quantity <= 0)
            {
                OnLog?.Invoke($"❌ [Binance] 주문 수량이 0 이하입니다: {quantity}");
                return false;
            }

            OrderSide orderSide = side.ToUpper() == "BUY" ? OrderSide.Buy : OrderSide.Sell;
            FuturesOrderType orderType = price.HasValue ? FuturesOrderType.Limit : FuturesOrderType.Market;

            try
            {
                var result = await _client.UsdFuturesApi.Trading.PlaceOrderAsync(
                    symbol,
                    orderSide,
                    orderType,
                    quantity,
                    price,
                    timeInForce: price.HasValue ? TimeInForce.GoodTillCanceled : null,
                    reduceOnly: reduceOnly,
                    ct: ct);

                if (!result.Success)
                {
                    string errorDetail = $"Code={result.Error?.Code}, Msg={result.Error?.Message}";
                    OnLog?.Invoke($"❌ [Binance API] 주문 실패 - {symbol} {side} {quantity}");
                    OnLog?.Invoke($"   📋 오류 상세: {errorDetail}");

                    int? errCode = result.Error?.Code;
                    if (errCode == -2019)
                        OnAlert?.Invoke($"⚠️ [{symbol}] 잔고 부족 오류 - 사용 가능한 마진 확인 필요");
                    else if (errCode == -1021)
                        OnAlert?.Invoke($"⚠️ [{symbol}] 타임스탬프 오류 - 시스템 시간 동기화 확인 필요");
                    else if (errCode == -2022)
                        OnLog?.Invoke($"⚠️ [{symbol}] ReduceOnly 주문 거부 (-2022) - 서버사이드 Stop이 이미 체결됐을 가능성 있음");
                    else if (errCode == -4061)
                        OnLog?.Invoke($"⚠️ [{symbol}] positionSide 불일치 (-4061) - Hedge Mode 설정 확인 필요");
                    else if (errCode == -1003)
                        OnLog?.Invoke($"⚠️ [{symbol}] API Rate Limit 초과 (-1003)");

                    return false;
                }

                OnLog?.Invoke($"✅ [Binance] 주문 성공 - {symbol} {side} {quantity} (OrderId: {result.Data?.Id})");
                return true;
            }
            catch (Exception ex)
            {
                OnLog?.Invoke($"❌ [Binance 예외] 주문 중 예외 발생 - {symbol} {side} {quantity}");
                OnLog?.Invoke($"   🔥 예외: {ex.Message}");
                if (ex.InnerException != null)
                    OnLog?.Invoke($"   🔍 내부 예외: {ex.InnerException.Message}");
                return false;
            }
        }

        /// <summary>
        /// 심볼 정밀도 정보 조회 (캐시 우선, TTL 1시간)
        /// </summary>
        // [안전장치] ExchangeInfo 실패 시 사용할 심볼별 기본 stepSize
        private static readonly Dictionary<string, (decimal stepSize, decimal tickSize)> _fallbackPrecision = new()
        {
            ["BTCUSDT"]  = (0.001m,   0.10m),
            ["ETHUSDT"]  = (0.001m,   0.01m),
            ["XRPUSDT"]  = (0.1m,     0.0001m),
            ["SOLUSDT"]  = (0.1m,     0.010m),
            ["DOGEUSDT"] = (1m,       0.00001m),
            ["ADAUSDT"]  = (1m,       0.0001m),
            ["TRXUSDT"]  = (1m,       0.00001m),
            ["AVAXUSDT"] = (0.1m,     0.010m),
            ["LINKUSDT"] = (0.1m,     0.001m),
            ["DOTUSDT"]  = (0.1m,     0.001m),
            ["MATICUSDT"]= (1m,       0.0001m),
            ["BNBUSDT"]  = (0.01m,    0.010m),
            ["SUIUSDT"]  = (0.1m,     0.0001m),
            ["PEPEUSDT"] = (100m,     0.0000001m),
            ["SHIBUSDT"] = (1m,       0.000001m),
        };
        private static readonly (decimal stepSize, decimal tickSize) _defaultFallback = (0.001m, 0.01m);

        private async Task<(decimal stepSize, decimal tickSize)> GetSymbolPrecisionAsync(string symbol, CancellationToken ct)
        {
            if (_symbolInfoCache.TryGetValue(symbol, out var cached) && (DateTime.UtcNow - cached.cachedAt) < _symbolInfoCacheTtl)
                return (cached.stepSize, cached.tickSize);

            try
            {
                var exchangeInfo = await _client.UsdFuturesApi.ExchangeData.GetExchangeInfoAsync(ct: ct);
                if (!exchangeInfo.Success)
                {
                    OnLog?.Invoke($"⚠️ [Binance] ExchangeInfo 조회 실패: {exchangeInfo.Error?.Message}");
                    return GetFallbackPrecision(symbol);
                }

                var symbolData = exchangeInfo.Data.Symbols.FirstOrDefault(s => s.Name == symbol);
                if (symbolData == null)
                    return GetFallbackPrecision(symbol);

                decimal stepSize = symbolData.LotSizeFilter?.StepSize ?? 0;
                decimal tickSize = symbolData.PriceFilter?.TickSize ?? 0;

                if (stepSize <= 0 || tickSize <= 0)
                    return GetFallbackPrecision(symbol);

                _symbolInfoCache[symbol] = (stepSize, tickSize, DateTime.UtcNow);
                return (stepSize, tickSize);
            }
            catch (Exception ex)
            {
                OnLog?.Invoke($"⚠️ [Binance] 심볼 정밀도 조회 예외: {ex.Message}");
                return GetFallbackPrecision(symbol);
            }
        }

        private (decimal stepSize, decimal tickSize) GetFallbackPrecision(string symbol)
        {
            if (_fallbackPrecision.TryGetValue(symbol, out var fb))
            {
                OnLog?.Invoke($"🔧 [{symbol}] 폴백 stepSize 사용: {fb.stepSize}");
                return fb;
            }
            OnLog?.Invoke($"🔧 [{symbol}] 기본 폴백 stepSize 사용: {_defaultFallback.stepSize}");
            return _defaultFallback;
        }

        /// <summary>[v5.10.63] STOP_MARKET — Binance 2025-12-09 이관 후 /fapi/v1/algoOrder 사용</summary>
        public async Task<(bool Success, string OrderId)> PlaceStopOrderAsync(string symbol, string side, decimal quantity, decimal stopPrice, CancellationToken ct = default)
        {
            try
            {
                (decimal stepSize, decimal tickSize) = await GetSymbolPrecisionAsync(symbol, ct);
                if (stepSize > 0) quantity = Math.Floor(quantity / stepSize) * stepSize;
                if (tickSize > 0) stopPrice = Math.Floor(stopPrice / tickSize) * tickSize;

                if (quantity <= 0)
                {
                    MainWindow.Instance?.AddLog($"❌ [SL] {symbol} STOP_MARKET 실패: quantity={quantity}");
                    return (false, string.Empty);
                }
                if (stopPrice <= 0)
                {
                    MainWindow.Instance?.AddLog($"❌ [SL] {symbol} STOP_MARKET 실패: stopPrice={stopPrice}");
                    return (false, string.Empty);
                }

                // Algo Order API 파라미터: algoType=CONDITIONAL, type=STOP_MARKET, triggerPrice
                string qs = $"symbol={symbol}&side={side.ToUpper()}&algoType=CONDITIONAL&type=STOP_MARKET&quantity={quantity}&triggerPrice={stopPrice}&reduceOnly=true";
                var (ok, body) = await CallAlgoApiAsync(HttpMethod.Post, "/fapi/v1/algoOrder", qs, ct);
                if (!ok)
                {
                    MainWindow.Instance?.AddLog($"❌ [SL] {symbol} algoOrder 실패: {body}");
                    MainWindow.Instance?.AddAlert($"⚠️ [SL] {symbol} 손절 등록 실패");
                    return (false, string.Empty);
                }

                using var doc = JsonDocument.Parse(body);
                long algoId = doc.RootElement.GetProperty("algoId").GetInt64();
                MainWindow.Instance?.AddLog($"✅ [SL] {symbol} STOP_MARKET 등록 | {side} qty={quantity} trigger=${stopPrice} algoId={algoId}");
                return (true, algoId.ToString());
            }
            catch (Exception ex)
            {
                MainWindow.Instance?.AddLog($"❌ [SL] {symbol} STOP_MARKET 예외: {ex.Message}");
                return (false, string.Empty);
            }
        }

        /// <summary>
        /// [v3.3.8 / v5.0.8] 바이낸스 서버사이드 TRAILING_STOP_MARKET 주문
        /// 거래소가 자동으로 고점 추적 → callbackRate% 하락 시 시장가 청산
        /// v5.0.8: 에러 메시지를 Console → OnLog + MainWindow.AddLog 로 노출
        ///          실패 시 Binance 에러 코드 상세 로깅 (GIGGLE 케이스 재발 방지)
        /// </summary>
        /// <param name="callbackRate">콜백 비율 (%) — 0.1~5.0, 예: 1.0 = 고점 대비 1% 하락 시 발동</param>
        /// <param name="activationPrice">활성화 가격 — 이 가격 도달 후부터 트레일링 시작 (null이면 즉시)</param>
        public async Task<(bool Success, string OrderId)> PlaceTrailingStopOrderAsync(
            string symbol, string side, decimal quantity,
            decimal callbackRate, decimal? activationPrice = null,
            CancellationToken ct = default)
        {
            try
            {
                (decimal stepSize, decimal tickSize) = await GetSymbolPrecisionAsync(symbol, ct);
                if (stepSize > 0) quantity = Math.Floor(quantity / stepSize) * stepSize;
                if (activationPrice.HasValue && tickSize > 0)
                    activationPrice = Math.Floor(activationPrice.Value / tickSize) * tickSize;
                callbackRate = Math.Clamp(callbackRate, 0.1m, 5.0m);

                if (quantity <= 0)
                {
                    MainWindow.Instance?.AddLog($"❌ [TRAILING] {symbol} quantity={quantity} — step 반영 후 0");
                    return (false, string.Empty);
                }

                // [v5.10.63] Algo Order API — TRAILING_STOP_MARKET
                string qs = $"symbol={symbol}&side={side.ToUpper()}&algoType=CONDITIONAL&type=TRAILING_STOP_MARKET&quantity={quantity}&callbackRate={callbackRate}&reduceOnly=true";
                if (activationPrice.HasValue && activationPrice.Value > 0)
                    qs += $"&activationPrice={activationPrice.Value}";

                var (ok, body) = await CallAlgoApiAsync(HttpMethod.Post, "/fapi/v1/algoOrder", qs, ct);
                if (!ok)
                {
                    MainWindow.Instance?.AddLog($"❌ [TRAILING] {symbol} algoOrder 실패: {body}");
                    return (false, string.Empty);
                }

                using var doc = JsonDocument.Parse(body);
                long algoId = doc.RootElement.GetProperty("algoId").GetInt64();
                MainWindow.Instance?.AddLog($"✅ [TRAILING] {symbol} TRAILING_STOP_MARKET 등록 | {side} qty={quantity} callback={callbackRate}% activation={activationPrice?.ToString("F6") ?? "즉시"} algoId={algoId}");
                return (true, algoId.ToString());
            }
            catch (Exception ex)
            {
                MainWindow.Instance?.AddLog($"❌ [TRAILING] {symbol} 예외: {ex.Message}");
                return (false, string.Empty);
            }
        }

        public async Task<bool> CancelOrderAsync(string symbol, string orderId, CancellationToken ct = default)
        {
            // [v5.10.63] orderId는 algoId 또는 일반 orderId일 수 있음 — 둘 다 시도
            if (!long.TryParse(orderId, out long id)) return false;

            // 1) Algo 주문 취소 시도 (DELETE /fapi/v1/algoOrder?algoId=...)
            var (ok1, body1) = await CallAlgoApiAsync(HttpMethod.Delete, "/fapi/v1/algoOrder", $"symbol={symbol}&algoId={id}", ct);
            if (ok1)
            {
                MainWindow.Instance?.AddLog($"🗑️ [Cancel] {symbol} algo {id} 취소 성공");
                return true;
            }

            // 2) 일반 주문 취소 (Binance.Net — LIMIT/MARKET 대상)
            try
            {
                var result = await _client.UsdFuturesApi.Trading.CancelOrderAsync(symbol, id, ct: ct);
                if (result.Success)
                {
                    MainWindow.Instance?.AddLog($"🗑️ [Cancel] {symbol} order {id} 취소 성공");
                    return true;
                }
            }
            catch { }

            MainWindow.Instance?.AddLog($"⚠️ [Cancel] {symbol} {id} 실패 (algo/일반 모두)");
            return false;
        }

        /// <summary>[v5.10.63] TAKE_PROFIT_MARKET — Binance 이관 후 /fapi/v1/algoOrder 사용</summary>
        public async Task<(bool Success, string OrderId)> PlaceTakeProfitOrderAsync(
            string symbol, string side, decimal quantity, decimal stopPrice,
            CancellationToken ct = default)
        {
            try
            {
                (decimal stepSize, decimal tickSize) = await GetSymbolPrecisionAsync(symbol, ct);
                if (stepSize > 0) quantity = Math.Floor(quantity / stepSize) * stepSize;
                if (tickSize > 0) stopPrice = Math.Floor(stopPrice / tickSize) * tickSize;
                if (quantity <= 0 || stopPrice <= 0) return (false, string.Empty);

                string qs = $"symbol={symbol}&side={side.ToUpper()}&algoType=CONDITIONAL&type=TAKE_PROFIT_MARKET&quantity={quantity}&triggerPrice={stopPrice}&reduceOnly=true";
                var (ok, body) = await CallAlgoApiAsync(HttpMethod.Post, "/fapi/v1/algoOrder", qs, ct);
                if (!ok)
                {
                    MainWindow.Instance?.AddLog($"❌ [TP] {symbol} algoOrder 실패: {body}");
                    return (false, string.Empty);
                }

                using var doc = JsonDocument.Parse(body);
                long algoId = doc.RootElement.GetProperty("algoId").GetInt64();
                MainWindow.Instance?.AddLog($"✅ [TP] {symbol} TAKE_PROFIT_MARKET 등록 | {side} qty={quantity} trigger=${stopPrice} algoId={algoId}");
                return (true, algoId.ToString());
            }
            catch (Exception ex)
            {
                MainWindow.Instance?.AddLog($"❌ [TP] {symbol} 예외: {ex.Message}");
                return (false, string.Empty);
            }
        }

        /// <summary>[v5.10.63] 특정 심볼의 algoOrder 개수 조회 — EnsureActivePositionProtectionAsync에서 사용</summary>
        public async Task<int> GetOpenAlgoOrderCountAsync(string symbol, CancellationToken ct = default)
        {
            var (ok, body) = await CallAlgoApiAsync(HttpMethod.Get, "/fapi/v1/openAlgoOrders", $"symbol={symbol}", ct);
            if (!ok) return 0;
            try
            {
                using var doc = JsonDocument.Parse(body);
                if (doc.RootElement.ValueKind == JsonValueKind.Array) return doc.RootElement.GetArrayLength();
                return 0;
            }
            catch { return 0; }
        }

        /// <summary>[v5.10.63] 특정 심볼의 모든 주문(일반+Algo) 일괄 취소</summary>
        public async Task CancelAllOrdersAsync(string symbol, CancellationToken ct = default)
        {
            // 1) 일반 주문 일괄 취소 (LIMIT/MARKET 등)
            try
            {
                var result = await _client.UsdFuturesApi.Trading.CancelAllOrdersAsync(symbol, ct: ct);
                if (result.Success)
                    MainWindow.Instance?.AddLog($"🗑️ [Binance] {symbol} 일반 주문 일괄 취소 성공");
            }
            catch (Exception ex) { MainWindow.Instance?.AddLog($"⚠️ [Binance] {symbol} 일반 주문 취소 예외: {ex.Message}"); }

            // 2) Algo 주문 일괄 취소 (STOP_MARKET/TP_MARKET/TRAILING_STOP_MARKET) — DELETE /fapi/v1/algoOpenOrders
            var (ok, body) = await CallAlgoApiAsync(HttpMethod.Delete, "/fapi/v1/algoOpenOrders", $"symbol={symbol}", ct);
            if (ok)
                MainWindow.Instance?.AddLog($"🗑️ [Binance] {symbol} algoOrders 일괄 취소 성공");
            else
                MainWindow.Instance?.AddLog($"⚠️ [Binance] {symbol} algoOrders 취소 결과: {body}");
        }

        // [v5.10.18] 진입 이후 최근 체결 내역 → 실제 청산가 추적
        public async Task<(decimal exitPrice, decimal quantity, string side, DateTime time)?> GetLastTradeAsync(
            string symbol, DateTime since, CancellationToken ct = default)
        {
            try
            {
                var result = await _client.UsdFuturesApi.Trading.GetUserTradesAsync(
                    symbol, startTime: since, ct: ct);
                if (!result.Success || result.Data == null) return null;
                var trades = result.Data.OrderByDescending(t => t.Timestamp).ToList();
                var last = trades.FirstOrDefault();
                if (last == null) return null;
                return (last.Price, Math.Abs(last.Quantity), last.Side.ToString(), last.Timestamp);
            }
            catch { return null; }
        }

        public async Task<bool> SetLeverageAsync(string symbol, int leverage, CancellationToken ct = default)
        {
            return await SetLeverageAutoAsync(symbol, leverage, ct) > 0;
        }

        public async Task<int> SetLeverageAutoAsync(string symbol, int desiredLeverage, CancellationToken ct = default)
        {
            var result = await _client.UsdFuturesApi.Account.ChangeInitialLeverageAsync(symbol, desiredLeverage, ct: ct);
            if (result.Success)
            {
                OnLog?.Invoke($"✅ [레버리지] {symbol} {desiredLeverage}x 설정 성공");
                return desiredLeverage;
            }

            // 심볼 최대 레버리지 초과 시 자동 조정: "'N' cannot be greater than M" 또는 서브어카운트 "greater than Nx"
            var errMsg = result.Error?.Message ?? "";
            var match = System.Text.RegularExpressions.Regex.Match(errMsg, @"(?:cannot be greater than|greater than)\s+(\d+)");
            if (match.Success && int.TryParse(match.Groups[1].Value, out int maxLev) && maxLev > 0 && maxLev < desiredLeverage)
            {
                OnLog?.Invoke($"⚠️ [레버리지] {symbol} {desiredLeverage}x 불가 → 최대 {maxLev}x로 자동 조정");
                var retry = await _client.UsdFuturesApi.Account.ChangeInitialLeverageAsync(symbol, maxLev, ct: ct);
                if (retry.Success)
                {
                    OnLog?.Invoke($"✅ [레버리지] {symbol} {maxLev}x 설정 성공 (자동 조정)");
                    return maxLev;
                }
                OnLog?.Invoke($"❌ [레버리지 실패] {symbol} {maxLev}x 재시도 실패 | {retry.Error?.Message}");
                return 0;
            }

            OnLog?.Invoke($"❌ [레버리지 실패] {symbol} {desiredLeverage}x 설정 불가 | 에러: {errMsg} (code={result.Error?.Code})");
            return 0;
        }

        public async Task<List<PositionInfo>> GetPositionsAsync(CancellationToken ct = default)
        {
            var result = await _client.UsdFuturesApi.Account.GetPositionInformationAsync(ct: ct);
            if (!result.Success) return new List<PositionInfo>();

            return result.Data
                .Where(p => Math.Abs(p.Quantity) > 0)
                .Select(p => new PositionInfo
                {
                    Symbol = p.Symbol,
                    Side = p.Quantity > 0 ? Binance.Net.Enums.OrderSide.Buy : Binance.Net.Enums.OrderSide.Sell,
                    IsLong = p.Quantity > 0,
                    Quantity = Math.Abs(p.Quantity),
                    EntryPrice = p.EntryPrice,
                    UnrealizedPnL = p.UnrealizedPnl,
                    Leverage = p.Leverage
                })
                .ToList();
        }

        public async Task<List<IBinanceKline>> GetKlinesAsync(string symbol, KlineInterval interval, int limit, CancellationToken ct = default)
        {
            var result = await _client.UsdFuturesApi.ExchangeData.GetKlinesAsync(symbol, interval, limit: limit, ct: ct);
            if (!result.Success) return new List<IBinanceKline>();
            return result.Data.Cast<IBinanceKline>().ToList();
        }

        /// <summary>
        /// [v2.4.2] 날짜 범위 기반 캔들 조회 (HistoricalDataLabeler용 6개월 데이터 수집)
        /// 현재는 최근 데이터만 반환하며, 향후 pagination 추가
        /// </summary>
        public async Task<List<IBinanceKline>> GetKlinesAsync(
            string symbol,
            KlineInterval interval,
            DateTime? startTime = null,
            DateTime? endTime = null,
            int limit = 1000,
            CancellationToken ct = default)
        {
            try
            {
                // 간단한 구현: Binance API에서 최근 limit개 캔들 조회
                // TODO: startTime/endTime 파라미터로 진정한 범위 조회 구현 (pagination 필요)
                var result = await _client.UsdFuturesApi.ExchangeData.GetKlinesAsync(
                    symbol,
                    interval,
                    startTime: startTime,
                    endTime: endTime,
                    limit: limit,
                    ct: ct);

                if (!result.Success)
                    return new List<IBinanceKline>();

                var allKlines = result.Data.Cast<IBinanceKline>().ToList();
                
                // 중복 제거 및 정렬
                return allKlines
                    .GroupBy(k => k.CloseTime)
                    .Select(g => g.First())
                    .OrderBy(k => k.CloseTime)
                    .ToList();
            }
            catch
            {
                return new List<IBinanceKline>();
            }
        }

        public async Task<ExchangeInfo?> GetExchangeInfoAsync(CancellationToken ct = default)
        {
            var result = await _client.UsdFuturesApi.ExchangeData.GetExchangeInfoAsync(ct: ct);
            if (!result.Success || result.Data == null) return null;

            var exchangeInfo = new ExchangeInfo();
            foreach (var s in result.Data.Symbols)
            {
                var symbolInfo = new SymbolInfo
                {
                    Name = s.Name,
                    LotSizeFilter = s.LotSizeFilter != null ? new SymbolFilter
                    {
                        StepSize = s.LotSizeFilter.StepSize,
                        TickSize = 0 // Not available in LotSizeFilter
                    } : null,
                    PriceFilter = s.PriceFilter != null ? new SymbolFilter
                    {
                        StepSize = 0, // Not available in PriceFilter
                        TickSize = s.PriceFilter.TickSize
                    } : null
                };
                exchangeInfo.Symbols.Add(symbolInfo);
            }
            return exchangeInfo;
        }

        public async Task<(decimal bestBid, decimal bestAsk)?> GetOrderBookAsync(string symbol, CancellationToken ct = default)
        {
            try
            {
                var result = await _client.UsdFuturesApi.ExchangeData.GetOrderBookAsync(symbol, limit: 5, ct: ct);
                if (!result.Success || result.Data == null) return null;

                var bestBid = result.Data.Bids.FirstOrDefault()?.Price ?? 0;
                var bestAsk = result.Data.Asks.FirstOrDefault()?.Price ?? 0;

                return (bestBid, bestAsk);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[Binance] GetOrderBookAsync failed: {ex.Message}");
                return null;
            }
        }

        public async Task<decimal> GetFundingRateAsync(string symbol, CancellationToken token = default)
        {
            try
            {
                var result = await _client.UsdFuturesApi.ExchangeData.GetFundingRatesAsync(symbol, limit: 1, ct: token);
                if (!result.Success || result.Data == null || !result.Data.Any()) 
                    return 0;

                var fundingData = result.Data.FirstOrDefault();
                if (fundingData == null)
                    return 0;

                return fundingData.FundingRate;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"❌ [Binance] GetFundingRate 예외 - {symbol}: {ex.Message}");
                return 0;
            }
        }

        // [Phase 12: PUMP 전략 지원] 지정가 주문
        public async Task<(bool Success, string OrderId)> PlaceLimitOrderAsync(
            string symbol,
            string side,
            decimal quantity,
            decimal price,
            CancellationToken ct = default)
        {
            try
            {
                // 정밀도 보정 (캐시 + 폴백 보장)
                (decimal stepSize, decimal tickSize) = await GetSymbolPrecisionAsync(symbol, ct);
                if (stepSize > 0)
                    quantity = Math.Floor(quantity / stepSize) * stepSize;
                if (tickSize > 0)
                    price = Math.Floor(price / tickSize) * tickSize;

                if (quantity <= 0) return (false, string.Empty);

                var sideUpper = side.ToUpper();
                OrderSide orderSide = (sideUpper == "BUY" || sideUpper == "LONG") ? OrderSide.Buy : OrderSide.Sell;

                var result = await _client.UsdFuturesApi.Trading.PlaceOrderAsync(
                    symbol,
                    orderSide,
                    FuturesOrderType.Limit,
                    quantity,
                    price: price,
                    timeInForce: TimeInForce.GoodTillCanceled,
                    ct: ct);

                if (result.Success && result.Data != null)
                {
                    OnLog?.Invoke($"✅ [Binance] 지정가 주문 성공 - {symbol} {side} {quantity}@{price} (OrderId: {result.Data.Id})");
                    return (true, result.Data.Id.ToString());
                }

                // [v5.10.27] Console → OnLog 변경: DB FooterLogs에 기록되도록
                OnLog?.Invoke($"❌ [Binance] 지정가 주문 실패 - {symbol} {side} {quantity}@{price} | Code={result.Error?.Code} | {result.Error?.Message}");
                return (false, string.Empty);
            }
            catch (Exception ex)
            {
                OnLog?.Invoke($"❌ [Binance] PlaceLimitOrder 예외 - {symbol} {side} {quantity}@{price} | {ex.Message}");
                return (false, string.Empty);
            }
        }

        // [Phase 12: PUMP 전략 지원] 주문 상태 확인
        public async Task<(bool Filled, decimal FilledQuantity, decimal AveragePrice)> GetOrderStatusAsync(
            string symbol,
            string orderId,
            CancellationToken ct = default)
        {
            try
            {
                if (!long.TryParse(orderId, out long id))
                {
                    return (false, 0, 0);
                }

                var result = await _client.UsdFuturesApi.Trading.GetOrderAsync(symbol, id, ct: ct);

                if (!result.Success || result.Data == null)
                {
                    Console.WriteLine($"❌ [Binance] 주문 상태 조회 실패 - {symbol} OrderId={orderId}");
                    Console.WriteLine($"   에러: {result.Error?.Message}");
                    return (false, 0, 0);
                }

                var order = result.Data;
                bool isFilled = order.Status == OrderStatus.Filled;
                bool isPartiallyFilled = order.Status == OrderStatus.PartiallyFilled;

                decimal filledQty = order.QuantityFilled;
                decimal avgPrice = order.AveragePrice > 0 ? order.AveragePrice : order.Price;

                Console.WriteLine($"📊 [Binance] 주문 상태: {symbol} OrderId={orderId} | Status={order.Status} | Filled={filledQty}/{order.Quantity} | AvgPrice={avgPrice}");

                return (isFilled || isPartiallyFilled, filledQty, avgPrice);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"❌ [Binance] GetOrderStatus 예외 - {symbol} OrderId={orderId}");
                Console.WriteLine($"   예외: {ex.Message}");
                return (false, 0, 0);
            }
        }

        // [시장가 주문] 즉시 체결 + 체결 정보 반환 (지정가 3초 대기 제거)
        public async Task<(bool Success, decimal FilledQuantity, decimal AveragePrice)> PlaceMarketOrderAsync(
            string symbol,
            string side,
            decimal quantity,
            CancellationToken ct = default,
            bool reduceOnly = false)
        {
            try
            {
                // 1. 수량 정밀도 보정 (캐시 + 폴백 보장)
                (decimal stepSize, _) = await GetSymbolPrecisionAsync(symbol, ct);
                if (stepSize > 0)
                    quantity = Math.Floor(quantity / stepSize) * stepSize;

                if (quantity <= 0)
                {
                    Console.WriteLine($"❌ [Binance] 시장가 주문 실패 - 수량 0: {symbol}");
                    return (false, 0, 0);
                }

                OrderSide orderSide = side.ToUpper() == "BUY" ? OrderSide.Buy : OrderSide.Sell;

                // 2. 시장가 주문 실행
                Console.WriteLine($"📤 [Binance] 시장가 주문 전송 - {symbol} {side} {quantity} reduceOnly={reduceOnly}");
                var result = await _client.UsdFuturesApi.Trading.PlaceOrderAsync(
                    symbol,
                    orderSide,
                    FuturesOrderType.Market,
                    quantity,
                    reduceOnly: reduceOnly,
                    ct: ct);

                if (!result.Success || result.Data == null)
                {
                    string errMsg = result.Error?.Message ?? "알 수 없는 오류";
                    OnLog?.Invoke($"❌ [주문실패] {symbol} {side} {quantity} — {errMsg} (code={result.Error?.Code})");
                    return (false, 0, 0);
                }

                var orderId = result.Data.Id.ToString();
                Console.WriteLine($"✅ [Binance] 시장가 주문 접수 - OrderId={orderId}");

                // 3. 짧은 대기 (시장가는 500ms 내 체결)
                await Task.Delay(500, ct);

                // 4. 체결 확인
                var statusResult = await _client.UsdFuturesApi.Trading.GetOrderAsync(symbol, result.Data.Id, ct: ct);

                if (!statusResult.Success || statusResult.Data == null)
                {
                    Console.WriteLine($"⚠️ [Binance] 체결 확인 실패 (주문은 성공) - OrderId={orderId}");
                    // 주문은 성공했으므로 포지션 조회로 복구 가능
                    return (true, quantity, 0);
                }

                var order = statusResult.Data;
                bool isFilled = order.Status == OrderStatus.Filled || order.Status == OrderStatus.PartiallyFilled;
                decimal filledQty = order.QuantityFilled;
                decimal avgPrice = order.AveragePrice > 0 ? order.AveragePrice : order.Price;

                Console.WriteLine($"✅ [Binance] 시장가 체결 완료 - {symbol} | Filled={filledQty} | AvgPrice={avgPrice:F4}");

                return (isFilled, filledQty, avgPrice);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"❌ [Binance] PlaceMarketOrder 예외 - {symbol} {side} {quantity}");
                Console.WriteLine($"   예외: {ex.Message}");
                return (false, 0, 0);
            }
        }

        /// <summary>
        /// Batch Order 실행 (Grid Strategy 최적화)
        /// Binance Futures는 최대 5개의 주문을 한 번에 처리할 수 있습니다.
        /// </summary>
        public async Task<BatchOrderResult> PlaceBatchOrdersAsync(List<BatchOrderRequest> orders, CancellationToken ct = default)
        {
            var result = new BatchOrderResult();
            if (orders == null || orders.Count == 0) return result;

            // Binance API는 한 번에 5개까지 처리 가능하므로 청크로 분할
            const int batchSize = 5;
            for (int i = 0; i < orders.Count; i += batchSize)
            {
                var batch = orders.Skip(i).Take(batchSize).ToList();

                try
                {
                    // PlaceMultipleOrdersAsync 사용
                    var batchOrders = new List<Binance.Net.Objects.Models.Futures.BinanceFuturesBatchOrder>();

                    foreach (var order in batch)
                    {
                        var orderSide = order.Side.ToUpper() == "BUY" ? OrderSide.Buy : OrderSide.Sell;
                        var orderType = order.OrderType?.ToUpper() == "MARKET"
                            ? FuturesOrderType.Market
                            : FuturesOrderType.Limit;

                        batchOrders.Add(new Binance.Net.Objects.Models.Futures.BinanceFuturesBatchOrder
                        {
                            Symbol = order.Symbol,
                            Side = orderSide,
                            Type = orderType,
                            Quantity = order.Quantity,
                            Price = orderType == FuturesOrderType.Limit ? order.Price : null
                        });
                    }

                    var batchResult = await _client.UsdFuturesApi.Trading.PlaceMultipleOrdersAsync(batchOrders, ct: ct);

                    if (batchResult.Success && batchResult.Data != null)
                    {
                        foreach (var orderResult in batchResult.Data)
                        {
                            if (orderResult.Success)
                            {
                                result.SuccessCount++;
                                result.OrderIds.Add(orderResult.Data?.Id.ToString() ?? "");
                            }
                            else
                            {
                                result.FailureCount++;
                                result.Errors.Add($"{orderResult.Error?.Code}: {orderResult.Error?.Message}");
                            }
                        }
                    }
                    else
                    {
                        result.FailureCount += batch.Count;
                        result.Errors.Add($"Batch 실패: {batchResult.Error?.Message}");
                    }
                }
                catch (Exception ex)
                {
                    result.FailureCount += batch.Count;
                    result.Errors.Add($"Batch 예외: {ex.Message}");
                }

                // API Rate Limit 방지를 위한 지연
                if (i + batchSize < orders.Count)
                {
                    await Task.Delay(200, ct);
                }
            }

            return result;
        }

        /// <summary>
        /// Multi-Assets Mode 조회 (Portfolio Margin)
        /// </summary>
        public async Task<bool> GetMultiAssetsModeAsync(CancellationToken ct = default)
        {
            try
            {
                var result = await _client.UsdFuturesApi.Account.GetMultiAssetsModeAsync(ct: ct);
                if (result.Success && result.Data != null)
                {
                    // Binance.Net v12.x: MultiAssetMode 속성 사용
                    return result.Data.MultiAssetMode;
                }
                return false;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[Binance] GetMultiAssetsMode Error: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Multi-Assets Mode 설정 (Portfolio Margin 활성화/비활성화)
        /// </summary>
        public async Task<bool> SetMultiAssetsModeAsync(bool enabled, CancellationToken ct = default)
        {
            try
            {
                var result = await _client.UsdFuturesApi.Account.SetMultiAssetsModeAsync(enabled, ct: ct);
                if (result.Success)
                {
                    System.Diagnostics.Debug.WriteLine($"[Binance] Multi-Assets Mode {(enabled ? "활성화" : "비활성화")} 완료");
                    return true;
                }
                else
                {
                    System.Diagnostics.Debug.WriteLine($"[Binance] SetMultiAssetsMode Error: {result.Error?.Message}");
                    return false;
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[Binance] SetMultiAssetsMode Exception: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Position Mode 조회 (Hedge Mode 여부)
        /// </summary>
        public async Task<bool> GetPositionModeAsync(CancellationToken ct = default)
        {
            try
            {
                var result = await _client.UsdFuturesApi.Account.GetPositionModeAsync(ct: ct);
                return result.Success && result.Data.IsHedgeMode;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[Binance] GetPositionMode Error: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Position Mode 설정 (Hedge Mode vs One-way Mode)
        /// </summary>
        public async Task<bool> SetPositionModeAsync(bool hedgeMode, CancellationToken ct = default)
        {
            try
            {
                var result = await _client.UsdFuturesApi.Account.ModifyPositionModeAsync(hedgeMode, ct: ct);
                if (result.Success)
                {
                    System.Diagnostics.Debug.WriteLine($"[Binance] Position Mode: {(hedgeMode ? "Hedge" : "One-way")} 설정 완료");
                    return true;
                }
                else
                {
                    System.Diagnostics.Debug.WriteLine($"[Binance] SetPositionMode Error: {result.Error?.Message}");
                    return false;
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[Binance] SetPositionMode Exception: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// [v5.5.2] 시장가 진입 + SL + TP(부분익절) + 트레일링 스탑 일괄 API 등록
        /// 진입 체결 즉시 거래소에 모든 청산 주문을 등록 → 봇 다운타임에도 거래소가 자동 처리
        /// </summary>
        /// <param name="partialProfitRoePercent">1차 익절 수량 비율 (예: 40.0 = 전체의 40%)</param>
        /// <param name="trailingStopCallbackRate">트레일링 콜백율 소수 표현 (예: 0.02 = 2%)</param>
        public async Task<bool> ExecuteFullEntryWithAllOrdersAsync(
            string symbol,
            string positionSide,
            decimal quantity,
            decimal leverage,
            decimal stopLossPrice,
            decimal takeProfitPrice,
            decimal partialProfitRoePercent,
            decimal trailingStopCallbackRate,
            CancellationToken ct = default)
        {
            bool isLong = positionSide.ToUpper() == "LONG";
            string entrySide = isLong ? "BUY" : "SELL";
            string closeSide = isLong ? "SELL" : "BUY";

            try
            {
                // [1] 레버리지 설정
                await SetLeverageAsync(symbol, (int)leverage, ct);

                // [2] 시장가 진입
                var (entryOk, filledQty, avgPrice) = await PlaceMarketOrderAsync(symbol, entrySide, quantity, ct, reduceOnly: false);
                if (!entryOk || filledQty <= 0)
                {
                    OnLog?.Invoke($"❌ [FULL_ENTRY] {symbol} 시장가 진입 실패");
                    return false;
                }
                OnLog?.Invoke($"✅ [FULL_ENTRY] {symbol} {positionSide} 진입 | qty={filledQty} avgPrice={avgPrice:F4}");

                // 수량 정밀도 보정
                (decimal stepSize, decimal tickSize) = await GetSymbolPrecisionAsync(symbol, ct);

                // [3] SL 등록 (전체 수량)
                if (stopLossPrice > 0)
                {
                    var (slOk, _) = await PlaceStopOrderAsync(symbol, closeSide, filledQty, stopLossPrice, ct);
                    if (!slOk)
                        OnLog?.Invoke($"⚠️ [FULL_ENTRY] {symbol} SL 등록 실패 — 내부 모니터링 대체");
                }

                // [4] TP 부분익절 등록 (partialProfitRoePercent% 수량)
                decimal tpRatio = Math.Clamp(partialProfitRoePercent / 100m, 0.1m, 0.9m);
                decimal tpQty = stepSize > 0
                    ? Math.Floor(filledQty * tpRatio / stepSize) * stepSize
                    : Math.Round(filledQty * tpRatio, 8);

                if (tpQty > 0 && takeProfitPrice > 0)
                {
                    var (tpOk, _) = await PlaceTakeProfitOrderAsync(symbol, closeSide, tpQty, takeProfitPrice, ct);
                    if (!tpOk)
                        OnLog?.Invoke($"⚠️ [FULL_ENTRY] {symbol} TP 등록 실패");
                }

                // [5] 트레일링 스탑 등록 (잔여 수량, TP 도달 시 활성화)
                decimal trailingQty = stepSize > 0
                    ? Math.Floor((filledQty - tpQty) / stepSize) * stepSize
                    : Math.Round(filledQty - tpQty, 8);

                // 소수 표현 → % 변환 (0.02 → 2.0%)
                decimal callbackPct = trailingStopCallbackRate < 1m
                    ? trailingStopCallbackRate * 100m
                    : trailingStopCallbackRate;
                callbackPct = Math.Clamp(callbackPct, 0.1m, 5.0m);

                if (trailingQty > 0)
                {
                    var (trailOk, _) = await PlaceTrailingStopOrderAsync(
                        symbol, closeSide, trailingQty,
                        callbackPct,
                        activationPrice: takeProfitPrice > 0 ? takeProfitPrice : (decimal?)null,
                        ct);
                    if (!trailOk)
                        OnLog?.Invoke($"⚠️ [FULL_ENTRY] {symbol} 트레일링 등록 실패");
                }

                OnLog?.Invoke($"✅ [FULL_ENTRY] {symbol} SL/TP/Trailing 일괄 등록 완료 | SL={stopLossPrice:F4} TP={takeProfitPrice:F4} tpQty={tpQty} trailQty={trailingQty} callback={callbackPct}%");
                return true;
            }
            catch (Exception ex)
            {
                OnLog?.Invoke($"❌ [FULL_ENTRY] {symbol} 예외: {ex.Message}");
                return false;
            }
        }

        public void Dispose()
        {
            if (_disposed) return;

            try
            {
                _client?.Dispose();
            }
            catch { }
            finally
            {
                _disposed = true;
            }

            GC.SuppressFinalize(this);
        }
    }
}
