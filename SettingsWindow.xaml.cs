using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;

namespace TradingBot
{
    public partial class SettingsWindow : Window
    {
        private const string SettingsFileName = "appsettings.json";
        private JsonNode? _rootNode;

        public SettingsWindow()
        {
            InitializeComponent();
            LoadSettings();
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

                    // DB Connection String
                    if (_rootNode["ConnectionStrings"]?["DefaultConnection"] != null)
                    {
                        txtConnectionString.Text = _rootNode["ConnectionStrings"]["DefaultConnection"].GetValue<string>();
                    }

                    // Trading Settings
                    var tradingNode = _rootNode["Trading"];
                    if (tradingNode != null)
                    {
                        // 거래소 선택 로드
                        int selectedExchange = tradingNode["SelectedExchange"]?.GetValue<int>() ?? 0;
                        cboExchange.SelectedIndex = selectedExchange;
                        
                        // UI 업데이트
                        UpdateExchangeVisibility(selectedExchange);

                        // GeneralSettings 섹션에서 로드
                        var generalNode = tradingNode["GeneralSettings"];
                        if (generalNode != null)
                        {
                            txtLeverage.Text = generalNode["DefaultLeverage"]?.ToString() ?? "10";
                            txtTargetRoe.Text = generalNode["TargetRoe"]?.ToString() ?? "20.0";
                            txtStopLossRoe.Text = generalNode["StopLossRoe"]?.ToString() ?? "15.0";
                        }

                        txtRisk.Text = tradingNode["RiskPercentage"]?.ToString() ?? "1.0";

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

        private void btnSave_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (_rootNode == null) _rootNode = new JsonObject();

                // 1. Connection String 업데이트
                if (_rootNode["ConnectionStrings"] == null) _rootNode["ConnectionStrings"] = new JsonObject();
                _rootNode["ConnectionStrings"]["DefaultConnection"] = txtConnectionString.Text;

                // 2. Trading Settings 업데이트
                if (_rootNode["Trading"] == null) _rootNode["Trading"] = new JsonObject();
                
                // 거래소 선택 저장
                _rootNode["Trading"]["SelectedExchange"] = cboExchange.SelectedIndex;

                // GeneralSettings 섹션
                if (_rootNode["Trading"]["GeneralSettings"] == null) 
                    _rootNode["Trading"]["GeneralSettings"] = new JsonObject();

                if (int.TryParse(txtLeverage.Text, out int leverage))
                    _rootNode["Trading"]["GeneralSettings"]["DefaultLeverage"] = leverage;

                if (decimal.TryParse(txtTargetRoe.Text, out decimal targetRoe))
                    _rootNode["Trading"]["GeneralSettings"]["TargetRoe"] = targetRoe;

                if (decimal.TryParse(txtStopLossRoe.Text, out decimal stopLossRoe))
                    _rootNode["Trading"]["GeneralSettings"]["StopLossRoe"] = stopLossRoe;

                if (decimal.TryParse(txtRisk.Text, out decimal risk))
                    _rootNode["Trading"]["RiskPercentage"] = risk;

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
                var options = new JsonSerializerOptions { WriteIndented = true };
                File.WriteAllText(path, _rootNode.ToJsonString(options));

                MessageBox.Show("설정이 저장되었습니다.\n변경 사항을 적용하려면 앱을 재시작하세요.", "저장 완료", MessageBoxButton.OK, MessageBoxImage.Information);
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
            if (pnlBinance == null || pnlBybit == null || pnlBitget == null) return;

            pnlBinance.Visibility = Visibility.Collapsed;
            pnlBybit.Visibility = Visibility.Collapsed;
            pnlBitget.Visibility = Visibility.Collapsed;

            switch (index)
            {
                case 0: pnlBinance.Visibility = Visibility.Visible; break; // Binance
                case 1: pnlBybit.Visibility = Visibility.Visible; break;   // Bybit
                case 2: pnlBitget.Visibility = Visibility.Visible; break;  // Bitget
            }
        }
    }
}