using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using TradingBot.Models;
using TradingBot.Services;
using ExchangeType = TradingBot.Shared.Models.ExchangeType;

namespace TradingBot
{
    public partial class SettingsWindow : Window
    {
        private const string SettingsFileName = "appsettings.json";
        private JsonNode? _rootNode;
        private DbManager? _dbManager;
        private int _initialExchangeIndex = 0;

        public SettingsWindow()
        {
            InitializeComponent();

            // DbManager 초기화
            try
            {
                if (!string.IsNullOrEmpty(AppConfig.ConnectionString))
                {
                    _dbManager = new DbManager(AppConfig.ConnectionString);
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"DB 연결 초기화 실패: {ex.Message}", "경고", MessageBoxButton.OK, MessageBoxImage.Warning);
            }

            // 비동기로 설정 로드
            _ = LoadSettingsAsync();

            // 현재 로그인 사용자 정보 표시
            if (AppConfig.CurrentUser != null)
            {
                this.Title = $"환경 설정 - {AppConfig.CurrentUser.Username}";
            }
        }

        private void OnMinimize_Click(object sender, RoutedEventArgs e)
        {
            this.WindowState = WindowState.Minimized;
        }

        private void OnMaximize_Click(object sender, RoutedEventArgs e)
        {
            // This is a fixed size window, so this handler is not used.
        }

        private void OnClose_Click(object sender, RoutedEventArgs e)
        {
            this.Close();
        }

        private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (e.LeftButton == MouseButtonState.Pressed)
            {
                this.DragMove();
            }
        }

        private async System.Threading.Tasks.Task LoadSettingsAsync()
        {
            LoadSettings();

            // DB에서 사용자별 설정 로드
            if (_dbManager != null && AppConfig.CurrentUser != null)
            {
                var dbSettings = await _dbManager.LoadGeneralSettingsAsync(AppConfig.CurrentUser.Id);
                if (dbSettings != null)
                {
                    // DB에서 로드한 설정으로 UI 업데이트
                    txtDefaultMargin.Text = dbSettings.DefaultMargin.ToString("F4");
                    txtLeverage.Text = dbSettings.DefaultLeverage.ToString();
                    txtTargetRoe.Text = dbSettings.TargetRoe.ToString("F4");
                    txtStopLossRoe.Text = dbSettings.StopLossRoe.ToString("F4");
                    txtPumpTp1Roe.Text = dbSettings.PumpTp1Roe.ToString("F4");
                    txtPumpTp2Roe.Text = dbSettings.PumpTp2Roe.ToString("F4");
                    txtPumpTimeStopMinutes.Text = dbSettings.PumpTimeStopMinutes.ToString("F2");
                    txtPumpStopWarnPct.Text = dbSettings.PumpStopDistanceWarnPct.ToString("F3");
                    txtPumpStopBlockPct.Text = dbSettings.PumpStopDistanceBlockPct.ToString("F3");
                }
            }
        }

