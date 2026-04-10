-- compare-before-after-RSI-SYMBOLS.sql
SELECT Symbol, COUNT(*) AS Cnt
FROM LiveLogs
WHERE [Timestamp] >= '2026-03-18 18:42:20'
  AND UNICODE(SUBSTRING(Message,1,1))=9940
  AND Message LIKE '%RSI=%'
GROUP BY Symbol
ORDER BY Cnt DESC;
