-- compare-before-after-HOURLY.sql
SELECT DATEPART(HOUR,[Timestamp]) AS hr,
  COUNT(*) AS TotalLogs_hr,
  SUM(CASE WHEN UNICODE(SUBSTRING(Message,1,1))=9940 THEN 1 ELSE 0 END) AS Blocks_hr
FROM LiveLogs
WHERE [Timestamp] >= '2026-03-18 18:42:20'
GROUP BY DATEPART(HOUR,[Timestamp])
ORDER BY hr;
