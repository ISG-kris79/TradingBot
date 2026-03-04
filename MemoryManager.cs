using System;
using System.Diagnostics;

namespace TradingBot.Services.Infrastructure
{
    public static class MemoryManager
    {
        private static readonly long MemoryThreshold = 1024 * 1024 * 512; // 512MB
        private static DateTime _lastCollectionTime = DateTime.MinValue;

        /// <summary>
        /// 메모리 사용량을 확인하고 임계값 초과 시 GC를 수행합니다.
        /// </summary>
        public static void CheckAndCollect(Action<string>? logger = null)
        {
            try
            {
                using var process = Process.GetCurrentProcess();
                long memoryUsed = process.PrivateMemorySize64;

                if (memoryUsed > MemoryThreshold)
                {
                    // 너무 잦은 GC 방지 (최소 5분 간격)
                    if ((DateTime.Now - _lastCollectionTime).TotalMinutes < 5) return;

                    logger?.Invoke($"⚠️ [MemoryManager] 메모리 사용량 높음 ({(memoryUsed / 1024 / 1024)}MB). GC 수행...");
                    
                    // LOH(Large Object Heap) 포함하여 강제 수집
                    GC.Collect(2, GCCollectionMode.Forced, true, true);
                    GC.WaitForPendingFinalizers();
                    
                    long afterMemory = Process.GetCurrentProcess().PrivateMemorySize64;
                    logger?.Invoke($"✅ [MemoryManager] 정리 완료. 현재 사용량: {(afterMemory / 1024 / 1024)}MB");
                    
                    _lastCollectionTime = DateTime.Now;
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Memory check failed: {ex.Message}");
            }
        }
    }
}