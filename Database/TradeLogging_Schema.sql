-- ============================================================================
-- 거래 로깅 데이터베이스 스키마
-- 생성일: 2026-03-05
-- 목적: 진입/청산 거래 내역 저장 및 통계 분석
-- ============================================================================

-- 1. TradeLogs 테이블 (진입 및 청산 로그)
IF NOT EXISTS (SELECT * FROM sys.tables WHERE name = 'TradeLogs')
BEGIN
    CREATE TABLE TradeLogs (
        Id BIGINT IDENTITY(1,1) PRIMARY KEY,
        Symbol NVARCHAR(20) NOT NULL,                   -- 거래 심볼 (BTCUSDT, ETHUSDT...)
        Side NVARCHAR(10) NOT NULL,                     -- 거래 방향 (BUY, SELL)
        Strategy NVARCHAR(50) NOT NULL,                 -- 전략 이름 (MAJOR, PUMP, ElliottWave...)
        Price DECIMAL(18, 8) NOT NULL,                  -- 체결 가격
        AiScore FLOAT NOT NULL DEFAULT 0,               -- AI 점수 (0~100)
        Time DATETIME2 NOT NULL DEFAULT GETDATE(),      -- 거래 시각
        PnL DECIMAL(18, 4) NOT NULL DEFAULT 0,          -- 실현 손익 (USDT)
        PnLPercent DECIMAL(18, 4) NOT NULL DEFAULT 0,   -- 수익률 (ROE %)
        
        INDEX IX_TradeLogs_Symbol (Symbol),
        INDEX IX_TradeLogs_Time (Time DESC),
        INDEX IX_TradeLogs_Strategy (Strategy)
    );
    
    PRINT '✅ TradeLogs 테이블 생성 완료';
END
ELSE
BEGIN
    PRINT '⚠️ TradeLogs 테이블이 이미 존재합니다';
END
GO

-- 2. TradeHistory 테이블 (진입~청산 쌍 기록)
IF NOT EXISTS (SELECT * FROM sys.tables WHERE name = 'TradeHistory')
BEGIN
    CREATE TABLE TradeHistory (
        Id BIGINT IDENTITY(1,1) PRIMARY KEY,
        Symbol NVARCHAR(20) NOT NULL,                   -- 거래 심볼
        Side NVARCHAR(10) NOT NULL,                     -- 포지션 방향 (BUY, SELL)
        EntryPrice DECIMAL(18, 8) NOT NULL,             -- 진입 가격
        ExitPrice DECIMAL(18, 8) NOT NULL,              -- 청산 가격
        Quantity DECIMAL(18, 8) NOT NULL,               -- 거래 수량
        PnL DECIMAL(18, 4) NOT NULL,                    -- 실현 손익 (USDT)
        PnLPercent DECIMAL(18, 4) NOT NULL,             -- 수익률 (ROE %)
        ExitReason NVARCHAR(100),                       -- 청산 사유
        EntryTime DATETIME2 NOT NULL,                   -- 진입 시각
        ExitTime DATETIME2 NOT NULL DEFAULT GETDATE(),  -- 청산 시각
        HoldingMinutes AS DATEDIFF(MINUTE, EntryTime, ExitTime), -- 보유 시간 (계산 컬럼)
        
        INDEX IX_TradeHistory_Symbol (Symbol),
        INDEX IX_TradeHistory_ExitTime (ExitTime DESC),
        INDEX IX_TradeHistory_EntryTime (EntryTime DESC)
    );
    
    PRINT '✅ TradeHistory 테이블 생성 완료';
END
ELSE
BEGIN
    PRINT '⚠️ TradeHistory 테이블이 이미 존재합니다';
END
GO

-- 3. 통계 뷰: 심볼별 거래 성적
IF EXISTS (SELECT * FROM sys.views WHERE name = 'vw_TradeStatisticsBySymbol')
    DROP VIEW vw_TradeStatisticsBySymbol;
GO

CREATE VIEW vw_TradeStatisticsBySymbol AS
SELECT 
    Symbol,
    COUNT(*) AS TotalTrades,
    SUM(CASE WHEN PnL > 0 THEN 1 ELSE 0 END) AS WinTrades,
    SUM(CASE WHEN PnL < 0 THEN 1 ELSE 0 END) AS LossTrades,
    CAST(SUM(CASE WHEN PnL > 0 THEN 1 ELSE 0 END) * 100.0 / NULLIF(COUNT(*), 0) AS DECIMAL(10,2)) AS WinRate,
    CAST(SUM(PnL) AS DECIMAL(18,2)) AS TotalPnL,
    CAST(AVG(PnL) AS DECIMAL(18,2)) AS AvgPnL,
    CAST(AVG(PnLPercent) AS DECIMAL(10,2)) AS AvgROE,
    CAST(AVG(HoldingMinutes) AS DECIMAL(10,2)) AS AvgHoldingMinutes,
    MIN(EntryTime) AS FirstTradeTime,
    MAX(ExitTime) AS LastTradeTime
FROM TradeHistory
GROUP BY Symbol;
GO

PRINT '✅ vw_TradeStatisticsBySymbol 뷰 생성 완료';
GO

-- 4. 통계 뷰: 일별 거래 성적
IF EXISTS (SELECT * FROM sys.views WHERE name = 'vw_DailyTradeStatistics')
    DROP VIEW vw_DailyTradeStatistics;
GO

CREATE VIEW vw_DailyTradeStatistics AS
SELECT 
    CAST(ExitTime AS DATE) AS TradeDate,
    COUNT(*) AS TotalTrades,
    SUM(CASE WHEN PnL > 0 THEN 1 ELSE 0 END) AS WinTrades,
    CAST(SUM(CASE WHEN PnL > 0 THEN 1 ELSE 0 END) * 100.0 / NULLIF(COUNT(*), 0) AS DECIMAL(10,2)) AS WinRate,
    CAST(SUM(PnL) AS DECIMAL(18,2)) AS DailyPnL,
    CAST(AVG(PnL) AS DECIMAL(18,2)) AS AvgPnL,
    MAX(PnL) AS BestTrade,
    MIN(PnL) AS WorstTrade
FROM TradeHistory
GROUP BY CAST(ExitTime AS DATE);
GO

PRINT '✅ vw_DailyTradeStatistics 뷰 생성 완료';
GO

PRINT '============================================================================';
PRINT '🎉 거래 로깅 데이터베이스 스키마 생성 완료!';
PRINT '============================================================================';
PRINT '';
PRINT '생성된 테이블:';
PRINT '  1. TradeLogs - 모든 진입/청산 개별 로그';
PRINT '  2. TradeHistory - 진입~청산 거래 쌍 기록';
PRINT '';
PRINT '생성된 뷰:';
PRINT '  1. vw_TradeStatisticsBySymbol - 심볼별 통계';
PRINT '  2. vw_DailyTradeStatistics - 일별 통계';
PRINT '';
PRINT '사용법:';
PRINT '  -- 최근 거래 조회';
PRINT '  SELECT TOP 20 * FROM TradeLogs ORDER BY Time DESC;';
PRINT '  SELECT TOP 20 * FROM TradeHistory ORDER BY ExitTime DESC;';
PRINT '';
PRINT '  -- 통계 조회';
PRINT '  SELECT * FROM vw_TradeStatisticsBySymbol;';
PRINT '  SELECT * FROM vw_DailyTradeStatistics ORDER BY TradeDate DESC;';
PRINT '============================================================================';
