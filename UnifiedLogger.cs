using System;
using System.Collections.Concurrent;
using System.IO;
using System.Text.Json;
using System.Threading;

namespace TradingBot.Services
{
    /// <summary>
    /// 통합 로그 시스템 - 모든 로그를 한곳에서 처리
    /// </summary>
    public static class UnifiedLogger
    {
        private static readonly ConcurrentQueue<LogEntry> _logQueue = new();
        private static readonly Timer _flushTimer;
        private static readonly string _logDirectory = "Logs";
        private static readonly object _consoleLock = new();
        
        // 로그 레벨
        public enum LogLevel
        {
            Trace,    // 상세 추적 (디버깅)
            Debug,    // 디버그 정보
            Info,     // 일반 정보
            Warning,  // 경고
            Error,    // 에러
            Critical  // 치명적 오류
        }
        
        // 로그 카테고리
        public enum LogCategory
        {
            System,       // 시스템 전반
            Trading,      // 트레이딩 엔진
            AI,           // AI/ML 관련
            Exchange,     // 거래소 API
            Position,     // 포지션 관리
            Risk,         // 리스크 관리
            UI,           // UI 이벤트
            Database,     // DB 작업
            Network,      // 네트워크/소켓
            Performance   // 성능 모니터링
        }
        
        // 이벤트: UI에서 구독 가능
        public static event Action<LogEntry>? OnLogReceived;
        
        static UnifiedLogger()
        {
            Directory.CreateDirectory(_logDirectory);
            _flushTimer = new Timer(_ => FlushLogs(), null, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5));
        }
        
        /// <summary>
        /// 로그 출력 (가장 일반적인 메서드)
        /// </summary>
        public static void Log(LogLevel level, LogCategory category, string message, Exception? ex = null)
        {
            var entry = new LogEntry
            {
                Timestamp = DateTime.UtcNow,
                Level = level,
                Category = category,
                Message = message,
                Exception = ex?.ToString(),
                ThreadId = Environment.CurrentManagedThreadId
            };
            
            _logQueue.Enqueue(entry);
            
            // 즉시 콘솔 출력 (컬러링)
            OutputToConsole(entry);
            
            // UI 이벤트 발생
            OnLogReceived?.Invoke(entry);
            
            // Critical/Error는 즉시 파일 저장
            if (level >= LogLevel.Error)
            {
                FlushLogs();
            }
        }
        
        /// <summary>
        /// 편의 메서드: Trace
        /// </summary>
        public static void Trace(LogCategory category, string message) 
            => Log(LogLevel.Trace, category, message);
        
        /// <summary>
        /// 편의 메서드: Debug
        /// </summary>
        public static void Debug(LogCategory category, string message) 
            => Log(LogLevel.Debug, category, message);
        
        /// <summary>
        /// 편의 메서드: Info
        /// </summary>
        public static void Info(LogCategory category, string message) 
            => Log(LogLevel.Info, category, message);
        
        /// <summary>
        /// 편의 메서드: Warning
        /// </summary>
        public static void Warn(LogCategory category, string message) 
            => Log(LogLevel.Warning, category, message);
        
        /// <summary>
        /// 편의 메서드: Error
        /// </summary>
        public static void Error(LogCategory category, string message, Exception? ex = null) 
            => Log(LogLevel.Error, category, message, ex);
        
        /// <summary>
        /// 편의 메서드: Critical
        /// </summary>
        public static void Critical(LogCategory category, string message, Exception? ex = null) 
            => Log(LogLevel.Critical, category, message, ex);
        
        /// <summary>
        /// 콘솔 출력 (컬러링 적용)
        /// </summary>
        private static void OutputToConsole(LogEntry entry)
        {
            lock (_consoleLock)
            {
                // 원래 색상 저장
                var originalColor = Console.ForegroundColor;
                
                try
                {
                    // 레벨별 색상
                    Console.ForegroundColor = entry.Level switch
                    {
                        LogLevel.Trace => ConsoleColor.Gray,
                        LogLevel.Debug => ConsoleColor.Cyan,
                        LogLevel.Info => ConsoleColor.White,
                        LogLevel.Warning => ConsoleColor.Yellow,
                        LogLevel.Error => ConsoleColor.Red,
                        LogLevel.Critical => ConsoleColor.Magenta,
                        _ => ConsoleColor.White
                    };
                    
                    // 아이콘
                    string icon = entry.Level switch
                    {
                        LogLevel.Trace => "🔍",
                        LogLevel.Debug => "🐛",
                        LogLevel.Info => "ℹ️",
                        LogLevel.Warning => "⚠️",
                        LogLevel.Error => "❌",
                        LogLevel.Critical => "🚨",
                        _ => "📝"
                    };
                    
                    // 출력 형식
                    Console.WriteLine(
                        $"{icon} [{entry.Timestamp:HH:mm:ss.fff}] [{entry.Level}] [{entry.Category}] {entry.Message}");
                    
                    // 예외 출력
                    if (!string.IsNullOrEmpty(entry.Exception))
                    {
                        Console.ForegroundColor = ConsoleColor.DarkRed;
                        Console.WriteLine($"   Exception: {entry.Exception}");
                    }
                }
                finally
                {
                    Console.ForegroundColor = originalColor;
                }
            }
        }
        
        /// <summary>
        /// 로그 파일로 플러시
        /// </summary>
        private static void FlushLogs()
        {
            if (_logQueue.IsEmpty)
                return;
            
            var logFileName = $"TradingBot_{DateTime.UtcNow:yyyyMMdd}.log";
            var logFilePath = Path.Combine(_logDirectory, logFileName);
            
            var entries = new System.Collections.Generic.List<LogEntry>();
            while (_logQueue.TryDequeue(out var entry))
            {
                entries.Add(entry);
            }
            
            if (entries.Count == 0)
                return;
            
            try
            {
                using var writer = new StreamWriter(logFilePath, append: true);
                foreach (var entry in entries)
                {
                    var json = JsonSerializer.Serialize(entry, new JsonSerializerOptions 
                    { 
                        WriteIndented = false 
                    });
                    writer.WriteLine(json);
                }
            }
            catch (Exception ex)
            {
                // 로그 저장 실패는 콘솔에만 출력
                Console.WriteLine($"[UnifiedLogger] 로그 저장 실패: {ex.Message}");
            }
        }
        
        /// <summary>
        /// 로그 엔트리 데이터 구조
        /// </summary>
        public class LogEntry
        {
            public DateTime Timestamp { get; set; }
            public LogLevel Level { get; set; }
            public LogCategory Category { get; set; }
            public string Message { get; set; } = string.Empty;
            public string? Exception { get; set; }
            public int ThreadId { get; set; }
        }
    }
}
