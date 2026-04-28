$ErrorActionPreference = "Stop"
[Console]::OutputEncoding = [Text.Encoding]::UTF8
$AesKey = [byte[]](0x43,0x6F,0x69,0x6E,0x46,0x46,0x2D,0x54,0x72,0x61,0x64,0x69,0x6E,0x67,0x42,0x6F,0x74,0x2D,0x41,0x45,0x53,0x32,0x35,0x36,0x2D,0x4B,0x65,0x79,0x2D,0x33,0x32,0x42)
function AesDecrypt($enc) {
    $f = [Convert]::FromBase64String($enc)
    $a = [System.Security.Cryptography.Aes]::Create(); $a.Key = $AesKey
    $iv = New-Object byte[] $a.IV.Length
    $c = New-Object byte[] ($f.Length - $a.IV.Length)
    [Buffer]::BlockCopy($f, 0, $iv, 0, $a.IV.Length)
    [Buffer]::BlockCopy($f, $a.IV.Length, $c, 0, $c.Length)
    $a.IV = $iv; $d = $a.CreateDecryptor($a.Key, $a.IV)
    $s = [Text.Encoding]::UTF8.GetString($d.TransformFinalBlock($c, 0, $c.Length))
    $a.Dispose(); $d.Dispose(); return $s
}
$json = Get-Content "$PSScriptRoot\appsettings.json" -Raw | ConvertFrom-Json
$cn = New-Object System.Data.SqlClient.SqlConnection (AesDecrypt $json.ConnectionStrings.DefaultConnection)
$cn.Open()
function Run($sql, $title) {
    Write-Host ""; Write-Host "=== $title ==="
    $cmd = $cn.CreateCommand(); $cmd.CommandText = $sql; $cmd.CommandTimeout = 60
    $rdr = $cmd.ExecuteReader()
    $tbl = New-Object System.Data.DataTable; $tbl.Load($rdr)
    $tbl | Format-Table -AutoSize | Out-String -Width 250 | Write-Host
}

# Bot_Log columns: Id, UserId, EventTime, Symbol, Direction, CoinType, Allowed, Reason, ML_Conf, TF_Conf, TrendScore, RSI, BBPosition, DecisionId

# [1] 최근 12시간 진입 시도/차단 (Allowed/Reason)
$sql1 = "SELECT TOP 50 EventTime, Symbol, CoinType, Allowed, Reason FROM dbo.Bot_Log WHERE EventTime >= DATEADD(HOUR, -12, SYSDATETIME()) ORDER BY EventTime DESC"
Run $sql1 "S1: Bot_Log entries last 12h (decisions)"

# [2] 차단 사유 카운트 (12시간)
$sql2 = "SELECT Reason, COUNT(*) AS cnt FROM dbo.Bot_Log WHERE EventTime >= DATEADD(HOUR, -12, SYSDATETIME()) AND Allowed = 0 GROUP BY Reason ORDER BY cnt DESC"
Run $sql2 "S2: Block Reasons last 12h (sorted by count)"

# [3] 12시간 동안 통과한 (Allowed=1) 건수
$sql3 = "SELECT COUNT(*) AS PassedCount FROM dbo.Bot_Log WHERE EventTime >= DATEADD(HOUR, -12, SYSDATETIME()) AND Allowed = 1"
Run $sql3 "S3: Allowed=1 count last 12h"

# [4] 실제 거래된 (TradeHistory) 12시간
$sql4 = "SELECT COUNT(*) AS TradeCount, SUM(CASE WHEN IsClosed=1 THEN 1 ELSE 0 END) AS Closed FROM dbo.TradeHistory WHERE EntryTime >= DATEADD(HOUR, -12, SYSDATETIME())"
Run $sql4 "S4: Actual TradeHistory entries last 12h"

# [5] 봇이 살아있는지 (last log)
$sql5 = "SELECT TOP 5 EventTime, Symbol, CoinType, Allowed, Reason FROM dbo.Bot_Log ORDER BY EventTime DESC"
Run $sql5 "S5: Most recent 5 logs (heartbeat check)"

# [6] CoinType별 시도 빈도 (12h)
$sql6 = "SELECT CoinType, COUNT(*) AS Attempts, SUM(CASE WHEN Allowed=1 THEN 1 ELSE 0 END) AS Passed FROM dbo.Bot_Log WHERE EventTime >= DATEADD(HOUR, -12, SYSDATETIME()) GROUP BY CoinType ORDER BY Attempts DESC"
Run $sql6 "S6: Per CoinType attempts last 12h"

$cn.Close()
