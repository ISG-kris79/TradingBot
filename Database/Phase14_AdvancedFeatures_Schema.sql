-- ============================================================================
-- Phase 14: 고급 거래 기능 데이터베이스 스키마
-- 생성일: 2026-02-28
-- 목적: 차익거래, 자금 이동, 포트폴리오 리밸런싱 로그 저장
-- ============================================================================

-- 1. 차익거래 실행 로그 테이블
IF NOT EXISTS (SELECT * FROM sys.tables WHERE name = 'ArbitrageExecutionLog')
BEGIN
    CREATE TABLE ArbitrageExecutionLog (
        Id BIGINT IDENTITY(1,1) PRIMARY KEY,
        Symbol NVARCHAR(20) NOT NULL,                   -- 거래 심볼 (예: BTC/USDT)
        BuyExchange NVARCHAR(20) NOT NULL,              -- 매수 거래소
        SellExchange NVARCHAR(20) NOT NULL,             -- 매도 거래소
        BuyPrice DECIMAL(18, 8) NOT NULL,               -- 매수 가격
        SellPrice DECIMAL(18, 8) NOT NULL,              -- 매도 가격
        Quantity DECIMAL(18, 8) NOT NULL,               -- 거래 수량
        ProfitPercent DECIMAL(10, 4) NOT NULL,          -- 수익률 (%)
        BuyOrderId NVARCHAR(100),                       -- 매수 주문 ID
        SellOrderId NVARCHAR(100),                      -- 매도 주문 ID
        BuySuccess BIT NOT NULL DEFAULT 0,              -- 매수 성공 여부
        SellSuccess BIT NOT NULL DEFAULT 0,             -- 매도 성공 여부
        Success BIT NOT NULL DEFAULT 0,                 -- 전체 성공 여부
        ErrorMessage NVARCHAR(MAX),                     -- 오류 메시지
        StartTime DATETIME2 NOT NULL,                   -- 시작 시각
        EndTime DATETIME2,                              -- 종료 시각
        CreatedAt DATETIME2 DEFAULT GETDATE(),          -- 레코드 생성 시각
        
        INDEX IX_ArbitrageLog_Symbol (Symbol),
        INDEX IX_ArbitrageLog_CreatedAt (CreatedAt DESC),
        INDEX IX_ArbitrageLog_Success (Success)
    );
    
    PRINT '✅ ArbitrageExecutionLog 테이블 생성 완료';
END
ELSE
BEGIN
    PRINT '⚠️ ArbitrageExecutionLog 테이블이 이미 존재합니다';
END
GO

-- 2. 자금 이동 로그 테이블
IF NOT EXISTS (SELECT * FROM sys.tables WHERE name = 'FundTransferLog')
BEGIN
    CREATE TABLE FundTransferLog (
        Id BIGINT IDENTITY(1,1) PRIMARY KEY,
        FromExchange NVARCHAR(20) NOT NULL,             -- 출금 거래소
        ToExchange NVARCHAR(20) NOT NULL,               -- 입금 거래소
        Asset NVARCHAR(20) NOT NULL DEFAULT 'USDT',     -- 자산 유형
        Amount DECIMAL(18, 8) NOT NULL,                 -- 이동 금액
        WithdrawSuccess BIT NOT NULL DEFAULT 0,         -- 출금 성공 여부
        DepositSuccess BIT NOT NULL DEFAULT 0,          -- 입금 성공 여부
        Success BIT NOT NULL DEFAULT 0,                 -- 전체 성공 여부
        WithdrawTxId NVARCHAR(200),                     -- 출금 트랜잭션 ID
        DepositTxId NVARCHAR(200),                      -- 입금 트랜잭션 ID
        ErrorMessage NVARCHAR(MAX),                     -- 오류 메시지
        RequestTime DATETIME2 NOT NULL,                 -- 요청 시각
        StartTime DATETIME2 NOT NULL,                   -- 시작 시각
        EndTime DATETIME2,                              -- 종료 시각
        CreatedAt DATETIME2 DEFAULT GETDATE(),          -- 레코드 생성 시각
        
        INDEX IX_FundTransferLog_Asset (Asset),
        INDEX IX_FundTransferLog_CreatedAt (CreatedAt DESC),
        INDEX IX_FundTransferLog_Success (Success)
    );
    
    PRINT '✅ FundTransferLog 테이블 생성 완료';
END
ELSE
BEGIN
    PRINT '⚠️ FundTransferLog 테이블이 이미 존재합니다';
END
GO

-- 3. 포트폴리오 리밸런싱 로그 테이블
IF NOT EXISTS (SELECT * FROM sys.tables WHERE name = 'PortfolioRebalancingLog')
BEGIN
    CREATE TABLE PortfolioRebalancingLog (
        Id BIGINT IDENTITY(1,1) PRIMARY KEY,
        TotalValue DECIMAL(18, 8) NOT NULL,             -- 총 포트폴리오 가치
        ActionCount INT NOT NULL DEFAULT 0,             -- 실행된 액션 수
        Success BIT NOT NULL DEFAULT 0,                 -- 성공 여부
        ErrorMessage NVARCHAR(MAX),                     -- 오류 메시지
        StartTime DATETIME2 NOT NULL,                   -- 시작 시각
        EndTime DATETIME2,                              -- 종료 시각
        CreatedAt DATETIME2 DEFAULT GETDATE(),          -- 레코드 생성 시각
        
        INDEX IX_RebalancingLog_CreatedAt (CreatedAt DESC),
        INDEX IX_RebalancingLog_Success (Success)
    );
    
    PRINT '✅ PortfolioRebalancingLog 테이블 생성 완료';
END
ELSE
BEGIN
    PRINT '⚠️ PortfolioRebalancingLog 테이블이 이미 존재합니다';
