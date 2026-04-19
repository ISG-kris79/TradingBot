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
        private bool _initialSimulationMode = false;
        private decimal _initialSimulationBalance = 10000m;

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

            LoadSettings();

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

        // [v5.10.9] btnRunBacktest3Y_Click, btnRunAI_Click, btnRunMtf_Click, btnRunWfo_Click 제거 (설정창 단순화)
        private void _Removed_BacktestHandlers() { }

        private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (e.LeftButton == MouseButtonState.Pressed)
            {
                this.DragMove();
            }
        }

        private void LoadSettings()
        {
            try
            {
                ApplyTelegramUiFromNode(null);
                SelectMajorTrendProfile("Balanced");

                string path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, SettingsFileName);
                if (File.Exists(path))
                {
                    string json = File.ReadAllText(path);
                    _rootNode = JsonNode.Parse(json);
                    if (_rootNode == null)
                    {
                        _rootNode = new JsonObject();
                    }

                    if (_rootNode["Telegram"] is JsonObject telegramNode)
                    {
                        ApplyTelegramUiFromNode(telegramNode);
                    }

                    // Trading Settings
                    var tradingNode = _rootNode["Trading"];
                    if (tradingNode != null)
                    {
                        // GeneralSettings 섹션에서 로드
                        var generalNode = tradingNode["GeneralSettings"];
                        if (generalNode != null)
                        {
                            txtDefaultMargin.Text = generalNode["DefaultMargin"]?.ToString() ?? "200.0";
                            chkEnableMajorTrading.IsChecked = !bool.TryParse(generalNode["EnableMajorTrading"]?.ToString(), out bool enableMaj) || enableMaj;

                            txtPumpMargin.Text = generalNode["PumpMargin"]?.ToString() ?? "200";
                            txtLeverage.Text = generalNode["DefaultLeverage"]?.ToString() ?? "10";
                            txtMaxMajorSlots.Text = generalNode["MaxMajorSlots"]?.ToString() ?? "4";
                            txtMaxPumpSlots.Text = generalNode["MaxPumpSlots"]?.ToString() ?? "3";
                            txtMaxDailyEntries.Text = generalNode["MaxDailyEntries"]?.ToString() ?? "500";
                // [v3.2.14 removed] txtTargetRoe.Text = generalNode["TargetRoe"]?.ToString() ?? "20.0";
                // [v3.2.14 removed] txtStopLossRoe.Text = generalNode["StopLossRoe"]?.ToString() ?? "15.0";
                // [removed] SelectMajorTrendProfile(generalNode["MajorTrendProfile"]?.ToString());
                // [v3.2.14 removed] txtPumpTp1Roe.Text = generalNode["PumpTp1Roe"]?.ToString() ?? "25.0";
                // [v3.2.14 removed] txtPumpTp2Roe.Text = generalNode["PumpTp2Roe"]?.ToString() ?? "50.0";
                // [v3.2.14 removed] txtPumpTimeStopMinutes.Text = generalNode["PumpTimeStopMinutes"]?.ToString() ?? "15.0";
                // [v3.2.14 removed] txtPumpStopWarnPct.Text = generalNode["PumpStopDistanceWarnPct"]?.ToString() ?? "1.0";
                // [v3.2.14 removed] txtPumpStopBlockPct.Text = generalNode["PumpStopDistanceBlockPct"]?.ToString() ?? "1.3";
                            // [메이저/PUMP 완전 분리] PUMP 추가 설정
                // [v3.2.14 removed] txtPumpLeverage.Text = generalNode["PumpLeverage"]?.ToString() ?? "20";
                // [v3.2.14 removed] txtPumpMargin.Text = generalNode["PumpMargin"]?.ToString() ?? "200.0";
                // [v3.2.14 removed] txtPumpBreakEvenRoe.Text = generalNode["PumpBreakEvenRoe"]?.ToString() ?? "20.0";
                // [v3.2.14 removed] txtPumpTrailingStartRoe.Text = generalNode["PumpTrailingStartRoe"]?.ToString() ?? "40.0";
                // [v3.2.14 removed] txtPumpTrailingGapRoe.Text = generalNode["PumpTrailingGapRoe"]?.ToString() ?? "20.0";
                // [v3.2.14 removed] txtPumpStopLossRoe.Text = generalNode["PumpStopLossRoe"]?.ToString() ?? "60.0";
                // [v3.2.14 removed] txtPumpFirstTakeProfitRatioPct.Text = generalNode["PumpFirstTakeProfitRatioPct"]?.ToString() ?? "15.0";
                // [v3.2.14 removed] txtPumpStairStep1Roe.Text = generalNode["PumpStairStep1Roe"]?.ToString() ?? "50.0";
                // [v3.2.14 removed] txtPumpStairStep2Roe.Text = generalNode["PumpStairStep2Roe"]?.ToString() ?? "100.0";
                // [v3.2.14 removed] txtPumpStairStep3Roe.Text = generalNode["PumpStairStep3Roe"]?.ToString() ?? "200.0";
                            // [메이저/PUMP 완전 분리] 메이저 코인 전용 설정
                // [v3.2.14 removed] txtMajorLeverage.Text = generalNode["MajorLeverage"]?.ToString() ?? "20";
                // [v3.2.14 removed] txtMajorMargin — 제거됨
                // [v3.2.14 removed] txtMajorBreakEvenRoe.Text = generalNode["MajorBreakEvenRoe"]?.ToString() ?? "7.0";
                // [v3.2.14 removed] txtMajorTp1Roe.Text = generalNode["MajorTp1Roe"]?.ToString() ?? "20.0";
                // [v3.2.14 removed] txtMajorTp2Roe.Text = generalNode["MajorTp2Roe"]?.ToString() ?? "40.0";
                // [v3.2.14 removed] txtMajorTrailingStartRoe.Text = generalNode["MajorTrailingStartRoe"]?.ToString() ?? "40.0";
                // [v3.2.14 removed] txtMajorTrailingGapRoe.Text = generalNode["MajorTrailingGapRoe"]?.ToString() ?? "5.0";
                // [v3.2.14 removed] txtMajorStopLossRoe.Text = generalNode["MajorStopLossRoe"]?.ToString() ?? "20.0";
                // [removed]                             // 급변 감지 설정
                // [v3.2.14 removed] chkCrashDetectorEnabled.IsChecked = generalNode["CrashDetectorEnabled"]?.GetValue<bool?>() ?? true;
                // [v3.2.14 removed] txtCrashThreshold.Text = generalNode["CrashThresholdPct"]?.ToString() ?? "-1.5";
                // [v3.2.14 removed] txtPumpDetectThreshold.Text = generalNode["PumpDetectThresholdPct"]?.ToString() ?? "1.5";
                // [v3.2.14 removed] txtCrashMinCoinCount.Text = generalNode["CrashMinCoinCount"]?.ToString() ?? "2";
                // [removed] var reverseRatio = generalNode["CrashReverseSizeRatio"]?.GetValue<decimal?>() ?? 0.5m;
                // [v3.2.14 removed] txtCrashReverseSize.Text = (reverseRatio * 100).ToString("F0");
                // [v3.2.14 removed] txtCrashCooldown.Text = generalNode["CrashCooldownSeconds"]?.ToString() ?? "120";
                        }

                        // 시뮬레이션 모드 로드
                        bool isSimulation = tradingNode["IsSimulationMode"]?.GetValue<bool>() ?? false;
                        chkSimulationMode.IsChecked = isSimulation;
                        _initialSimulationMode = isSimulation;

                        // 시뮬레이션 초기 잔고 로드 (기본값 10000)
                        decimal simBalance = 10000m;
                        if (tradingNode["SimulationInitialBalance"] is JsonValue simBalanceNode)
                        {
                            simBalance = simBalanceNode.GetValue<decimal>();
                        }
                        txtSimulationBalance.Text = simBalance.ToString("F2");
                        _initialSimulationBalance = simBalance;
                        
                        // 시뮬레이션 잔고 패널 가시성 설정
                        pnlSimulationBalance.Visibility = isSimulation ? Visibility.Visible : Visibility.Collapsed;
                        
                        // AppConfig에 반영
                        if (AppConfig.Current?.Trading != null)
                        {
                            AppConfig.Current.Trading.SimulationInitialBalance = simBalance;
                        }

                        // Symbols
                        var symbolsNode = tradingNode["Symbols"];
                        if (symbolsNode is JsonArray arr)
                        {
                            txtSymbols.Text = string.Join(",", arr.Where(x => x != null).Select(x => x!.ToString().Trim('"')));
                        }

                    }
                }
                else
                {
                    _rootNode = new JsonObject(); // 파일이 없으면 새로 생성 준비
                }

                // CurrentGeneralSettings는 앱 시작 시 DB에서 이미 로드됨 → JSON보다 우선 적용 (비동기 DB 재쿼리 불필요)
                var currentSettings = MainWindow.CurrentGeneralSettings;
                if (currentSettings != null)
                {
                    txtDefaultMargin.Text = currentSettings.DefaultMargin.ToString("F4");
                    chkEnableMajorTrading.IsChecked = currentSettings.EnableMajorTrading;
                    txtPumpMargin.Text = currentSettings.PumpMargin.ToString("F0");
                    txtLeverage.Text = currentSettings.DefaultLeverage.ToString();
                    txtMaxMajorSlots.Text = currentSettings.MaxMajorSlots.ToString();
                    txtMaxPumpSlots.Text = currentSettings.MaxPumpSlots.ToString();
                    txtMaxDailyEntries.Text = (currentSettings.MaxDailyEntries > 0 ? currentSettings.MaxDailyEntries : 500).ToString();
                    SelectMajorTrendProfile(currentSettings.MajorTrendProfile);
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
                if (!ValidateGeneralInputs(out string generalValidationError))
                {
                    MessageBox.Show(generalValidationError, "입력값 오류", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }


                if (_rootNode == null) _rootNode = new JsonObject();

                // Trading Settings 업데이트
                var tradingNode = (_rootNode["Trading"] as JsonObject) ?? new JsonObject();
                _rootNode["Trading"] = tradingNode;

                // 거래소는 바이낸스로 고정
                tradingNode["SelectedExchange"] = 0;

                // AppConfig에 즉시 반영 (재시작 없이 적용 가능하도록)
                if (AppConfig.Current?.Trading != null)
                {
                    AppConfig.Current.Trading.SelectedExchange = ExchangeType.Binance;
                }

                // GeneralSettings 섹션
                var generalNode = (tradingNode["GeneralSettings"] as JsonObject) ?? new JsonObject();
                tradingNode["GeneralSettings"] = generalNode;

                // GeneralSettings 객체 생성 (DB 저장용)
                var generalSettings = new TradingSettings();

                if (int.TryParse(txtLeverage.Text, out int leverage))
                {
                    generalNode["DefaultLeverage"] = leverage;
                    generalSettings.DefaultLeverage = leverage;
                }

                // [v3.2.14 removed] if (decimal.TryParse(txtTargetRoe.Text, out decimal targetRoe))
                // [removed] {
                // [removed] generalNode["TargetRoe"] = targetRoe;
                // [removed] generalSettings.TargetRoe = targetRoe;
                // [removed] }

                // [v3.2.14 removed] if (decimal.TryParse(txtStopLossRoe.Text, out decimal stopLossRoe))
                // [removed] {
                // [removed] generalNode["StopLossRoe"] = stopLossRoe;
                // [removed] generalSettings.StopLossRoe = stopLossRoe;
                // [removed] }

                // [v3.2.14 removed] MajorTrendProfile — 소스 하드코딩
                generalSettings.MajorTrendProfile = "Balanced";

                // [v3.2.14 removed] if (decimal.TryParse(txtPumpTp1Roe.Text, out decimal pumpTp1Roe))
                // [removed] {
                // [removed] generalNode["PumpTp1Roe"] = pumpTp1Roe;
                // [removed] generalSettings.PumpTp1Roe = pumpTp1Roe;
                // [removed] }

                // [v3.2.14 removed] if (decimal.TryParse(txtPumpTp2Roe.Text, out decimal pumpTp2Roe))
                // [removed] {
                // [removed] generalNode["PumpTp2Roe"] = pumpTp2Roe;
                // [removed] generalSettings.PumpTp2Roe = pumpTp2Roe;
                // [removed] }

                // [v3.2.14 removed] if (decimal.TryParse(txtPumpTimeStopMinutes.Text, out decimal pumpTimeStopMinutes))
                // [removed] {
                // [removed] generalNode["PumpTimeStopMinutes"] = pumpTimeStopMinutes;
                // [removed] generalSettings.PumpTimeStopMinutes = pumpTimeStopMinutes;
                // [removed] }

                // [v3.2.14 removed] if (decimal.TryParse(txtPumpStopWarnPct.Text, out decimal pumpStopWarnPct))
                // [removed] {
                // [removed] generalNode["PumpStopDistanceWarnPct"] = pumpStopWarnPct;
                // [removed] generalSettings.PumpStopDistanceWarnPct = pumpStopWarnPct;
                // [removed] }

                // [v3.2.14 removed] if (decimal.TryParse(txtPumpStopBlockPct.Text, out decimal pumpStopBlockPct))
                // [removed] {
                // [removed] generalNode["PumpStopDistanceBlockPct"] = pumpStopBlockPct;
                // [removed] generalSettings.PumpStopDistanceBlockPct = pumpStopBlockPct;
                // [removed] }

                // [메이저/PUMP 완전 분리] PUMP 추가 설정 저장
                // [v3.2.14 removed] if (int.TryParse(txtPumpLeverage.Text, out int pumpLeverage))
                // [removed] {
                // [removed] generalNode["PumpLeverage"] = pumpLeverage;
                // [removed] generalSettings.PumpLeverage = pumpLeverage;
                // [removed] }

                // [v3.2.14 removed] if (decimal.TryParse(txtPumpMargin.Text, out decimal pumpMargin))
                // [removed] {
                // [removed] generalNode["PumpMargin"] = pumpMargin;
                // [removed] generalSettings.PumpMargin = pumpMargin;
                // [removed] }

                // [v3.2.14 removed] if (decimal.TryParse(txtPumpBreakEvenRoe.Text, out decimal pumpBreakEvenRoe))
                // [removed] {
                // [removed] generalNode["PumpBreakEvenRoe"] = pumpBreakEvenRoe;
                // [removed] generalSettings.PumpBreakEvenRoe = pumpBreakEvenRoe;
                // [removed] }

                // [v3.2.14 removed] if (decimal.TryParse(txtPumpTrailingStartRoe.Text, out decimal pumpTrailingStartRoe))
                // [removed] {
                // [removed] generalNode["PumpTrailingStartRoe"] = pumpTrailingStartRoe;
                // [removed] generalSettings.PumpTrailingStartRoe = pumpTrailingStartRoe;
                // [removed] }

                // [v3.2.14 removed] if (decimal.TryParse(txtPumpTrailingGapRoe.Text, out decimal pumpTrailingGapRoe))
                // [removed] {
                // [removed] generalNode["PumpTrailingGapRoe"] = pumpTrailingGapRoe;
                // [removed] generalSettings.PumpTrailingGapRoe = pumpTrailingGapRoe;
                // [removed] }

                // [v3.2.14 removed] if (decimal.TryParse(txtPumpStopLossRoe.Text, out decimal pumpStopLossRoe))
                // [removed] {
                // [removed] generalNode["PumpStopLossRoe"] = pumpStopLossRoe;
                // [removed] generalSettings.PumpStopLossRoe = pumpStopLossRoe;
                // [removed] }

                // [v3.2.14 removed] if (decimal.TryParse(txtPumpFirstTakeProfitRatioPct.Text, out decimal pumpFirstTakeProfitRatioPct))
                // [removed] {
                // [removed] generalNode["PumpFirstTakeProfitRatioPct"] = pumpFirstTakeProfitRatioPct;
                // [removed] generalSettings.PumpFirstTakeProfitRatioPct = pumpFirstTakeProfitRatioPct;
                // [removed] }

                // [v3.2.14 removed] if (decimal.TryParse(txtPumpStairStep1Roe.Text, out decimal pumpStairStep1Roe))
                // [removed] {
                // [removed] generalNode["PumpStairStep1Roe"] = pumpStairStep1Roe;
                // [removed] generalSettings.PumpStairStep1Roe = pumpStairStep1Roe;
                // [removed] }

                // [v3.2.14 removed] if (decimal.TryParse(txtPumpStairStep2Roe.Text, out decimal pumpStairStep2Roe))
                // [removed] {
                // [removed] generalNode["PumpStairStep2Roe"] = pumpStairStep2Roe;
                // [removed] generalSettings.PumpStairStep2Roe = pumpStairStep2Roe;
                // [removed] }

                // [v3.2.14 removed] if (decimal.TryParse(txtPumpStairStep3Roe.Text, out decimal pumpStairStep3Roe))
                // [removed] {
                // [removed] generalNode["PumpStairStep3Roe"] = pumpStairStep3Roe;
                // [removed] generalSettings.PumpStairStep3Roe = pumpStairStep3Roe;
                // [removed] }

                // [메이저/PUMP 완전 분리] 메이저 코인 전용 설정 저장
                // [v3.2.14 removed] if (int.TryParse(txtMajorLeverage.Text, out int majorLeverage))
                // [removed] {
                // [removed] generalNode["MajorLeverage"] = majorLeverage;
                // [removed] generalSettings.MajorLeverage = majorLeverage;
                // [removed] }

                // [v3.2.14 removed] if (decimal.TryParse(txtMajorMargin.Text, out decimal majorMarginPercent))
                // [removed] {
                // [removed] majorMarginPercent = Math.Clamp(majorMarginPercent, 1.0m, 50.0m);
                // [removed] generalNode["MajorMarginPercent"] = majorMarginPercent;
                // [removed] generalSettings.MajorMarginPercent = majorMarginPercent;
                // [removed] }

                // [v3.2.14 removed] if (decimal.TryParse(txtMajorBreakEvenRoe.Text, out decimal majorBreakEvenRoe))
                // [removed] {
                // [removed] generalNode["MajorBreakEvenRoe"] = majorBreakEvenRoe;
                // [removed] generalSettings.MajorBreakEvenRoe = majorBreakEvenRoe;
                // [removed] }

                // [v3.2.14 removed] if (decimal.TryParse(txtMajorTp1Roe.Text, out decimal majorTp1Roe))
                // [removed] {
                // [removed] generalNode["MajorTp1Roe"] = majorTp1Roe;
                // [removed] generalSettings.MajorTp1Roe = majorTp1Roe;
                // [removed] }

                // [v3.2.14 removed] if (decimal.TryParse(txtMajorTp2Roe.Text, out decimal majorTp2Roe))
                // [removed] {
                // [removed] generalNode["MajorTp2Roe"] = majorTp2Roe;
                // [removed] generalSettings.MajorTp2Roe = majorTp2Roe;
                // [removed] }

                // [v3.2.14 removed] if (decimal.TryParse(txtMajorTrailingStartRoe.Text, out decimal majorTrailingStartRoe))
                // [removed] {
                // [removed] generalNode["MajorTrailingStartRoe"] = majorTrailingStartRoe;
                // [removed] generalSettings.MajorTrailingStartRoe = majorTrailingStartRoe;
                // [removed] }

                // [v3.2.14 removed] if (decimal.TryParse(txtMajorTrailingGapRoe.Text, out decimal majorTrailingGapRoe))
                // [removed] {
                // [removed] generalNode["MajorTrailingGapRoe"] = majorTrailingGapRoe;
                // [removed] generalSettings.MajorTrailingGapRoe = majorTrailingGapRoe;
                // [removed] }

                // [v3.2.14 removed] if (decimal.TryParse(txtMajorStopLossRoe.Text, out decimal majorStopLossRoe))
                // [removed] {
                // [removed] generalNode["MajorStopLossRoe"] = majorStopLossRoe;
                // [removed] generalSettings.MajorStopLossRoe = majorStopLossRoe;
                // [removed] }

                // 급변 감지 설정 저장
                // [v3.2.14 removed] generalSettings.CrashDetectorEnabled = chkCrashDetectorEnabled.IsChecked == true;
                // [removed] generalNode["CrashDetectorEnabled"] = generalSettings.CrashDetectorEnabled;

                // [v3.2.14 removed] if (decimal.TryParse(txtCrashThreshold.Text, out decimal crashThresh))
                // [removed] {
                // [removed] generalNode["CrashThresholdPct"] = crashThresh;
                // [removed] generalSettings.CrashThresholdPct = crashThresh;
                // [removed] }
                // [v3.2.14 removed] if (decimal.TryParse(txtPumpDetectThreshold.Text, out decimal pumpThresh))
                // [removed] {
                // [removed] generalNode["PumpDetectThresholdPct"] = pumpThresh;
                // [removed] generalSettings.PumpDetectThresholdPct = pumpThresh;
                // [removed] }
                // [v3.2.14 removed] if (int.TryParse(txtCrashMinCoinCount.Text, out int minCoin))
                // [removed] {
                // [removed] generalNode["CrashMinCoinCount"] = minCoin;
                // [removed] generalSettings.CrashMinCoinCount = minCoin;
                // [removed] }
                // [v3.2.14 removed] if (decimal.TryParse(txtCrashReverseSize.Text, out decimal reverseSize))
                // [removed] {
                // [removed] generalNode["CrashReverseSizeRatio"] = reverseSize / 100m;
                // [removed] generalSettings.CrashReverseSizeRatio = reverseSize / 100m;
                // [removed] }
                // [v3.2.14 removed] if (int.TryParse(txtCrashCooldown.Text, out int cooldown))
                // [removed] {
                // [removed] generalNode["CrashCooldownSeconds"] = cooldown;
                // [removed] generalSettings.CrashCooldownSeconds = cooldown;
                // [removed] }

                // DefaultMargin 저장
                if (decimal.TryParse(txtDefaultMargin?.Text ?? "200.0", out decimal defaultMargin))
                {
                    generalNode["DefaultMargin"] = defaultMargin;
                    generalSettings.DefaultMargin = defaultMargin;
                }
                // [v5.5.8] 메이저 코인 매매 ON/OFF
                bool enableMajor = chkEnableMajorTrading?.IsChecked ?? true;
                generalNode["EnableMajorTrading"] = enableMajor;
                generalSettings.EnableMajorTrading = enableMajor;

                // [v4.4.4] PUMP 증거금 $
                if (decimal.TryParse(txtPumpMargin?.Text ?? "200", out decimal pumpMargin))
                {
                    generalNode["PumpMargin"] = pumpMargin;
                    generalSettings.PumpMargin = pumpMargin;
                }
                // [v5.7.9] 메이저/PUMP 슬롯
                if (int.TryParse(txtMaxMajorSlots?.Text ?? "4", out int majorSlots))
                {
                    generalSettings.MaxMajorSlots = Math.Clamp(majorSlots, 0, 10);
                    generalNode["MaxMajorSlots"] = generalSettings.MaxMajorSlots;
                }
                if (int.TryParse(txtMaxPumpSlots?.Text ?? "3", out int pumpSlots))
                {
                    generalSettings.MaxPumpSlots = Math.Clamp(pumpSlots, 1, 10);
                    generalNode["MaxPumpSlots"] = generalSettings.MaxPumpSlots;
                }
                // [v5.9.20] 하루 진입 횟수
                if (int.TryParse(txtMaxDailyEntries?.Text ?? "500", out int maxDaily))
                {
                    generalSettings.MaxDailyEntries = Math.Clamp(maxDaily, 1, 10000);
                    generalNode["MaxDailyEntries"] = generalSettings.MaxDailyEntries;
                }

                // TrailingStartRoe, TrailingDropRoe도 저장 (UI에 필드가 없으면 기본값 유지)
                if (generalNode["TrailingStartRoe"] != null &&
                    decimal.TryParse(generalNode["TrailingStartRoe"]?.ToString(), out decimal trailingStart))
                {
                    generalSettings.TrailingStartRoe = trailingStart;
                }

                if (generalNode["TrailingDropRoe"] != null &&
                    decimal.TryParse(generalNode["TrailingDropRoe"]?.ToString(), out decimal trailingDrop))
                {
                    generalSettings.TrailingDropRoe = trailingDrop;
                }

                // [v3.2.14 removed] if (decimal.TryParse(txtRisk.Text, out decimal risk))
                // [removed] tradingNode["RiskPercentage"] = risk;

                // 시뮬레이션 모드 저장
                tradingNode["IsSimulationMode"] = chkSimulationMode.IsChecked == true;

                // 시뮬레이션 초기 잔고 저장
                if (decimal.TryParse(txtSimulationBalance.Text, out decimal simBalance))
                {
                    tradingNode["SimulationInitialBalance"] = simBalance;
                }
                else
                {
                    tradingNode["SimulationInitialBalance"] = 10000m;
                }

                // AppConfig에 즉시 반영
                if (AppConfig.Current?.Trading != null)
                {
                    AppConfig.Current.Trading.IsSimulationMode = chkSimulationMode.IsChecked == true;
                    AppConfig.Current.Trading.SimulationInitialBalance = decimal.TryParse(txtSimulationBalance.Text, out decimal sb) ? sb : 10000m;
                    AppConfig.Current.Trading.GeneralSettings = generalSettings;
                }

                // Symbols 배열 처리
                var symbols = txtSymbols.Text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                var symbolsArray = new JsonArray();
                foreach (var s in symbols) symbolsArray.Add(s);
                tradingNode["Symbols"] = symbolsArray;


                // Telegram 메시지 타입 필터 저장
                var telegramNode = (_rootNode["Telegram"] as JsonObject) ?? new JsonObject();
                _rootNode["Telegram"] = telegramNode;

                telegramNode["EnableAlertMessages"] = chkTelegramAlert.IsChecked == true;
                telegramNode["EnableProfitMessages"] = chkTelegramProfit.IsChecked == true;
                telegramNode["EnableEntryMessages"] = chkTelegramEntry.IsChecked == true;
                telegramNode["EnableAiGateMessages"] = chkTelegramAiGate.IsChecked == true;
                telegramNode["EnableLogMessages"] = chkTelegramLog.IsChecked == true;
                telegramNode["EnableCloseErrorMessages"] = chkTelegramCloseError.IsChecked == true;

                if (AppConfig.Current?.Telegram != null)
                {
                    AppConfig.Current.Telegram.EnableAlertMessages = chkTelegramAlert.IsChecked == true;
                    AppConfig.Current.Telegram.EnableProfitMessages = chkTelegramProfit.IsChecked == true;
                    AppConfig.Current.Telegram.EnableEntryMessages = chkTelegramEntry.IsChecked == true;
                    AppConfig.Current.Telegram.EnableAiGateMessages = chkTelegramAiGate.IsChecked == true;
                    AppConfig.Current.Telegram.EnableLogMessages = chkTelegramLog.IsChecked == true;
                    AppConfig.Current.Telegram.EnableCloseErrorMessages = chkTelegramCloseError.IsChecked == true;
                }


                // 3. 파일 저장
                string path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, SettingsFileName);
                var options = new JsonSerializerOptions
                {
                    WriteIndented = true,
                    NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowNamedFloatingPointLiterals
                };
                File.WriteAllText(path, _rootNode.ToJsonString(options));

                MainWindow.ApplyGeneralSettings(generalSettings);

                // 4. GeneralSettings를 DB에도 저장
                if (_dbManager != null && AppConfig.CurrentUser != null)
                {
                    await _dbManager.SaveGeneralSettingsAsync(AppConfig.CurrentUser.Id, generalSettings);

                    // 시뮬레이션 모드 또는 잔고 변경 확인
                    bool simulationModeChanged = _initialSimulationMode != (chkSimulationMode.IsChecked == true);
                    bool simulationBalanceChanged = _initialSimulationBalance != (decimal.TryParse(txtSimulationBalance.Text, out decimal newBalance) ? newBalance : 10000m);

                    string message = $"✅ [{AppConfig.CurrentUser.Username}]의 설정이 저장되었습니다.";
                    
                    if (simulationModeChanged || simulationBalanceChanged)
                    {
                        message += "\n\n⚠️ 시뮬레이션 설정이 변경되었습니다.\nStop 후 Start를 누르면 새 설정이 즉시 적용됩니다. (앱 재시작 불필요)";
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


        private bool ValidateGeneralInputs(out string errorMessage)
        {
            errorMessage = string.Empty;

            if (!TryParseDecimalInRange(txtDefaultMargin, "기본 마진 (USDT)", 1m, 100000m, out _, out errorMessage))
                return false;

            if (!TryParseIntInRange(txtLeverage, "레버리지", 1, 125, out _, out errorMessage))
                return false;

                // [v3.2.14 removed] if (!TryParseDecimalInRange(txtTargetRoe, "목표 ROE", 0.1m, 500m, out _, out errorMessage))
                // [removed] return false;

                // [v3.2.14 removed] if (!TryParseDecimalInRange(txtStopLossRoe, "손절 ROE", 0.1m, 500m, out _, out errorMessage))
                // [removed] return false;

                // [v3.2.14 removed] if (!TryParseDecimalInRange(txtPumpTp1Roe, "PUMP 1차 익절 ROE", 0.1m, 1000m, out decimal pumpTp1, out errorMessage))
                // [removed] return false;

                // [v3.2.14 removed] if (!TryParseDecimalInRange(txtPumpTp2Roe, "PUMP 2차 익절 ROE", 0.1m, 1000m, out decimal pumpTp2, out errorMessage))
                // [removed] return false;

            // [v3.2.14 removed] pumpTp1/pumpTp2 검증 — 소스 하드코딩

                // [v3.2.14 removed] if (!TryParseDecimalInRange(txtPumpTimeStopMinutes, "PUMP 시간손절(분)", 1m, 1440m, out _, out errorMessage))
                // [removed] return false;

                // [v3.2.14 removed] if (!TryParseDecimalInRange(txtPumpFirstTakeProfitRatioPct, "PUMP 1차 익절 비중(%)", 1m, 95m, out _, out errorMessage))
                // [removed] return false;

                // [removed] // [v3.2.14 removed] if (!TryParseDecimalInRange(txtPumpStairStep1Roe, "PUMP 계단식 구간1 ROE", 1m, 2000m, out decimal pumpStep1, out errorMessage))
                // [removed] return false;

                // [removed] // [v3.2.14 removed] if (!TryParseDecimalInRange(txtPumpStairStep2Roe, "PUMP 계단식 구간2 ROE", 1m, 2000m, out decimal pumpStep2, out errorMessage))
                // [removed] return false;

                // [removed] // [v3.2.14 removed] if (!TryParseDecimalInRange(txtPumpStairStep3Roe, "PUMP 계단식 구간3 ROE", 1m, 2000m, out decimal pumpStep3, out errorMessage))
                // [removed] return false;

                // [v3.2.17] 제거된 검증 블록 정리 완료


            var symbols = txtSymbols.Text
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Where(s => !string.IsNullOrWhiteSpace(s))
                .ToArray();

            if (symbols.Length == 0)
            {
                txtSymbols.Focus();
                errorMessage = "주요 심볼은 최소 1개 이상 입력해야 합니다. (예: BTCUSDT,ETHUSDT)";
                return false;
            }

            if (symbols.Any(s => s.Length < 6 || !s.EndsWith("USDT", StringComparison.OrdinalIgnoreCase)))
            {
                txtSymbols.Focus();
                txtSymbols.SelectAll();
                errorMessage = "심볼 형식이 올바르지 않습니다. 쉼표로 구분하고 각 심볼은 USDT로 끝나야 합니다. (예: BTCUSDT,ETHUSDT)";
                return false;
            }

            // 시뮬레이션 모드 활성화 시 초기 잔고 검증
            if (chkSimulationMode.IsChecked == true)
            {
                if (!TryParseDecimalInRange(txtSimulationBalance, "시뮬레이션 초기 잔고", 1m, 1000000m, out _, out errorMessage))
                    return false;
            }

            return true;
        }

        private void ApplyTelegramUiFromNode(JsonObject? telegramNode)
        {
            chkTelegramAlert.IsChecked = telegramNode?["EnableAlertMessages"]?.GetValue<bool?>() ?? true;
            chkTelegramProfit.IsChecked = telegramNode?["EnableProfitMessages"]?.GetValue<bool?>() ?? true;
            chkTelegramEntry.IsChecked = telegramNode?["EnableEntryMessages"]?.GetValue<bool?>() ?? true;
            chkTelegramAiGate.IsChecked = telegramNode?["EnableAiGateMessages"]?.GetValue<bool?>() ?? true;
            chkTelegramLog.IsChecked = telegramNode?["EnableLogMessages"]?.GetValue<bool?>() ?? true;
            chkTelegramCloseError.IsChecked = telegramNode?["EnableCloseErrorMessages"]?.GetValue<bool?>() ?? true;
        }

        private void SelectMajorTrendProfile(string? profile)
        {
            // [v3.2.14 removed] MajorTrendProfile UI — 소스 하드코딩
        }

        private static bool TryParseIntInRange(TextBox textBox, string fieldName, int min, int max, out int value, out string error)
        {
            if (!int.TryParse(textBox.Text, out value))
            {
                textBox.Focus();
                textBox.SelectAll();
                error = $"{fieldName} 값이 숫자가 아닙니다.";
                return false;
            }

            if (value < min || value > max)
            {
                textBox.Focus();
                textBox.SelectAll();
                error = $"{fieldName} 값은 {min} ~ {max} 범위여야 합니다.";
                return false;
            }

            error = string.Empty;
            return true;
        }

        private static bool TryParseDoubleInRange(TextBox textBox, string fieldName, double min, double max, out double value, out string error)
        {
            if (!double.TryParse(textBox.Text, out value))
            {
                textBox.Focus();
                textBox.SelectAll();
                error = $"{fieldName} 값이 숫자가 아닙니다.";
                return false;
            }

            if (value < min || value > max)
            {
                textBox.Focus();
                textBox.SelectAll();
                error = $"{fieldName} 값은 {min} ~ {max} 범위여야 합니다.";
                return false;
            }

            error = string.Empty;
            return true;
        }

        private static bool TryParseDecimalInRange(TextBox textBox, string fieldName, decimal min, decimal max, out decimal value, out string error)
        {
            if (!decimal.TryParse(textBox.Text, out value))
            {
                textBox.Focus();
                textBox.SelectAll();
                error = $"{fieldName} 값이 숫자가 아닙니다.";
                return false;
            }

            if (value < min || value > max)
            {
                textBox.Focus();
                textBox.SelectAll();
                error = $"{fieldName} 값은 {min} ~ {max} 범위여야 합니다.";
                return false;
            }

            error = string.Empty;
            return true;
        }

        private void chkSimulationMode_Checked(object sender, RoutedEventArgs e)
        {
            if (pnlSimulationBalance != null)
            {
                pnlSimulationBalance.Visibility = Visibility.Visible;
            }
        }

        private void chkSimulationMode_Unchecked(object sender, RoutedEventArgs e)
        {
            if (pnlSimulationBalance != null)
            {
                pnlSimulationBalance.Visibility = Visibility.Collapsed;
            }
        }
    }
}
