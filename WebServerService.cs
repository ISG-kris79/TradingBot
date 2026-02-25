using System;
using System.Net;
using System.Text;
using System.Threading.Tasks;

namespace TradingBot.Services
{
    public class WebServerService
    {
        private HttpListener _listener;
        private Func<string> _statusProvider;
        private bool _isRunning = false;

        public WebServerService(Func<string> statusProvider)
        {
            _statusProvider = statusProvider;
        }

        public void Start(int port)
        {
            if (_isRunning) return;
            _listener = new HttpListener();
            _listener.Prefixes.Add($"http://*:{port}/");
            _listener.Start();
            _isRunning = true;
            Task.Run(ListenLoop);
        }

        public void Stop()
        {
            _isRunning = false;
            _listener?.Stop();
        }

        private async Task ListenLoop()
        {
            while (_isRunning)
            {
                try
                {
                    var context = await _listener.GetContextAsync();
                    string responseString = _statusProvider();
                    byte[] buffer = Encoding.UTF8.GetBytes(responseString);
                    context.Response.ContentLength64 = buffer.Length;
                    context.Response.OutputStream.Write(buffer, 0, buffer.Length);
                    context.Response.OutputStream.Close();
                }
                catch { }
            }
        }
    }
}