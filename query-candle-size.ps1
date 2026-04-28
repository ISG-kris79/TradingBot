function Get-CS {
    $json = Get-Content "$PSScriptRoot\appsettings.json" -Raw | ConvertFrom-Json
    $enc = $json.ConnectionStrings.DefaultConnection
    $k = [byte[]](0x43, 0x6F, 0x69, 0x6E, 0x46, 0x46, 0x2D, 0x54,
                  0x72, 0x61, 0x64, 0x69, 0x6E, 0x67, 0x42, 0x6F,
                  0x74, 0x2D, 0x41, 0x45, 0x53, 0x32, 0x35, 0x36,
                  0x2D, 0x4B, 0x65, 0x79, 0x2D, 0x33, 0x32, 0x42)
    $f = [Convert]::FromBase64String($enc)
    $a = [System.Security.Cryptography.Aes]::Create(); $a.Key = $k
    $iv = New-Object byte[] $a.IV.Length
    $c = New-Object byte[] ($f.Length - $a.IV.Length)
    [Buffer]::BlockCopy($f, 0, $iv, 0, $a.IV.Length)
    [Buffer]::BlockCopy($f, $a.IV.Length, $c, 0, $c.Length)
    $a.IV = $iv
    $d = $a.CreateDecryptor($a.Key, $a.IV)
    $s = [System.Text.Encoding]::UTF8.GetString($d.TransformFinalBlock($c, 0, $c.Length))
    $a.Dispose(); $d.Dispose()
    return $s
}
function Q($sql) {
    $cn = New-Object System.Data.SqlClient.SqlConnection (Get-CS); $cn.Open()
    $cm = $cn.CreateCommand(); $cm.CommandText = $sql; $cm.CommandTimeout = 120
    $ap = New-Object System.Data.SqlClient.SqlDataAdapter $cm
    $ds = New-Object System.Data.DataSet; [void]$ap.Fill($ds); $cn.Close()
    return $ds.Tables[0]
}
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8

Write-Host "=== [1] CandleData 총 건수 + Interval 분포 ===" -ForegroundColor Cyan
Q @"
SELECT IntervalText, COUNT(*) AS Cnt,
       MIN(OpenTime) AS OldestTime,
       MAX(OpenTime) AS NewestTime,
       COUNT(DISTINCT Symbol) AS SymbolCount
FROM CandleData
GROUP BY IntervalText
ORDER BY Cnt DESC
"@ | Format-Table -AutoSize

Write-Host "=== [2] 심볼별 5분봉 분포 (상위 20) ===" -ForegroundColor Cyan
Q @"
SELECT TOP 20 Symbol,
    COUNT(*) AS Candles5m,
    MIN(OpenTime) AS OldestTime,
    MAX(OpenTime) AS NewestTime,
    DATEDIFF(HOUR, MIN(OpenTime), MAX(OpenTime)) AS SpanHours
FROM CandleData
WHERE IntervalText='5m'
GROUP BY Symbol
ORDER BY Candles5m DESC
"@ | Format-Table -AutoSize

Write-Host "=== [3] 메이저 심볼 5분봉 보유량 ===" -ForegroundColor Cyan
Q @"
SELECT Symbol,
    COUNT(*) AS Candles5m,
    MIN(OpenTime) AS OldestTime,
    MAX(OpenTime) AS NewestTime,
    DATEDIFF(DAY, MIN(OpenTime), MAX(OpenTime)) AS SpanDays
FROM CandleData
WHERE IntervalText='5m'
  AND Symbol IN ('BTCUSDT','ETHUSDT','SOLUSDT','XRPUSDT','BNBUSDT','ADAUSDT','DOGEUSDT','AVAXUSDT','LINKUSDT')
GROUP BY Symbol
ORDER BY Candles5m DESC
"@ | Format-Table -AutoSize

Write-Host "=== [4] 1분봉 Spike용 데이터 ===" -ForegroundColor Cyan
Q @"
SELECT
    COUNT(*) AS Total1m,
    COUNT(DISTINCT Symbol) AS SymbolCount,
    MIN(OpenTime) AS OldestTime,
    MAX(OpenTime) AS NewestTime
FROM CandleData
WHERE IntervalText='1m'
"@ | Format-Table -AutoSize

Write-Host "=== [5] CandleData 테이블 전체 크기 ===" -ForegroundColor Cyan
Q @"
SELECT
    SUM(p.rows) AS TotalRows,
    CAST(SUM(a.used_pages) * 8.0 / 1024 AS decimal(10,1)) AS UsedMB
FROM sys.tables t
INNER JOIN sys.indexes i ON t.object_id = i.object_id
INNER JOIN sys.partitions p ON i.object_id = p.object_id AND i.index_id = p.index_id
INNER JOIN sys.allocation_units a ON p.partition_id = a.container_id
WHERE t.name = 'CandleData' AND i.index_id IN (0, 1)
"@ | Format-Table -AutoSize
