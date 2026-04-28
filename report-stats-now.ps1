$ErrorActionPreference = "Stop"
[Console]::OutputEncoding = [Text.Encoding]::UTF8

$AesKey = [byte[]](0x43, 0x6F, 0x69, 0x6E, 0x46, 0x46, 0x2D, 0x54, 0x72, 0x61, 0x64, 0x69, 0x6E, 0x67, 0x42, 0x6F, 0x74, 0x2D, 0x41, 0x45, 0x53, 0x32, 0x35, 0x36, 0x2D, 0x4B, 0x65, 0x79, 0x2D, 0x33, 0x32, 0x42)
function AesDecrypt($enc) {
    $f = [Convert]::FromBase64String($enc)
    $a = [System.Security.Cryptography.Aes]::Create(); $a.Key = $AesKey
    $iv = New-Object byte[] $a.IV.Length
    $c = New-Object byte[] ($f.Length - $a.IV.Length)
    [Buffer]::BlockCopy($f, 0, $iv, 0, $a.IV.Length)
    [Buffer]::BlockCopy($f, $a.IV.Length, $c, 0, $c.Length)
    $a.IV = $iv
    $d = $a.CreateDecryptor($a.Key, $a.IV)
    $s = [Text.Encoding]::UTF8.GetString($d.TransformFinalBlock($c, 0, $c.Length))
    $a.Dispose(); $d.Dispose()
    return $s
}
$json = Get-Content "$PSScriptRoot\appsettings.json" -Raw | ConvertFrom-Json
$cn = New-Object System.Data.SqlClient.SqlConnection (AesDecrypt $json.ConnectionStrings.DefaultConnection)
$cn.Open()

function Run($sql, $title) {
    Write-Host ""
    Write-Host "=== $title ==="
    $cmd = $cn.CreateCommand(); $cmd.CommandText = $sql; $cmd.CommandTimeout = 60
    $rdr = $cmd.ExecuteReader()
    $tbl = New-Object System.Data.DataTable; $tbl.Load($rdr)
    $tbl | Format-Table -AutoSize | Out-String -Width 200 | Write-Host
}

$todayStr = [DateTime]::Today.ToString("yyyy-MM-dd HH:mm:ss")
$sql1 = "EXEC dbo.sp_GetTodayStatsByCategory @todayStart = '$todayStr', @userId = 0"
Run $sql1 "[1] TODAY by Category - sp_GetTodayStatsByCategory v5.20.7"

$sql2 = @'
WITH Closed AS (
  SELECT Category, Symbol, EntryTime, SUM(ISNULL(PnL,0)) AS Pnl
  FROM dbo.TradeHistory
  WHERE Category IS NOT NULL AND IsClosed=1
    AND ExitTime >= DATEADD(DAY, -7, SYSDATETIME())
  GROUP BY Category, Symbol, EntryTime
)
SELECT
  Category,
  COUNT(*) AS Trades,
  SUM(CASE WHEN Pnl > 0 THEN 1 ELSE 0 END) AS Wins,
  SUM(CASE WHEN Pnl < 0 THEN 1 ELSE 0 END) AS Losses,
  SUM(CASE WHEN Pnl = 0 THEN 1 ELSE 0 END) AS Zero,
  CAST(100.0 * SUM(CASE WHEN Pnl > 0 THEN 1 ELSE 0 END) /
       NULLIF(SUM(CASE WHEN Pnl <> 0 THEN 1 ELSE 0 END), 0) AS DECIMAL(5,2)) AS WinRate,
  CAST(SUM(Pnl) AS DECIMAL(12,2)) AS TotalPnL,
  CAST(AVG(Pnl) AS DECIMAL(12,2)) AS AvgPnL
FROM Closed
GROUP BY Category
ORDER BY Category
'@
Run $sql2 "[2] LAST 7 DAYS by Category"

$sql3 = @'
WITH Closed AS (
  SELECT Symbol, EntryTime, SUM(ISNULL(PnL,0)) AS Pnl
  FROM dbo.TradeHistory
  WHERE IsClosed=1 AND ExitTime >= DATEADD(DAY, -7, SYSDATETIME())
  GROUP BY Symbol, EntryTime
)
SELECT
  COUNT(*) AS Trades,
  SUM(CASE WHEN Pnl > 0 THEN 1 ELSE 0 END) AS Wins,
  SUM(CASE WHEN Pnl < 0 THEN 1 ELSE 0 END) AS Losses,
  SUM(CASE WHEN Pnl = 0 THEN 1 ELSE 0 END) AS Zero,
  CAST(100.0 * SUM(CASE WHEN Pnl > 0 THEN 1 ELSE 0 END) /
       NULLIF(SUM(CASE WHEN Pnl <> 0 THEN 1 ELSE 0 END), 0) AS DECIMAL(5,2)) AS WinRate,
  CAST(SUM(Pnl) AS DECIMAL(12,2)) AS TotalPnL,
  CAST(AVG(Pnl) AS DECIMAL(12,2)) AS AvgPnL
FROM Closed
'@
Run $sql3 "[3] LAST 7 DAYS TOTAL"

$sql4 = @'
SELECT
  CAST(ExitTime AS DATE) AS [Date],
  COUNT(DISTINCT CONCAT(Symbol,'|',EntryTime)) AS Trades,
  CAST(SUM(ISNULL(PnL,0)) AS DECIMAL(12,2)) AS DailyPnL,
  SUM(CASE WHEN PnL > 0 THEN 1 ELSE 0 END) AS Wins,
  SUM(CASE WHEN PnL < 0 THEN 1 ELSE 0 END) AS Losses
FROM dbo.TradeHistory
WHERE IsClosed=1 AND ExitTime >= DATEADD(DAY, -14, SYSDATETIME())
GROUP BY CAST(ExitTime AS DATE)
ORDER BY [Date] DESC
'@
Run $sql4 "[4] DAILY PnL last 14 days"

$sql5 = @'
WITH SymPnL AS (
  SELECT Symbol, SUM(ISNULL(PnL,0)) AS TotalPnL, COUNT(*) AS Cnt
  FROM dbo.TradeHistory
  WHERE IsClosed=1 AND ExitTime >= DATEADD(DAY, -7, SYSDATETIME())
  GROUP BY Symbol
)
SELECT TOP 15 Symbol, Cnt, CAST(TotalPnL AS DECIMAL(12,2)) AS PnL_7d
FROM SymPnL
ORDER BY TotalPnL ASC
'@
Run $sql5 "[5] WORST 15 symbols 7d"

$sql6 = @'
WITH SymPnL AS (
  SELECT Symbol, SUM(ISNULL(PnL,0)) AS TotalPnL, COUNT(*) AS Cnt
  FROM dbo.TradeHistory
  WHERE IsClosed=1 AND ExitTime >= DATEADD(DAY, -7, SYSDATETIME())
  GROUP BY Symbol
)
SELECT TOP 10 Symbol, Cnt, CAST(TotalPnL AS DECIMAL(12,2)) AS PnL_7d
FROM SymPnL
ORDER BY TotalPnL DESC
'@
Run $sql6 "[6] BEST 10 symbols 7d"

$cn.Close()
