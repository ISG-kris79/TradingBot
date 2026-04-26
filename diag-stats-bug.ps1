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
    $cmd = $cn.CreateCommand(); $cmd.CommandText = $sql; $cmd.CommandTimeout = 30
    $rdr = $cmd.ExecuteReader()
    $tbl = New-Object System.Data.DataTable; $tbl.Load($rdr)
    $tbl | Format-Table -AutoSize | Out-String -Width 200 | Write-Host
}

# 1. TradeHistory 컬럼 확인
Run "SELECT TOP 1 * FROM dbo.TradeHistory ORDER BY Id DESC" "[1] TradeHistory 최신 row 컬럼"

# 2. 오늘(KST) 청산 포지션 - ExitTime 기준
Run @"
SELECT TOP 30
  Id, Symbol, Category, EntryTime, ExitTime, IsClosed, PnL, ExitReason, UserId
FROM dbo.TradeHistory
WHERE ExitTime IS NOT NULL
  AND ExitTime >= DATEADD(HOUR, -24, SYSDATETIME())
ORDER BY ExitTime DESC
"@ "[2] 최근 24시간 청산 (ExitTime DESC)"

# 3. Category NULL 비율
Run @"
SELECT
  COUNT(*) AS Total,
  SUM(CASE WHEN Category IS NULL THEN 1 ELSE 0 END) AS Null_Category,
  SUM(CASE WHEN Category IS NOT NULL THEN 1 ELSE 0 END) AS HasCategory,
  SUM(CASE WHEN IsClosed=1 AND PnL < 0 THEN 1 ELSE 0 END) AS LossClosed,
  SUM(CASE WHEN IsClosed=1 AND PnL > 0 THEN 1 ELSE 0 END) AS WinClosed
FROM dbo.TradeHistory
WHERE ExitTime >= DATEADD(HOUR, -24, SYSDATETIME())
"@ "[3] 24시간 청산 통계 (Category NULL 여부)"

# 4. 현재 SP 결과 (메이저/펌프/스파이크 표시되는 그것)
Run @"
EXEC dbo.sp_GetTodayStatsByCategory
  @todayStart = '$([DateTime]::Today.ToString("yyyy-MM-dd HH:mm:ss"))',
  @userId = 0
"@ "[4] sp_GetTodayStatsByCategory 결과 (todayStart=KST 자정, userId=0=ALL)"

# 5. EntryTime vs ExitTime 분포 (어제 진입 / 오늘 청산)
Run @"
SELECT TOP 20
  Symbol, Category, EntryTime, ExitTime, PnL, IsClosed,
  DATEDIFF(MINUTE, EntryTime, ExitTime) AS HoldMinutes,
  CASE WHEN EntryTime < CAST(GETDATE() AS DATE) AND ExitTime >= CAST(GETDATE() AS DATE)
       THEN 'YES_yesterday_entry_today_exit' ELSE 'no' END AS CrossDay
FROM dbo.TradeHistory
WHERE ExitTime IS NOT NULL
  AND ExitTime >= DATEADD(HOUR, -24, SYSDATETIME())
ORDER BY ExitTime DESC
"@ "[5] 청산 포지션의 EntryTime vs ExitTime crossover 체크"

# 6. PnL 합계 (24h)
Run @"
SELECT
  SUM(CASE WHEN IsClosed=1 THEN PnL ELSE 0 END) AS TotalPnL_24h,
  COUNT(CASE WHEN IsClosed=1 THEN 1 END) AS Closed_24h
FROM dbo.TradeHistory
WHERE ExitTime >= DATEADD(HOUR, -24, SYSDATETIME())
"@ "[6] 24시간 실제 PnL 합계"

$cn.Close()
