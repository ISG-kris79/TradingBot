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

# Use Id-based filter (much faster than Timestamp range)
$maxId = (Q "SELECT TOP 1 Id FROM FooterLogs WITH (NOLOCK) ORDER BY Id DESC")[0].Id
$lookbackId = $maxId - 30000  # last ~30k rows
Write-Host "FooterLogs MaxId=$maxId, scanning last ~30k rows (Id >= $lookbackId)" -ForegroundColor DarkGray

Write-Host "`n==== [A] 최근 [보호점검] 메시지 (전체 패턴) ====" -ForegroundColor Cyan
Q "SELECT TOP 30 Timestamp, LEFT(Message, 250) AS Msg FROM FooterLogs WITH (NOLOCK) WHERE Id >= $lookbackId AND Message LIKE '%[보호점검]%' ORDER BY Id DESC" | Format-Table -AutoSize -Wrap

Write-Host "`n==== [B] 최근 algoOrder / STOP / TP / TRAILING 등록 이벤트 ====" -ForegroundColor Cyan
Q "SELECT TOP 25 Timestamp, LEFT(Message, 250) AS Msg FROM FooterLogs WITH (NOLOCK) WHERE Id >= $lookbackId AND (Message LIKE '%STOP_MARKET%' OR Message LIKE '%TAKE_PROFIT_MARKET%' OR Message LIKE '%TRAILING_STOP%' OR Message LIKE '%algoOrder%') ORDER BY Id DESC" | Format-Table -AutoSize -Wrap

Write-Host "`n==== [C] 활성 포지션별 PositionState ====" -ForegroundColor Cyan
Q "SELECT ps.Symbol, ps.TakeProfitStep AS TpStep, ps.PartialProfitStage AS PartStage, ROUND(ps.BreakevenPrice,6) AS BEPx, ROUND(ps.HighestROE,2) AS HighROE, ps.StairStep, ps.IsBreakEvenTriggered AS BeTrig, ROUND(ps.HighestPrice,6) AS HighPx, ROUND(ps.LowestPrice,6) AS LowPx, ps.IsPumpStrategy AS IsPump, ps.LastUpdatedAt FROM PositionState ps WITH (NOLOCK) INNER JOIN TradeHistory th WITH (NOLOCK) ON ps.Symbol=th.Symbol AND ps.UserId=th.UserId WHERE th.IsClosed=0 ORDER BY ps.LastUpdatedAt DESC" | Format-Table -AutoSize

Write-Host "`n==== [D] 보호점검 결과 패턴 분석 ====" -ForegroundColor Cyan
Q @"
SELECT
    CASE
        WHEN Message LIKE '%(SL=True TP=True TR=True)%' THEN '✅ ALL_OK'
        WHEN Message LIKE '%(SL=True TP=True TR=False)%' THEN '🟡 NO_TRAILING'
        WHEN Message LIKE '%(SL=True TP=False TR=True)%' THEN '🟡 NO_TP'
        WHEN Message LIKE '%(SL=False TP=True TR=True)%' THEN '🔴 NO_SL'
        WHEN Message LIKE '%(SL=False TP=False TR=False)%' THEN '🔴 NONE'
        WHEN Message LIKE '%[보호점검]%' THEN 'OTHER_PATTERN'
        ELSE 'NOT_PROTECTION'
    END AS Pattern,
    COUNT(*) AS N
FROM FooterLogs WITH (NOLOCK)
WHERE Id >= $lookbackId
  AND Message LIKE '%[보호점검]%'
GROUP BY
    CASE
        WHEN Message LIKE '%(SL=True TP=True TR=True)%' THEN '✅ ALL_OK'
        WHEN Message LIKE '%(SL=True TP=True TR=False)%' THEN '🟡 NO_TRAILING'
        WHEN Message LIKE '%(SL=True TP=False TR=True)%' THEN '🟡 NO_TP'
        WHEN Message LIKE '%(SL=False TP=True TR=True)%' THEN '🔴 NO_SL'
        WHEN Message LIKE '%(SL=False TP=False TR=False)%' THEN '🔴 NONE'
        WHEN Message LIKE '%[보호점검]%' THEN 'OTHER_PATTERN'
        ELSE 'NOT_PROTECTION'
    END
ORDER BY N DESC
"@ | Format-Table -AutoSize
