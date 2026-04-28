function Get-CS {
    $json = Get-Content "$PSScriptRoot\appsettings.json" -Raw | ConvertFrom-Json
    $enc = $json.ConnectionStrings.DefaultConnection
    $k = [byte[]](0x43, 0x6F, 0x69, 0x6E, 0x46, 0x46, 0x2D, 0x54, 0x72, 0x61, 0x64, 0x69, 0x6E, 0x67, 0x42, 0x6F, 0x74, 0x2D, 0x41, 0x45, 0x53, 0x32, 0x35, 0x36, 0x2D, 0x4B, 0x65, 0x79, 0x2D, 0x33, 0x32, 0x42)
    $f = [Convert]::FromBase64String($enc)
    $a = [System.Security.Cryptography.Aes]::Create(); $a.Key = $k
    $iv = New-Object byte[] $a.IV.Length; $c = New-Object byte[] ($f.Length - $a.IV.Length)
    [Buffer]::BlockCopy($f, 0, $iv, 0, $a.IV.Length); [Buffer]::BlockCopy($f, $a.IV.Length, $c, 0, $c.Length)
    $a.IV = $iv; $d = $a.CreateDecryptor($a.Key, $a.IV)
    $s = [Text.Encoding]::UTF8.GetString($d.TransformFinalBlock($c, 0, $c.Length)); $a.Dispose(); $d.Dispose(); return $s
}
function Q($sql) {
    $cn = New-Object System.Data.SqlClient.SqlConnection (Get-CS); $cn.Open()
    $cm = $cn.CreateCommand(); $cm.CommandText = $sql; $cm.CommandTimeout = 30
    $ap = New-Object System.Data.SqlClient.SqlDataAdapter $cm; $ds = New-Object System.Data.DataSet; [void]$ap.Fill($ds); $cn.Close()
    return $ds.Tables[0]
}
[Console]::OutputEncoding = [Text.Encoding]::UTF8

Write-Host "==== A. last 30min - 진입 시도 / 주문 / 슬롯 / 마진 / 차단 키워드 ====" -ForegroundColor Cyan
Q @"
SELECT TOP 30 LEFT(Message, 230) AS Msg, COUNT(*) AS Cnt
FROM FooterLogs WHERE Timestamp >= DATEADD(MINUTE,-30,GETDATE())
  AND (Message LIKE '%주문%' OR Message LIKE '%진입%' OR Message LIKE '%슬롯%' OR Message LIKE '%마진%'
    OR Message LIKE '%ExecuteAutoOrder%' OR Message LIKE '%Order%' OR Message LIKE '%MAX%'
    OR Message LIKE '%잔고%' OR Message LIKE '%부족%' OR Message LIKE '%FAIL%' OR Message LIKE '%실패%'
    OR Message LIKE '%PASS%' OR Message LIKE '%차단%' OR Message LIKE '%금지%' OR Message LIKE '%대기%'
    OR Message LIKE '%스킵%' OR Message LIKE '%bypass%' OR Message LIKE '%hold%')
GROUP BY LEFT(Message, 230)
ORDER BY Cnt DESC
"@ | Format-Table -AutoSize -Wrap

Write-Host ""
Write-Host "==== B. 마지막 GATE-PASS 다음 5줄 ====" -ForegroundColor Cyan
Q @"
WITH LastGate AS (
  SELECT TOP 1 Id FROM FooterLogs WHERE Message LIKE '%[GATE-PASS]%' ORDER BY Id DESC
)
SELECT TOP 10 Timestamp, LEFT(Message,250) AS Msg
FROM FooterLogs WHERE Id >= (SELECT Id FROM LastGate)
ORDER BY Id ASC
"@ | Format-Table -AutoSize -Wrap

Write-Host ""
Write-Host "==== C. PositionState 활성 ====" -ForegroundColor Cyan
Q "SELECT TOP 10 * FROM sys.columns WHERE object_id=OBJECT_ID('PositionState')" | Format-Table -AutoSize
Q "SELECT COUNT(*) AS ActiveCount FROM PositionState" | Format-Table -AutoSize
