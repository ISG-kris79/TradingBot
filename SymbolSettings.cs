namespace TradingBot.Models
{
    // [Agent 2] 심볼별 독립 설정 모델
    public class SymbolSettings
    {
        public string Symbol { get; set; } = "";
        public int Leverage { get; set; } = 10;
        public decimal MarginAmount { get; set; } = 100; // 투입 증거금 (USDT)
        public bool UseAI { get; set; } = true;          // AI 사용 여부
        public float AiThreshold { get; set; } = 0.6f;   // AI 진입 임계값 (0.0 ~ 1.0)
        public bool IsTradingEnabled { get; set; } = true; // 해당 심볼 거래 활성화 여부
        public string StrategyType { get; set; } = "Standard"; // Standard, Aggressive, Conservative

        // [Agent 2] 전략별 세부 설정 추가
        public decimal GridStepPercent { get; set; } = 1.0m;   // 그리드 간격 (%)
        public int MaxGridLevels { get; set; } = 5;            // 최대 그리드 레벨
        public decimal ArbitrageThreshold { get; set; } = 0.5m; // 차익거래 진입 괴리율 (%)
    }
}