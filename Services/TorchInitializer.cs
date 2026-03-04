using System;
using System.Diagnostics;

namespace TradingBot.Services
{
    /// <summary>
    /// TorchSharp 초기화를 안전하게 처리하는 유틸리티 클래스
    /// </summary>
    public static class TorchInitializer
    {
        private static bool _initialized = false;
        private static bool _available = false;
        private static string? _errorMessage = null;

        /// <summary>
        /// TorchSharp가 사용 가능한지 확인
        /// </summary>
        public static bool IsAvailable => _available;

        /// <summary>
        /// 초기화 오류 메시지 (있는 경우)
        /// </summary>
        public static string? ErrorMessage => _errorMessage;

        /// <summary>
        /// TorchSharp 초기화 시도
        /// </summary>
        public static bool TryInitialize()
        {
            if (_initialized)
                return _available;

            _initialized = true;

            try
            {
                // TorchSharp의 static 초기화를 트리거
                var device = TorchSharp.torch.CPU;
                Debug.WriteLine($"[TorchInitializer] TorchSharp 초기화 성공 - Device: {device}");
                
                // CUDA 체크 (선택 사항)
                try
                {
                    bool cudaAvailable = TorchSharp.torch.cuda.is_available();
                    Debug.WriteLine($"[TorchInitializer] CUDA Available: {cudaAvailable}");
                }
                catch (Exception cudaEx)
                {
                    Debug.WriteLine($"[TorchInitializer] CUDA 체크 실패 (정상 - CPU만 사용): {cudaEx.Message}");
                }

                _available = true;
                return true;
            }
            catch (TypeInitializationException ex)
            {
                _errorMessage = $"TorchSharp 초기화 실패: {ex.InnerException?.Message ?? ex.Message}\n\n" +
                               "해결 방법:\n" +
                               "1. Visual C++ Redistributable 2015-2022 x64 설치\n" +
                               "   다운로드: https://aka.ms/vs/17/release/vc_redist.x64.exe\n" +
                               "2. 앱 재시작\n\n" +
                               "TorchSharp 기능(PPO 에이전트, Transformer)은 비활성화됩니다.\n" +
                               "ML.NET 기반 예측은 정상 작동합니다.";
                Debug.WriteLine($"[TorchInitializer] {_errorMessage}");
                Debug.WriteLine($"[TorchInitializer] Exception Details: {ex}");
                _available = false;
                return false;
            }
            catch (Exception ex)
            {
                _errorMessage = $"TorchSharp 초기화 중 예외 발생: {ex.Message}";
                Debug.WriteLine($"[TorchInitializer] {_errorMessage}");
                Debug.WriteLine($"[TorchInitializer] Exception: {ex}");
                _available = false;
                return false;
            }
        }

        /// <summary>
        /// 안전하게 TorchSharp 기능을 실행
        /// </summary>
        public static bool TryExecute(Action action, out string? error)
        {
            error = null;

            if (!TryInitialize())
            {
                error = _errorMessage;
                return false;
            }

            try
            {
                action();
                return true;
            }
            catch (Exception ex)
            {
                error = $"TorchSharp 실행 중 오류: {ex.Message}";
                Debug.WriteLine($"[TorchInitializer] {error}");
                return false;
            }
        }

        /// <summary>
        /// 안전하게 TorchSharp 기능을 실행하고 결과 반환
        /// </summary>
        public static bool TryExecute<T>(Func<T> func, out T? result, out string? error)
        {
            result = default;
            error = null;

            if (!TryInitialize())
            {
                error = _errorMessage;
                return false;
            }

            try
            {
                result = func();
                return true;
            }
            catch (Exception ex)
            {
                error = $"TorchSharp 실행 중 오류: {ex.Message}";
                Debug.WriteLine($"[TorchInitializer] {error}");
                return false;
            }
        }
    }
}
