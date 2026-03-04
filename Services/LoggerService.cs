using Serilog;
using System;
using System.IO;

namespace TradingBot.Services
{
    public static class LoggerService
    {
        public static void Initialize()
        {
            string logFolder = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Logs");
            if (!Directory.Exists(logFolder)) Directory.CreateDirectory(logFolder);

            // Serilog 설정: 매일 새로운 로그 파일 생성, 최근 30일 보관
            Log.Logger = new LoggerConfiguration()
                .MinimumLevel.Information()
                .WriteTo.Console()
                .WriteTo.File(Path.Combine(logFolder, "log-.txt"), 
                    rollingInterval: RollingInterval.Day, 
                    retainedFileCountLimit: 30,
                    outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] {Message:lj}{NewLine}{Exception}")
                .CreateLogger();
        }

        public static void Info(string message) => Log.Information(message);
        public static void Warning(string message) => Log.Warning(message);
        public static void Error(string message, Exception? ex = null) => Log.Error(ex, message);
        public static void CloseAndFlush() => Log.CloseAndFlush();
    }
}