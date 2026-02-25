using System;
using Newtonsoft.Json;
using System.Configuration;
using System.IO;
using System.Windows;
using TradingBot.Services;

namespace TradingBot
{
    /// <summary>
    /// Interaction logic for App.xaml
    /// </summary>
    public partial class App : Application
    {
        public App()
        {
            // [Fix] 'Root level is invalid' 오류 자동 복구 (user.config 손상 시 초기화)
            try
            {
                // 구성 시스템을 강제로 로드하여 손상 여부 확인
                ConfigurationManager.OpenExeConfiguration(ConfigurationUserLevel.PerUserRoamingAndLocal);
            }
            catch (ConfigurationErrorsException ex)
            {
                string filename = ex.Filename;
                if (!string.IsNullOrEmpty(filename) && File.Exists(filename))
                {
                    File.Delete(filename); // 손상된 파일 삭제
                }
            }
        }

        protected override async void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);
            AppConfig.Load();
            LoggerService.Initialize();

            // 자동 로그인 체크
            CheckAutoLogin();
        }

        private async void CheckAutoLogin()
        {
            string configFile = "login.config";
            bool autoLoginSuccess = false;

            if (File.Exists(configFile))
            {
                try
                {
                    var json = File.ReadAllText(configFile);
                    dynamic config = JsonConvert.DeserializeObject(json);
                    string username = config.Username;
                    string token = config.Token; // PasswordHash

                    var db = new DatabaseService();
                    var user = await db.LoginUserAsync(username, token);

                    if (user != null)
                    {
                        AppConfig.SetUserCredentials(
                            SecurityService.DecryptString(user.BinanceApiKey),
                            SecurityService.DecryptString(user.BinanceApiSecret),
                            SecurityService.DecryptString(user.TelegramBotToken),
                            SecurityService.DecryptString(user.TelegramChatId),
                            user.Username,
                            SecurityService.DecryptString(user.BitgetApiKey),
                            SecurityService.DecryptString(user.BitgetApiSecret),
                            SecurityService.DecryptString(user.BitgetPassphrase)
                        );

                        TelegramService.Instance.Initialize(); // 키 설정 후 초기화
                        new MainWindow().Show();
                        autoLoginSuccess = true;
                    }
                }
                catch { /* 자동 로그인 실패 시 무시하고 로그인 창 띄움 */ }
            }

            if (!autoLoginSuccess)
            {
                new LoginWindow().Show();
            }
        }
    }
}