END
GO

-- 4. 리밸런싱 액션 상세 테이블 (리밸런싱 로그의 자식 테이블)
IF NOT EXISTS (SELECT * FROM sys.tables WHERE name = 'RebalancingAction')
BEGIN
    CREATE TABLE RebalancingAction (
        Id BIGINT IDENTITY(1,1) PRIMARY KEY,
        RebalancingLogId BIGINT NOT NULL,               -- 부모 로그 ID
        Asset NVARCHAR(20) NOT NULL,                    -- 자산명 (BTC, ETH...)
        CurrentPercentage DECIMAL(10, 4) NOT NULL,      -- 현재 비율 (%)
        TargetPercentage DECIMAL(10, 4) NOT NULL,       -- 목표 비율 (%)
        Deviation DECIMAL(10, 4) NOT NULL,              -- 편차 (%)
        Action NVARCHAR(10) NOT NULL,                   -- 액션 (매수/매도)
        TargetValue DECIMAL(18, 8) NOT NULL,            -- 목표 가치
        OrderId NVARCHAR(100),                          -- 주문 ID
        Executed BIT NOT NULL DEFAULT 0,                -- 실행 여부
        CreatedAt DATETIME2 DEFAULT GETDATE(),          -- 레코드 생성 시각
        
        CONSTRAINT FK_RebalancingAction_Log FOREIGN KEY (RebalancingLogId)
            REFERENCES PortfolioRebalancingLog(Id) ON DELETE CASCADE,
        
        INDEX IX_RebalancingAction_LogId (RebalancingLogId),
        INDEX IX_RebalancingAction_Asset (Asset)
    );
    
    PRINT '✅ RebalancingAction 테이블 생성 완료';
END
ELSE
BEGIN
    PRINT '⚠️ RebalancingAction 테이블이 이미 존재합니다';
END
GO

-- 5. 통계 뷰 생성: 차익거래 요약
IF EXISTS (SELECT * FROM sys.views WHERE name = 'vw_ArbitrageStatistics')
    DROP VIEW vw_ArbitrageStatistics;
GO

CREATE VIEW vw_ArbitrageStatistics AS
SELECT 
    Symbol,
    COUNT(*) AS TotalExecutions,
    SUM(CASE WHEN Success = 1 THEN 1 ELSE 0 END) AS SuccessCount,
    CAST(SUM(CASE WHEN Success = 1 THEN 1 ELSE 0 END) * 100.0 / COUNT(*) AS DECIMAL(10,2)) AS SuccessRate,
    AVG(ProfitPercent) AS AvgProfitPercent,
    MAX(ProfitPercent) AS MaxProfitPercent,
    MIN(ProfitPercent) AS MinProfitPercent,
    SUM(CASE WHEN Success = 1 THEN Quantity * BuyPrice ELSE 0 END) AS TotalVolume
FROM ArbitrageExecutionLog
GROUP BY Symbol;
GO

PRINT '✅ vw_ArbitrageStatistics 뷰 생성 완료';
GO

-- 6. 통계 뷰 생성: 자금 이동 요약
IF EXISTS (SELECT * FROM sys.views WHERE name = 'vw_FundTransferStatistics')
    DROP VIEW vw_FundTransferStatistics;
GO

CREATE VIEW vw_FundTransferStatistics AS
SELECT 
    Asset,
    COUNT(*) AS TotalTransfers,
    SUM(CASE WHEN Success = 1 THEN 1 ELSE 0 END) AS SuccessCount,
    CAST(SUM(CASE WHEN Success = 1 THEN 1 ELSE 0 END) * 100.0 / COUNT(*) AS DECIMAL(10,2)) AS SuccessRate,
    SUM(Amount) AS TotalAmount,
    AVG(Amount) AS AvgAmount
FROM FundTransferLog
GROUP BY Asset;
GO

PRINT '✅ vw_FundTransferStatistics 뷰 생성 완료';
GO

-- 7. 통계 뷰 생성: 리밸런싱 요약
IF EXISTS (SELECT * FROM sys.views WHERE name = 'vw_RebalancingStatistics')
    DROP VIEW vw_RebalancingStatistics;
GO

CREATE VIEW vw_RebalancingStatistics AS
SELECT 
    COUNT(*) AS TotalRebalancings,
    SUM(CASE WHEN Success = 1 THEN 1 ELSE 0 END) AS SuccessCount,
    CAST(SUM(CASE WHEN Success = 1 THEN 1 ELSE 0 END) * 100.0 / COUNT(*) AS DECIMAL(10,2)) AS SuccessRate,
    AVG(ActionCount) AS AvgActionCount,
    AVG(TotalValue) AS AvgPortfolioValue
FROM PortfolioRebalancingLog;
GO

PRINT '✅ vw_RebalancingStatistics 뷰 생성 완료';
GO

PRINT '============================================================================';
PRINT '🎉 Phase 14 고급 기능 데이터베이스 스키마 생성 완료!';
PRINT '============================================================================';
PRINT '';
PRINT '생성된 테이블:';
PRINT '  1. ArbitrageExecutionLog - 차익거래 실행 로그';
PRINT '  2. FundTransferLog - 자금 이동 로그';
PRINT '  3. PortfolioRebalancingLog - 포트폴리오 리밸런싱 로그';
PRINT '  4. RebalancingAction - 리밸런싱 액션 상세';
PRINT '';
PRINT '생성된 뷰:';
PRINT '  1. vw_ArbitrageStatistics - 차익거래 통계';
PRINT '  2. vw_FundTransferStatistics - 자금 이동 통계';
PRINT '  3. vw_RebalancingStatistics - 리밸런싱 통계';
PRINT '';
