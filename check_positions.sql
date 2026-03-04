-- BTC, ETH, SOL 최근 거래 로그 조회
SELECT TOP 20
    Symbol,
    Side,
    Strategy,
    Price,
    AiScore,
    Time,
    PnL,
    PnLPercent,
    CASE 
        WHEN PnL > 0 THEN '✅ 수익'
        WHEN PnL < 0 THEN '❌ 손실'
        ELSE '⚪ 보합'
    END AS Status
FROM TradeLogs
WHERE Symbol IN ('BTCUSDT', 'ETHUSDT', 'SOLUSDT')
ORDER BY Time DESC;

-- 심볼별 통계
SELECT 
    Symbol,
    COUNT(*) AS 거래횟수,
    SUM(CASE WHEN PnL > 0 THEN 1 ELSE 0 END) AS 수익거래,
    SUM(CASE WHEN PnL < 0 THEN 1 ELSE 0 END) AS 손실거래,
    CAST(SUM(CASE WHEN PnL > 0 THEN 1 ELSE 0 END) * 100.0 / COUNT(*) AS DECIMAL(5,2)) AS 승률,
    CAST(SUM(PnL) AS DECIMAL(18,2)) AS 누적손익,
    CAST(AVG(PnL) AS DECIMAL(18,2)) AS 평균손익
FROM TradeLogs
WHERE Symbol IN ('BTCUSDT', 'ETHUSDT', 'SOLUSDT')
GROUP BY Symbol
ORDER BY Symbol;

-- 최근 1시간 이내 거래
SELECT 
    Symbol,
    Side,
    Price,
    Time,
    PnL,
    DATEDIFF(MINUTE, Time, GETDATE()) AS 경과분
FROM TradeLogs
WHERE Symbol IN ('BTCUSDT', 'ETHUSDT', 'SOLUSDT')
    AND Time >= DATEADD(HOUR, -1, GETDATE())
ORDER BY Time DESC;
