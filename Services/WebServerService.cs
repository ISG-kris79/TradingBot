using System;
using System.Net;
using System.Text;
using System.Threading.Tasks;

namespace TradingBot.Services
{
    public class WebServerService
    {
        private readonly HttpListener _listener = new HttpListener();
        private readonly Func<string> _statusProvider;
        private readonly Func<Task> _startAction;
        private readonly Action _stopAction;
        private readonly Func<string> _logProvider; // [추가] 로그 제공자
        private readonly Func<string, string, Task<string>> _chartDataProvider; // [수정] 차트 데이터 제공자 (symbol, interval)
        private readonly Func<string> _positionProvider; // [추가] 포지션 목록 제공자
        private readonly Func<string, Task<string>> _closeAction; // [추가] 포지션 청산 액션 (symbol -> result json)
        private bool _isRunning = false;

        // 간단한 API Key (실제로는 설정 파일에서 로드해야 함)
        private const string API_KEY = "coinff-secret-key";

        public WebServerService(Func<string> statusProvider, Func<Task> startAction, Action stopAction, Func<string> logProvider, Func<string, string, Task<string>> chartDataProvider, Func<string> positionProvider, Func<string, Task<string>> closeAction)
        {
            _statusProvider = statusProvider;
            _startAction = startAction;
            _stopAction = stopAction;
            _logProvider = logProvider;
            _chartDataProvider = chartDataProvider;
            _positionProvider = positionProvider;
            _closeAction = closeAction;

            // 외부 접속 허용 (관리자 권한 필요: netsh http add urlacl url=http://+:8080/ user=Everyone)
            _listener.Prefixes.Add("http://+:8080/");
            // HTTPS 지원 (인증서 바인딩 필요: netsh http add sslcert ...)
            _listener.Prefixes.Add("https://+:8443/");
        }

        public void Start(int port)
        {
            if (_isRunning) return;
            try
            {
                _listener.Start();
                _isRunning = true;
                Task.Run(ListenLoop);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"WebServer Start Error: {ex.Message}");
            }
        }

        public void Stop()
        {
            _isRunning = false;
            if (_listener.IsListening) _listener.Stop();
        }

        private async Task ListenLoop()
        {
            while (_isRunning && _listener.IsListening)
            {
                try
                {
                    var context = await _listener.GetContextAsync();
                    _ = ProcessRequest(context);
                }
                catch { break; }
            }
        }

        private async Task ProcessRequest(HttpListenerContext context)
        {
            try
            {
                var request = context.Request;
                var response = context.Response;
                string responseString = "";
                var path = request.Url?.AbsolutePath ?? "/";

                // [보안] API Key 인증 (헤더 검사)
                // 모바일 앱에서도 요청 헤더에 "X-API-KEY"를 추가해야 함
                // string apiKey = request.Headers["X-API-KEY"];
                // if (apiKey != API_KEY)
                // {
                //     response.StatusCode = (int)HttpStatusCode.Unauthorized;
                //     response.Close();
                //     return;
                // }

                if (request.HttpMethod == "POST" && path == "/start")
                {
                    _ = Task.Run(() => _startAction()); // 비동기 실행
                    responseString = "{\"status\": \"starting\"}";
                }
                else if (request.HttpMethod == "POST" && path == "/stop")
                {
                    _stopAction();
                    responseString = "{\"status\": \"stopping\"}";
                }
                else if (request.HttpMethod == "GET" && path == "/logs")
                {
                    responseString = _logProvider(); // [추가] 로그 반환
                }
                else if (request.HttpMethod == "GET" && path == "/chart")
                {
                    // 쿼리 파라미터에서 심볼 추출 (예: /chart?symbol=BTCUSDT)
                    string symbol = request.QueryString["symbol"] ?? "BTCUSDT";
                    string interval = request.QueryString["interval"] ?? "1h";
                    responseString = await _chartDataProvider(symbol, interval);
                }
                else if (request.HttpMethod == "GET" && path == "/positions")
                {
                    responseString = _positionProvider();
                }
                else if (request.HttpMethod == "POST" && path == "/close")
                {
                    // Body에서 symbol 읽기 (간단히 쿼리 파라미터로 처리하거나 JSON 파싱)
                    // 여기서는 쿼리 파라미터 사용: /close?symbol=BTCUSDT
                    string symbol = request.QueryString["symbol"] ?? "";
                    responseString = await _closeAction(symbol);
                }
                else
                {
                    responseString = _statusProvider();
                }

                byte[] buffer = Encoding.UTF8.GetBytes(responseString);
                response.ContentLength64 = buffer.Length;
                await response.OutputStream.WriteAsync(buffer, 0, buffer.Length);
                response.Close();
            }
            catch { }
        }
    }
}
