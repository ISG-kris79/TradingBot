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

Write-Host "==== [1] AAVEUSDT 활성 포지션 (TradeHistory IsClosed=0) ====" -ForegroundColor Cyan
Q "SELECT TOP 5 Id, UserId, Symbol, Side, EntryPrice, Quantity, AiScore, EntryTime, LastUpdatedAt FROM TradeHistory WITH (NOLOCK) WHERE Symbol='AAVEUSDT' AND IsClosed=0 ORDER BY EntryTime DESC" | Format-Table -AutoSize

Write-Host "==== [2] AAVEUSDT 최근 12h 청산 기록 ====" -ForegroundColor Cyan
Q "SELECT TOP 20 Id, Side, EntryPrice, ExitPrice, Quantity, ROUND(PnL,2) AS PnL, ROUND(PnLPercent,2) AS PnLPct, EntryTime, ExitTime, LEFT(ExitReason,60) AS ExitReason FROM TradeHistory WITH (NOLOCK) WHERE Symbol='AAVEUSDT' AND ExitTime>=DATEADD(hour,-12,GETDATE()) ORDER BY ExitTime DESC" | Format-Table -AutoSize

Write-Host "==== [3] AAVEUSDT 최근 신호/ROI 로그 (FooterLogs) ====" -ForegroundColor Cyan
Q "SELECT TOP 20 Timestamp, LEFT(Message, 220) AS Msg FROM FooterLogs WITH (NOLOCK) WHERE Timestamp>=DATEADD(hour,-12,GETDATE()) AND Message LIKE '%AAVE%' AND (Message LIKE '%ROI%' OR Message LIKE '%ROE%' OR Message LIKE '%PnL%' OR Message LIKE '%수익%' OR Message LIKE '%pos %' OR Message LIKE '%signal%') ORDER BY Id DESC" | Format-Table -AutoSize -Wrap

Write-Host "==== [4] AAVEUSDT 가장 최근 PositionState ====" -ForegroundColor Cyan
Q "SELECT TOP 3 UserId, Symbol, TakeProfitStep, PartialProfitStage, BreakevenPrice, HighestROE, StairStep, IsBreakEvenTriggered, HighestPrice, LowestPrice, LastUpdatedAt FROM PositionState WITH (NOLOCK) WHERE Symbol='AAVEUSDT' ORDER BY LastUpdatedAt DESC" | Format-Table -AutoSize

Write-Host "==== [5] 12h 매매 기록 일별 요약 ====" -ForegroundColor Cyan
Q "SELECT DATEPART(hour, EntryTime) AS Hour, COUNT(*) AS N, SUM(CASE WHEN PnL>0 THEN 1 ELSE 0 END) AS Wins, ROUND(SUM(PnL),2) AS TotalPnL FROM TradeHistory WITH (NOLOCK) WHERE EntryTime>=DATEADD(hour,-12,GETDATE()) AND IsClosed=1 GROUP BY DATEPART(hour, EntryTime) ORDER BY Hour DESC" | Format-Table -AutoSize

Write-Host "==== [6] 12h 카테고리별 매매 ====" -ForegroundColor Cyan
Q "SELECT ISNULL(Category,'(none)') AS Category, COUNT(*) AS N, SUM(CASE WHEN PnL>0 THEN 1 ELSE 0 END) AS Wins, ROUND(SUM(PnL),2) AS TotalPnL, ROUND(AVG(PnL),2) AS AvgPnL FROM TradeHistory WITH (NOLOCK) WHERE EntryTime>=DATEADD(hour,-12,GETDATE()) AND IsClosed=1 GROUP BY Category ORDER BY TotalPnL DESC" | Format-Table -AutoSize

Write-Host "==== [7] 12h 손실 TOP 5 ====" -ForegroundColor Cyan
Q "SELECT TOP 5 Symbol, Side, ROUND(PnL,2) AS PnL, ROUND(PnLPercent,2) AS PnLPct, EntryTime, ExitTime, LEFT(ExitReason,60) AS ExitReason FROM TradeHistory WITH (NOLOCK) WHERE ExitTime>=DATEADD(hour,-12,GETDATE()) AND IsClosed=1 ORDER BY PnL ASC" | Format-Table -AutoSize

Write-Host "==== [8] 12h 수익 TOP 5 ====" -ForegroundColor Cyan
Q "SELECT TOP 5 Symbol, Side, ROUND(PnL,2) AS PnL, ROUND(PnLPercent,2) AS PnLPct, EntryTime, ExitTime, LEFT(ExitReason,60) AS ExitReason FROM TradeHistory WITH (NOLOCK) WHERE ExitTime>=DATEADD(hour,-12,GETDATE()) AND IsClosed=1 ORDER BY PnL DESC" | Format-Table -AutoSize
