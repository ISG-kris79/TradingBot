function Get-CS {
    $json = Get-Content "$PSScriptRoot\appsettings.json" -Raw | ConvertFrom-Json
    $enc = $json.ConnectionStrings.DefaultConnection
    $k = [byte[]](0x43, 0x6F, 0x69, 0x6E, 0x46, 0x46, 0x2D, 0x54, 0x72, 0x61, 0x64, 0x69, 0x6E, 0x67, 0x42, 0x6F, 0x74, 0x2D, 0x41, 0x45, 0x53, 0x32, 0x35, 0x36, 0x2D, 0x4B, 0x65, 0x79, 0x2D, 0x33, 0x32, 0x42)
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
    $cm = $cn.CreateCommand(); $cm.CommandText = $sql; $cm.CommandTimeout = 30
    $r = $cm.ExecuteReader()
    $rows = @()
    while ($r.Read()) {
        $row = [ordered]@{}
        for ($i = 0; $i -lt $r.FieldCount; $i++) {
            $row[$r.GetName($i)] = if ($r.IsDBNull($i)) { $null } else { $r[$i] }
        }
        $rows += [PSCustomObject]$row
    }
    $r.Close(); $cn.Close()
    return $rows
}
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8

foreach ($sym in @('MUSDT', 'TAOUSDT')) {
    Write-Host ""
    Write-Host "==== $sym 최근 7일 매매 ====" -ForegroundColor Cyan
    Q "SELECT TOP 15 Id, Side, ROUND(EntryPrice,4) AS EntryPx, ROUND(ExitPrice,4) AS ExitPx, ROUND(PnL,2) AS PnL, ROUND(PnLPercent,2) AS PnLPct, EntryTime, DATEDIFF(MINUTE,EntryTime,ExitTime) AS HoldMin, LEFT(ExitReason,40) AS ExitReason FROM TradeHistory WITH (NOLOCK) WHERE Symbol='$sym' AND EntryTime>=DATEADD(day,-7,GETDATE()) ORDER BY EntryTime DESC" | Format-Table -AutoSize

    Write-Host "==== $sym 통계 (7일) ====" -ForegroundColor Yellow
    Q "SELECT COUNT(*) AS N, SUM(CASE WHEN PnL>0 THEN 1 ELSE 0 END) AS Wins, CAST(SUM(CASE WHEN PnL>0 THEN 1.0 ELSE 0 END)/NULLIF(COUNT(*),0) AS DECIMAL(5,2)) AS WinRate, ROUND(SUM(PnL),2) AS TotalPnL, ROUND(AVG(PnL),2) AS AvgPnL, ROUND(AVG(PnLPercent),2) AS AvgPnLPct FROM TradeHistory WITH (NOLOCK) WHERE Symbol='$sym' AND EntryTime>=DATEADD(day,-7,GETDATE()) AND IsClosed=1" | Format-Table -AutoSize

    Write-Host "==== $sym 진입 가격 vs 5분봉 고점 비교 (최근 5건) ====" -ForegroundColor Yellow
    Q "SELECT TOP 5 th.Symbol, ROUND(th.EntryPrice,4) AS EntryPx, ROUND(MAX(mc.HighPrice),4) AS Prev5mHigh, ROUND(MIN(mc.LowPrice),4) AS Prev5mLow, CAST((th.EntryPrice - MIN(mc.LowPrice)) / NULLIF(MAX(mc.HighPrice) - MIN(mc.LowPrice), 0) AS DECIMAL(5,2)) AS PosInRange, ROUND(th.PnLPercent,2) AS PnLPct, th.EntryTime FROM TradeHistory th WITH (NOLOCK) INNER JOIN MarketCandles mc WITH (NOLOCK) ON mc.Symbol=th.Symbol AND mc.OpenTime BETWEEN DATEADD(MINUTE,-15,th.EntryTime) AND th.EntryTime WHERE th.Symbol='$sym' AND th.IsClosed=1 AND th.EntryTime>=DATEADD(day,-7,GETDATE()) GROUP BY th.Id, th.Symbol, th.EntryPrice, th.PnLPercent, th.EntryTime HAVING COUNT(mc.OpenTime) >= 1 ORDER BY th.EntryTime DESC" | Format-Table -AutoSize
}

# Check TAO blacklist status
Write-Host ""
Write-Host "==== TAOUSDT 최근 ML 신호 (12h) ====" -ForegroundColor Cyan
$maxId = (Q "SELECT TOP 1 Id FROM FooterLogs WITH (NOLOCK) ORDER BY Id DESC")[0].Id
$lookbackId = $maxId - 100000
Q "SELECT TOP 20 Timestamp, LEFT(Message, 220) AS Msg FROM FooterLogs WITH (NOLOCK) WHERE Id >= $lookbackId AND Message LIKE '%TAOUSDT%' AND (Message LIKE '%SIGNAL%' OR Message LIKE '%PREDICT%' OR Message LIKE '%BLOCK%' OR Message LIKE '%블랙리스트%') ORDER BY Id DESC" | Format-Table -AutoSize -Wrap
