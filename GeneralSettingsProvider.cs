using System;
using System.Threading.Tasks;
using TradingBot.Models;
using TradingBot.Services;

namespace TradingBot
{
    /// <summary>
    /// GeneralSettings 제공 및 관리 클래스 (싱글톤 패턴)
    /// </summary>
    public class GeneralSettingsProvider
    {
        private static GeneralSettingsProvider? _instance;
        private static readonly object _lock = new object();

        private TradingSettings _currentSettings;
        private DbManager? _dbManager;

        public static GeneralSettingsProvider Instance
        {
            get
            {
                if (_instance == null)
                {
                    lock (_lock)
                    {
                        if (_instance == null)
                        {
                            _instance = new GeneralSettingsProvider();
                        }
                    }
                }
                return _instance;
            }
        }

        private GeneralSettingsProvider()
        {
            _currentSettings = new TradingSettings();

            // DbManager 초기화
            if (!string.IsNullOrEmpty(AppConfig.ConnectionString))
            {
                _dbManager = new DbManager(AppConfig.ConnectionString);
            }
        }

        /// <summary>
        /// 현재 GeneralSettings 가져오기
        /// </summary>
        public TradingSettings GetSettings()
        {
            // MainWindow의 캐시된 설정 사용
            return MainWindow.CurrentGeneralSettings ?? _currentSettings;
        }

        /// <summary>
        /// GeneralSettings를 DB 및 appsettings.json에서 새로고침
        /// </summary>
        public async Task<TradingSettings> RefreshSettingsAsync()
        {
            try
            {
                // 1. appsettings.json 기본값
                if (AppConfig.Current?.Trading?.GeneralSettings != null)
                {
                    _currentSettings = AppConfig.Current.Trading.GeneralSettings;
                }

                // 2. DB 사용자 설정 (우선순위 높음)
                if (_dbManager != null && AppConfig.CurrentUser != null)
                {
                    var dbSettings = await _dbManager.LoadGeneralSettingsAsync(AppConfig.CurrentUser.Id);
                    if (dbSettings != null)
                    {
                        _currentSettings = dbSettings;
                    }
                }

                // MainWindow 캐시 업데이트 (있는 경우)
                if (MainWindow.Instance != null)
                {
                    // Reflection으로 private setter 우회 (또는 public setter로 변경)
                    // 여기서는 간단히 직접 할당 (MainWindow.CurrentGeneralSettings의 setter를 public으로 변경 필요)
                }

                return _currentSettings;
            }
            catch (Exception ex)
            {
                MainWindow.Instance?.AddLog($"[GeneralSettings] ❌ 설정 새로고침 실패: {ex.Message}");
                return _currentSettings;
            }
        }

        /// <summary>
        /// GeneralSettings를 DB에 저장
        /// </summary>
        public async Task<bool> SaveSettingsAsync(TradingSettings settings)
        {
            try
            {
                if (_dbManager == null || AppConfig.CurrentUser == null)
                {
                    MainWindow.Instance?.AddLog("[GeneralSettings] ⚠️ DB 또는 사용자 정보 없음. 저장 불가");
                    return false;
                }

                await _dbManager.SaveGeneralSettingsAsync(AppConfig.CurrentUser.Id, settings);
                _currentSettings = settings;

                MainWindow.Instance?.AddLog("[GeneralSettings] ✅ 설정 저장 완료");
                return true;
            }
            catch (Exception ex)
            {
                MainWindow.Instance?.AddLog($"[GeneralSettings] ❌ 설정 저장 실패: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// 설정값 빠른 접근 헬퍼
        /// </summary>
        public int DefaultLeverage => GetSettings().DefaultLeverage;
        public decimal DefaultMargin => GetSettings().DefaultMargin;
        public decimal TargetRoe => GetSettings().TargetRoe;
        public decimal StopLossRoe => GetSettings().StopLossRoe;
        public decimal TrailingStartRoe => GetSettings().TrailingStartRoe;
        public decimal TrailingDropRoe => GetSettings().TrailingDropRoe;
    }
}
