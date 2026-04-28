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

Write-Host "==== EDUUSDT 오늘 매매 이력 ====" -ForegroundColor Cyan
Q "SELECT TOP 20 Id, Side, ROUND(EntryPrice,6) AS EntryPx, ROUND(ExitPrice,6) AS ExitPx, Quantity, ROUND(PnL,2) AS PnL, ROUND(PnLPercent,2) AS PnLPct, EntryTime, ExitTime, DATEDIFF(MINUTE,EntryTime,ExitTime) AS HoldMin, LEFT(ExitReason,50) AS ExitReason FROM TradeHistory WITH (NOLOCK) WHERE Symbol='EDUUSDT' AND (EntryTime>=DATEADD(hour,-24,GETDATE()) OR ExitTime>=DATEADD(hour,-24,GETDATE())) ORDER BY EntryTime DESC" | Format-Table -AutoSize

Write-Host "==== EDUUSDT PositionState ====" -ForegroundColor Cyan
Q "SELECT UserId, Symbol, TakeProfitStep, PartialProfitStage, ROUND(BreakevenPrice,6) AS BEPx, ROUND(HighestROE,2) AS HighROE, StairStep, IsBreakEvenTriggered AS BeTrig, ROUND(HighestPrice,6) AS HighPx, ROUND(LowestPrice,6) AS LowPx, IsPumpStrategy, LastUpdatedAt FROM PositionState WITH (NOLOCK) WHERE Symbol='EDUUSDT'" | Format-Table -AutoSize

Write-Host "==== EDUUSDT FooterLogs 수동 청산/익절 관련 (최근 6h) ====" -ForegroundColor Cyan
$maxId = (Q "SELECT TOP 1 Id FROM FooterLogs WITH (NOLOCK) ORDER BY Id DESC")[0].Id
$lookbackId = $maxId - 50000
Q "SELECT TOP 30 Timestamp, LEFT(Message, 300) AS Msg FROM FooterLogs WITH (NOLOCK) WHERE Id >= $lookbackId AND Message LIKE '%EDUUSDT%' AND (Message LIKE '%익절%' OR Message LIKE '%청산%' OR Message LIKE '%Exit%' OR Message LIKE '%수동%' OR Message LIKE '%ROE%' OR Message LIKE '%TRAILING%' OR Message LIKE '%StairStep%' OR Message LIKE '%본절%') ORDER BY Id DESC" | Format-Table -AutoSize -Wrap
