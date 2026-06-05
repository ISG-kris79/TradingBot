function Get-CS {
    $json = Get-Content "$PSScriptRoot\appsettings.json" -Raw | ConvertFrom-Json
    $enc = $json.ConnectionStrings.DefaultConnection
    $k = [byte[]](0x43,0x6F,0x69,0x6E,0x46,0x46,0x2D,0x54,0x72,0x61,0x64,0x69,0x6E,0x67,0x42,0x6F,0x74,0x2D,0x41,0x45,0x53,0x32,0x35,0x36,0x2D,0x4B,0x65,0x79,0x2D,0x33,0x32,0x42)
    $f = [Convert]::FromBase64String($enc); $a = [System.Security.Cryptography.Aes]::Create(); $a.Key = $k
    $iv = New-Object byte[] $a.IV.Length; $c = New-Object byte[] ($f.Length - $a.IV.Length)
    [Buffer]::BlockCopy($f,0,$iv,0,$a.IV.Length); [Buffer]::BlockCopy($f,$a.IV.Length,$c,0,$c.Length); $a.IV = $iv
    $d = $a.CreateDecryptor($a.Key,$a.IV); $s = [Text.Encoding]::UTF8.GetString($d.TransformFinalBlock($c,0,$c.Length)); $a.Dispose(); $d.Dispose(); return $s
}
function Q($sql) {
    $cn = New-Object System.Data.SqlClient.SqlConnection (Get-CS); $cn.Open()
    $cm = $cn.CreateCommand(); $cm.CommandText = $sql; $cm.CommandTimeout = 90
    $ap = New-Object System.Data.SqlClient.SqlDataAdapter $cm
    $ds = New-Object System.Data.DataSet; [void]$ap.Fill($ds); $cn.Close()
    return $ds.Tables[0]
}
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8

Write-Host "=== 실거래 카테고리별 평균익절/평균손절/평균보유 (30일) ===" -ForegroundColor Cyan
Q "SELECT Category AS Cat, COUNT(*) AS N, ROUND(AVG(CASE WHEN PnL>0 THEN PnL END),2) AS AvgWin, ROUND(AVG(CASE WHEN PnL<=0 THEN PnL END),2) AS AvgLoss, ROUND(MIN(PnL),2) AS WorstLoss, ROUND(AVG(holdingMinutes),0) AS AvgHoldMin FROM TradeHistory WITH (NOLOCK) WHERE UserId=1 AND IsClosed=1 AND PnL<>0 AND EntryTime>=DATEADD(DAY,-30,GETDATE()) GROUP BY Category ORDER BY N DESC" | Format-Table -AutoSize

Write-Host "=== 실거래 ExitReason 분포 (손절이 어떻게 잘리나) ===" -ForegroundColor Yellow
Q "SELECT CASE WHEN ExitReason LIKE 'SL%' THEN 'SL손절' WHEN ExitReason LIKE 'TP%' THEN 'TP익절' WHEN ExitReason LIKE '%ATR%' OR ExitReason LIKE '%Fractal%' OR ExitReason LIKE '%트레일%' OR ExitReason LIKE '%Trail%' THEN 'ATR/트레일' WHEN ExitReason LIKE '%본절%' OR ExitReason LIKE '%BE%' THEN '본절' WHEN ExitReason LIKE '%MACD%' THEN 'MACD' WHEN ExitReason LIKE 'EXTERNAL%' THEN '외부청산' ELSE 'OTHER' END AS ExitKind, COUNT(*) AS N, ROUND(AVG(PnL),2) AS AvgPnL, ROUND(AVG(holdingMinutes),0) AS AvgHold FROM TradeHistory WITH (NOLOCK) WHERE UserId=1 AND IsClosed=1 AND PnL<>0 AND EntryTime>=DATEADD(DAY,-30,GETDATE()) GROUP BY CASE WHEN ExitReason LIKE 'SL%' THEN 'SL손절' WHEN ExitReason LIKE 'TP%' THEN 'TP익절' WHEN ExitReason LIKE '%ATR%' OR ExitReason LIKE '%Fractal%' OR ExitReason LIKE '%트레일%' OR ExitReason LIKE '%Trail%' THEN 'ATR/트레일' WHEN ExitReason LIKE '%본절%' OR ExitReason LIKE '%BE%' THEN '본절' WHEN ExitReason LIKE '%MACD%' THEN 'MACD' WHEN ExitReason LIKE 'EXTERNAL%' THEN '외부청산' ELSE 'OTHER' END ORDER BY N DESC" | Format-Table -AutoSize