        private void LoadSettings()
        {
            try
            {
                string path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, SettingsFileName);
                if (File.Exists(path))
                {
                    string json = File.ReadAllText(path);
                    _rootNode = JsonNode.Parse(json);

                    // Trading Settings
                    var tradingNode = _rootNode["Trading"];
                    if (tradingNode != null)
                    {
                        // 거래소 선택 로드
                        int selectedExchange = tradingNode["SelectedExchange"]?.GetValue<int>() ?? 0;
                        _initialExchangeIndex = selectedExchange;
                        cboExchange.SelectedIndex = selectedExchange;

                        // UI 업데이트
                        UpdateExchangeVisibility(selectedExchange);

                        // GeneralSettings 섹션에서 로드
                        var generalNode = tradingNode["GeneralSettings"];
                        if (generalNode != null)
                        {
                            txtDefaultMargin.Text = generalNode["DefaultMargin"]?.ToString() ?? "200.0";
                            txtLeverage.Text = generalNode["DefaultLeverage"]?.ToString() ?? "10";
                            txtTargetRoe.Text = generalNode["TargetRoe"]?.ToString() ?? "20.0";
                            txtStopLossRoe.Text = generalNode["StopLossRoe"]?.ToString() ?? "15.0";
                            txtPumpTp1Roe.Text = generalNode["PumpTp1Roe"]?.ToString() ?? "20.0";
                            txtPumpTp2Roe.Text = generalNode["PumpTp2Roe"]?.ToString() ?? "50.0";
                            txtPumpTimeStopMinutes.Text = generalNode["PumpTimeStopMinutes"]?.ToString() ?? "15.0";
                            txtPumpStopWarnPct.Text = generalNode["PumpStopDistanceWarnPct"]?.ToString() ?? "1.0";
                            txtPumpStopBlockPct.Text = generalNode["PumpStopDistanceBlockPct"]?.ToString() ?? "1.3";
                        }

                        txtRisk.Text = tradingNode["RiskPercentage"]?.ToString() ?? "1.0";

                        // 시뮬레이션 모드 로드
                        bool isSimulation = tradingNode["IsSimulationMode"]?.GetValue<bool>() ?? false;
                        chkSimulationMode.IsChecked = isSimulation;

                        // Symbols
                        var symbolsNode = tradingNode["Symbols"];
                        if (symbolsNode is JsonArray arr)
                        {
                            txtSymbols.Text = string.Join(",", arr.Select(x => x.ToString().Trim('"')));
                        }

                        // [Agent 2] Grid Settings 로드
                        var gridNode = tradingNode["GridSettings"];
                        if (gridNode != null)
                        {
                            txtGridLevels.Text = gridNode["GridLevels"]?.ToString() ?? "10";
                            txtGridSpacing.Text = gridNode["GridSpacingPercentage"]?.ToString() ?? "0.5";
                        }

                        // [Agent 2] Arbitrage Settings 로드
                        var arbNode = tradingNode["ArbitrageSettings"];
                        if (arbNode != null)
                        {
                            bool autoHedge = arbNode["AutoHedge"]?.GetValue<bool>() ?? true;
                            chkAutoHedge.IsChecked = autoHedge;
                        }
                    }
                }
                else
                {
                    _rootNode = new JsonObject(); // 파일이 없으면 새로 생성 준비
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"설정 로드 실패: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private async void btnSave_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (_rootNode == null) _rootNode = new JsonObject();

                // Trading Settings 업데이트
                if (_rootNode["Trading"] == null) _rootNode["Trading"] = new JsonObject();

                // 거래소 선택 저장
                int newExchangeIndex = cboExchange.SelectedIndex;
                _rootNode["Trading"]["SelectedExchange"] = newExchangeIndex;
                bool exchangeChanged = (_initialExchangeIndex != newExchangeIndex);

                // AppConfig에 즉시 반영 (재시작 없이 적용 가능하도록)
                if (exchangeChanged && AppConfig.Current?.Trading != null)
                {
                    AppConfig.Current.Trading.SelectedExchange = (ExchangeType)newExchangeIndex;
                }

                // GeneralSettings 섹션
                if (_rootNode["Trading"]["GeneralSettings"] == null)
                    _rootNode["Trading"]["GeneralSettings"] = new JsonObject();

                // GeneralSettings 객체 생성 (DB 저장용)
                var generalSettings = new TradingSettings();

                if (int.TryParse(txtLeverage.Text, out int leverage))
                {
                    _rootNode["Trading"]["GeneralSettings"]["DefaultLeverage"] = leverage;
                    generalSettings.DefaultLeverage = leverage;
                }

                if (decimal.TryParse(txtTargetRoe.Text, out decimal targetRoe))
                {
                    _rootNode["Trading"]["GeneralSettings"]["TargetRoe"] = targetRoe;
                    generalSettings.TargetRoe = targetRoe;
                }

                if (decimal.TryParse(txtStopLossRoe.Text, out decimal stopLossRoe))
                {
                    _rootNode["Trading"]["GeneralSettings"]["StopLossRoe"] = stopLossRoe;
                    generalSettings.StopLossRoe = stopLossRoe;
                }

                if (decimal.TryParse(txtPumpTp1Roe.Text, out decimal pumpTp1Roe))
                {
                    _rootNode["Trading"]["GeneralSettings"]["PumpTp1Roe"] = pumpTp1Roe;
                    generalSettings.PumpTp1Roe = pumpTp1Roe;
                }

                if (decimal.TryParse(txtPumpTp2Roe.Text, out decimal pumpTp2Roe))
                {
                    _rootNode["Trading"]["GeneralSettings"]["PumpTp2Roe"] = pumpTp2Roe;
                    generalSettings.PumpTp2Roe = pumpTp2Roe;
                }

                if (decimal.TryParse(txtPumpTimeStopMinutes.Text, out decimal pumpTimeStopMinutes))
                {
                    _rootNode["Trading"]["GeneralSettings"]["PumpTimeStopMinutes"] = pumpTimeStopMinutes;
                    generalSettings.PumpTimeStopMinutes = pumpTimeStopMinutes;
                }

                if (decimal.TryParse(txtPumpStopWarnPct.Text, out decimal pumpStopWarnPct))
                {
                    _rootNode["Trading"]["GeneralSettings"]["PumpStopDistanceWarnPct"] = pumpStopWarnPct;
                    generalSettings.PumpStopDistanceWarnPct = pumpStopWarnPct;
                }

                if (decimal.TryParse(txtPumpStopBlockPct.Text, out decimal pumpStopBlockPct))
                {
                    _rootNode["Trading"]["GeneralSettings"]["PumpStopDistanceBlockPct"] = pumpStopBlockPct;
                    generalSettings.PumpStopDistanceBlockPct = pumpStopBlockPct;
                }

                // DefaultMargin 저장 (UI에서 입력받지 않으면 기본값 사용)
                if (decimal.TryParse(txtDefaultMargin?.Text ?? "200.0", out decimal defaultMargin))
                {
                    _rootNode["Trading"]["GeneralSettings"]["DefaultMargin"] = defaultMargin;
                    generalSettings.DefaultMargin = defaultMargin;
                }

                // TrailingStartRoe, TrailingDropRoe도 저장 (UI에 필드가 없으면 기본값 유지)
                if (_rootNode["Trading"]["GeneralSettings"]["TrailingStartRoe"] != null &&
                    decimal.TryParse(_rootNode["Trading"]["GeneralSettings"]["TrailingStartRoe"].ToString(), out decimal trailingStart))
                {
                    generalSettings.TrailingStartRoe = trailingStart;
                }

                if (_rootNode["Trading"]["GeneralSettings"]["TrailingDropRoe"] != null &&
                    decimal.TryParse(_rootNode["Trading"]["GeneralSettings"]["TrailingDropRoe"].ToString(), out decimal trailingDrop))
                {
                    generalSettings.TrailingDropRoe = trailingDrop;
                }

                if (decimal.TryParse(txtRisk.Text, out decimal risk))
                    _rootNode["Trading"]["RiskPercentage"] = risk;

                // 시뮬레이션 모드 저장
                _rootNode["Trading"]["IsSimulationMode"] = chkSimulationMode.IsChecked == true;

                // AppConfig에 즉시 반영
                if (AppConfig.Current?.Trading != null)
                {
                    AppConfig.Current.Trading.IsSimulationMode = chkSimulationMode.IsChecked == true;
                }

                // Symbols 배열 처리
                var symbols = txtSymbols.Text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                var symbolsArray = new JsonArray();
                foreach (var s in symbols) symbolsArray.Add(s);
                _rootNode["Trading"]["Symbols"] = symbolsArray;

                // [Agent 2] Grid Settings 저장
                if (_rootNode["Trading"]["GridSettings"] == null) _rootNode["Trading"]["GridSettings"] = new JsonObject();

                if (int.TryParse(txtGridLevels.Text, out int gridLevels))
                    _rootNode["Trading"]["GridSettings"]["GridLevels"] = gridLevels;
                if (decimal.TryParse(txtGridSpacing.Text, out decimal gridSpacing))
                    _rootNode["Trading"]["GridSettings"]["GridSpacingPercentage"] = gridSpacing;

                // [Agent 2] Arbitrage Settings 저장
                if (_rootNode["Trading"]["ArbitrageSettings"] == null) _rootNode["Trading"]["ArbitrageSettings"] = new JsonObject();

                _rootNode["Trading"]["ArbitrageSettings"]["AutoHedge"] = chkAutoHedge.IsChecked == true;


                // 3. 파일 저장
                string path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, SettingsFileName);
                var options = new JsonSerializerOptions
                {
                    WriteIndented = true,
                    NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowNamedFloatingPointLiterals
                };
                File.WriteAllText(path, _rootNode.ToJsonString(options));

                // 4. GeneralSettings를 DB에도 저장
                if (_dbManager != null && AppConfig.CurrentUser != null)
                {
                    await _dbManager.SaveGeneralSettingsAsync(AppConfig.CurrentUser.Id, generalSettings);

                    string message = $"✅ [{AppConfig.CurrentUser.Username}]의 설정이 저장되었습니다.";
                    if (exchangeChanged)
                    {
                        message += "\n\n⚠️ 거래소 변경이 감지되었습니다.\n거래를 중지하고 다시 시작하면 새 거래소가 적용됩니다.\n(또는 앱을 재시작하세요)";
                    }

                    MessageBox.Show(message, "저장 완료", MessageBoxButton.OK, MessageBoxImage.Information);
                }
                else if (_dbManager != null)
                {
                    MessageBox.Show("⚠️ 현재 사용자 정보를 찾을 수 없습니다.\n설정이 파일에만 저장되었습니다.",
                        "경고", MessageBoxButton.OK, MessageBoxImage.Warning);
                }
                else
                {
                    MessageBox.Show("⚠️ 설정이 파일에만 저장되었습니다.\n(DB 연결 불가)",
                        "저장 완료 (부분)", MessageBoxButton.OK, MessageBoxImage.Warning);
                }

                this.Close();
            }
            catch (Exception ex)
            {
                MessageBox.Show($"설정 저장 실패: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void btnCancel_Click(object sender, RoutedEventArgs e)
        {
            this.Close();
        }

        private void cboExchange_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            UpdateExchangeVisibility(cboExchange.SelectedIndex);
        }

        private void UpdateExchangeVisibility(int index)
        {
            if (pnlBinance == null || pnlBybit == null) return;

            pnlBinance.Visibility = Visibility.Collapsed;
            pnlBybit.Visibility = Visibility.Collapsed;

            switch (index)
            {
                case 0: pnlBinance.Visibility = Visibility.Visible; break; // Binance
                case 1: pnlBybit.Visibility = Visibility.Visible; break;   // Bybit
            }
        }
    }
}
