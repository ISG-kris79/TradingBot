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
    $cm = $cn.CreateCommand(); $cm.CommandText = $sql; $cm.CommandTimeout = 60
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

$maxId = (Q "SELECT TOP 1 Id FROM FooterLogs WITH (NOLOCK) ORDER BY Id DESC")[0].Id
$lookbackId = $maxId - 100000
Write-Host "FooterLogs Max=$maxId, scan last 100k rows" -ForegroundColor DarkGray

Write-Host "`n==== [1] RAVEUSDT 매매 이력 (30일) ====" -ForegroundColor Cyan
Q "SELECT TOP 15 Id, Side, ROUND(EntryPrice,4) AS EntryPx, ROUND(ExitPrice,4) AS ExitPx, ROUND(PnL,2) AS PnL, ROUND(PnLPercent,2) AS PnLPct, EntryTime, ExitTime, LEFT(ExitReason,40) AS ExitReason FROM TradeHistory WITH (NOLOCK) WHERE Symbol='RAVEUSDT' AND EntryTime>=DATEADD(day,-30,GETDATE()) ORDER BY EntryTime DESC" | Format-Table -AutoSize

Write-Host "`n==== [2] RAVEUSDT 최근 12h 5분봉 가격/거래량 (펌프 시점 찾기) ====" -ForegroundColor Cyan
Q "SELECT TOP 30 OpenTime, ROUND(OpenPrice,4) AS [Open], ROUND(HighPrice,4) AS [High], ROUND(LowPrice,4) AS [Low], ROUND(ClosePrice,4) AS [Close], ROUND(Volume,0) AS Volume, ROUND((ClosePrice-OpenPrice)/NULLIF(OpenPrice,0)*100, 2) AS ChgPct FROM MarketCandles WITH (NOLOCK) WHERE Symbol='RAVEUSDT' AND OpenTime >= DATEADD(hour,-12,GETUTCDATE()) ORDER BY OpenTime DESC" | Format-Table -AutoSize

Write-Host "`n==== [3] RAVEUSDT 봇 신호/진입 차단 로그 (최근 12h) ====" -ForegroundColor Cyan
Q "SELECT TOP 40 Timestamp, LEFT(Message, 230) AS Msg FROM FooterLogs WITH (NOLOCK) WHERE Id >= $lookbackId AND Message LIKE '%RAVEUSDT%' ORDER BY Id DESC" | Format-Table -AutoSize -Wrap

Write-Host "`n==== [4] PUMP 우선순위 큐 현황 (SLOT 포화 + 큐 등록 로그 최근 6h) ====" -ForegroundColor Cyan
Q "SELECT TOP 30 Timestamp, LEFT(Message, 250) AS Msg FROM FooterLogs WITH (NOLOCK) WHERE Id >= $lookbackId AND (Message LIKE '%PUMP 포화%우선순위 큐%' OR Message LIKE '%queued score%') ORDER BY Id DESC" | Format-Table -AutoSize -Wrap

Write-Host "`n==== [5] RAVEUSDT 현재 포지션 상태 ====" -ForegroundColor Cyan
Q "SELECT Id, Symbol, Side, ROUND(EntryPrice,6) AS EntryPx, Quantity, ROUND(AiScore,2) AS AiScore, EntryTime, LastUpdatedAt FROM TradeHistory WITH (NOLOCK) WHERE Symbol='RAVEUSDT' AND IsClosed=0" | Format-Table -AutoSize
