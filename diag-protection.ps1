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

Write-Host "==== [1] 현재 활성 포지션 ====" -ForegroundColor Cyan
$activePositions = Q "SELECT Id, Symbol, Side, ROUND(EntryPrice,6) AS EntryPx, Quantity, EntryTime, LastUpdatedAt FROM TradeHistory WITH (NOLOCK) WHERE IsClosed=0 ORDER BY EntryTime DESC"
$activePositions | Format-Table -AutoSize

Write-Host "==== [2] 최근 1시간 보호점검 로그 (각 심볼별 마지막 1건) ====" -ForegroundColor Cyan
foreach ($pos in $activePositions) {
    $sym = $pos.Symbol
    $logs = Q "SELECT TOP 3 Timestamp, LEFT(Message, 250) AS Msg FROM FooterLogs WITH (NOLOCK) WHERE Timestamp >= DATEADD(hour,-1,GETDATE()) AND Message LIKE '%[보호점검]%' AND Message LIKE '%$sym%' ORDER BY Id DESC"
    if ($logs.Count -gt 0) {
        Write-Host ""
        Write-Host "  $sym (Id=$($pos.Id))" -ForegroundColor Yellow
        $logs | ForEach-Object { Write-Host "    [$($_.Timestamp.ToString('HH:mm:ss'))] $($_.Msg)" }
    } else {
        Write-Host "  $sym - 최근 1h 보호점검 로그 없음" -ForegroundColor DarkGray
    }
}

Write-Host ""
Write-Host "==== [3] 보호점검 발생 빈도 (24h 전체) ====" -ForegroundColor Cyan
Q @"
SELECT
    CASE
        WHEN Message LIKE '%SL=True TP=True TR=True%' THEN 'ALL_TRUE (정상)'
        WHEN Message LIKE '%SL=False%' OR Message LIKE '%TP=False%' OR Message LIKE '%TR=False%' THEN 'PARTIAL (일부 등록)'
        WHEN Message LIKE '%재등록%' THEN 'RE_REGISTER'
        ELSE 'OTHER'
    END AS PatternType,
    COUNT(*) AS N
FROM FooterLogs WITH (NOLOCK)
WHERE Timestamp >= DATEADD(hour,-24,GETDATE())
  AND Message LIKE '%[보호점검]%'
GROUP BY
    CASE
        WHEN Message LIKE '%SL=True TP=True TR=True%' THEN 'ALL_TRUE (정상)'
        WHEN Message LIKE '%SL=False%' OR Message LIKE '%TP=False%' OR Message LIKE '%TR=False%' THEN 'PARTIAL (일부 등록)'
        WHEN Message LIKE '%재등록%' THEN 'RE_REGISTER'
        ELSE 'OTHER'
    END
ORDER BY N DESC
"@ | Format-Table -AutoSize

Write-Host "==== [4] 币安人生USDT (사용자 언급 심볼) 보호점검 이력 (최근 6h) ====" -ForegroundColor Cyan
Q "SELECT TOP 15 Timestamp, LEFT(Message, 250) AS Msg FROM FooterLogs WITH (NOLOCK) WHERE Timestamp >= DATEADD(hour,-6,GETDATE()) AND Message LIKE N'%币安人生%' AND (Message LIKE '%[보호점검]%' OR Message LIKE '%SL%' OR Message LIKE '%TP%' OR Message LIKE '%Trailing%') ORDER BY Id DESC" | Format-Table -AutoSize -Wrap

Write-Host ""
Write-Host "==== [5] 활성 포지션별 PositionState (BreakevenPrice/TakeProfitStep/HighestROE) ====" -ForegroundColor Cyan
Q "SELECT ps.Symbol, ps.TakeProfitStep, ps.PartialProfitStage, ROUND(ps.BreakevenPrice,6) AS BEPx, ROUND(ps.HighestROE,2) AS HighROE, ps.StairStep, ps.IsBreakEvenTriggered, ROUND(ps.HighestPrice,6) AS HighPx, ROUND(ps.LowestPrice,6) AS LowPx, ps.IsPumpStrategy, ps.LastUpdatedAt FROM PositionState ps WITH (NOLOCK) INNER JOIN TradeHistory th WITH (NOLOCK) ON ps.Symbol=th.Symbol AND ps.UserId=th.UserId WHERE th.IsClosed=0 ORDER BY ps.LastUpdatedAt DESC" | Format-Table -AutoSize

Write-Host ""
Write-Host "==== [6] 최근 1h 알고 주문 등록/취소 이벤트 ====" -ForegroundColor Cyan
Q "SELECT TOP 20 Timestamp, LEFT(Message, 220) AS Msg FROM FooterLogs WITH (NOLOCK) WHERE Timestamp >= DATEADD(hour,-1,GETDATE()) AND (Message LIKE '%algoOrder%' OR Message LIKE '%STOP_MARKET%' OR Message LIKE '%TAKE_PROFIT_MARKET%' OR Message LIKE '%TRAILING_STOP%' OR Message LIKE '%PlaceStopOrder%' OR Message LIKE '%PlaceTakeProfit%') ORDER BY Id DESC" | Format-Table -AutoSize -Wrap
