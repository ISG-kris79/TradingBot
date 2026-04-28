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
    $cm = $cn.CreateCommand(); $cm.CommandText = $sql; $cm.CommandTimeout = 90
    $ap = New-Object System.Data.SqlClient.SqlDataAdapter $cm
    $ds = New-Object System.Data.DataSet; [void]$ap.Fill($ds); $cn.Close()
    return $ds.Tables[0]
}
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8

Write-Host "=== [1] UserId=1 최근 2일 메이저 진입 시도 + 종결 ===" -ForegroundColor Cyan
Q @"
SELECT Symbol, Side, Strategy, EntryTime, ExitTime, PnL,
       DATEDIFF(MINUTE, EntryTime, ExitTime) AS HoldMin
FROM TradeHistory
WHERE UserId=1
  AND Symbol IN ('BTCUSDT','ETHUSDT','SOLUSDT','XRPUSDT','BNBUSDT','ADAUSDT','DOGEUSDT','AVAXUSDT','LINKUSDT')
  AND EntryTime >= DATEADD(HOUR, -48, GETDATE())
ORDER BY EntryTime DESC
"@ | Format-Table -AutoSize

Write-Host "=== [2] UserId=1 활성 포지션 확인 ===" -ForegroundColor Cyan
Q @"
SELECT Symbol, Side, EntryPrice, Quantity, EntryTime, IsClosed, ExitReason
FROM TradeHistory
WHERE UserId=1 AND IsClosed = 0
ORDER BY Id DESC
"@ | Format-Table -AutoSize

Write-Host "=== [3] UserId=1 최근 6시간 전체 거래 ===" -ForegroundColor Cyan
Q @"
SELECT Symbol, Side, Strategy, EntryTime, ExitTime, PnL, PnLPercent
FROM TradeHistory
WHERE UserId=1
  AND EntryTime >= DATEADD(HOUR, -6, GETDATE())
ORDER BY EntryTime DESC
"@ | Format-Table -AutoSize

Write-Host "=== [4] UserId=1 최근 PUMP vs MAJOR 비율 (24h) ===" -ForegroundColor Cyan
Q @"
SELECT
  CASE WHEN Symbol IN ('BTCUSDT','ETHUSDT','SOLUSDT','XRPUSDT','BNBUSDT','ADAUSDT','DOGEUSDT','AVAXUSDT','LINKUSDT')
       THEN 'MAJOR' ELSE 'PUMP' END AS Category,
  COUNT(*) AS Cnt,
  SUM(PnL) AS TotalPnL
FROM TradeHistory
WHERE UserId=1 AND EntryTime >= DATEADD(HOUR, -24, GETDATE())
GROUP BY CASE WHEN Symbol IN ('BTCUSDT','ETHUSDT','SOLUSDT','XRPUSDT','BNBUSDT','ADAUSDT','DOGEUSDT','AVAXUSDT','LINKUSDT')
              THEN 'MAJOR' ELSE 'PUMP' END
"@ | Format-Table -AutoSize

Write-Host "=== [5] UserId 분포 (운영 중인 계정) ===" -ForegroundColor Cyan
Q @"
SELECT UserId, COUNT(*) AS Trades, MAX(EntryTime) AS LastEntry
FROM TradeHistory
WHERE EntryTime >= DATEADD(HOUR, -24, GETDATE())
GROUP BY UserId
ORDER BY UserId
"@ | Format-Table -AutoSize
