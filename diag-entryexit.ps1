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
    $cm = $cn.CreateCommand(); $cm.CommandText = $sql; $cm.CommandTimeout = 120
    $ap = New-Object System.Data.SqlClient.SqlDataAdapter $cm
    $ds = New-Object System.Data.DataSet; [void]$ap.Fill($ds); $cn.Close()
    return $ds.Tables[0]
}
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8
# 가격기반 move% = (Exit/Entry-1)*100 (LONG). 손상행 제거: ExitPrice>0 AND |move|<80%
$valid = "FROM TradeHistory WITH (NOLOCK) WHERE UserId=10 AND IsClosed=1 AND Category='LORENTZIAN' AND EntryPrice>0 AND ExitPrice>0 AND ABS(ExitPrice/EntryPrice-1)<0.8 AND EntryTime>=DATEADD(DAY,-30,GETDATE())"

Write-Host "=== 전체 요약 (가격기반, LORENTZIAN 30d) ===" -ForegroundColor Cyan
Q "SELECT COUNT(*) AS N, SUM(CASE WHEN ExitPrice<EntryPrice THEN 1 ELSE 0 END) AS LossN, CAST(100.0*SUM(CASE WHEN ExitPrice<EntryPrice THEN 1 ELSE 0 END)/COUNT(*) AS DECIMAL(5,1)) AS LossRate, CAST(AVG(CASE WHEN ExitPrice>EntryPrice THEN (ExitPrice/EntryPrice-1)*100 END) AS DECIMAL(6,3)) AS AvgWinMovePct, CAST(AVG(CASE WHEN ExitPrice<EntryPrice THEN (ExitPrice/EntryPrice-1)*100 END) AS DECIMAL(6,3)) AS AvgLossMovePct, CAST(MAX((ExitPrice/EntryPrice-1)*100) AS DECIMAL(6,2)) AS BestWinMovePct $valid" | Format-Table -AutoSize

Write-Host "=== 승리 거래 수익크기 분포 (가격 move%) — 잔익만 먹나? ===" -ForegroundColor Yellow
Q "SELECT CASE WHEN m<0.5 THEN '1_0-0.5%' WHEN m<1 THEN '2_0.5-1%' WHEN m<2 THEN '3_1-2%' WHEN m<4 THEN '4_2-4%' ELSE '5_4%+' END AS WinMoveBkt, COUNT(*) AS N FROM (SELECT (ExitPrice/EntryPrice-1)*100 AS m $valid AND ExitPrice>EntryPrice) t GROUP BY CASE WHEN m<0.5 THEN '1_0-0.5%' WHEN m<1 THEN '2_0.5-1%' WHEN m<2 THEN '3_1-2%' WHEN m<4 THEN '4_2-4%' ELSE '5_4%+' END ORDER BY WinMoveBkt" | Format-Table -AutoSize

Write-Host "=== 손실 거래 손실크기 분포 (가격 move%) ===" -ForegroundColor Yellow
Q "SELECT CASE WHEN m>-0.5 THEN '1_0~-0.5%' WHEN m>-1 THEN '2_-0.5~-1%' WHEN m>-2 THEN '3_-1~-2%' WHEN m>-4 THEN '4_-2~-4%' ELSE '5_-4%+' END AS LossMoveBkt, COUNT(*) AS N FROM (SELECT (ExitPrice/EntryPrice-1)*100 AS m $valid AND ExitPrice<EntryPrice) t GROUP BY CASE WHEN m>-0.5 THEN '1_0~-0.5%' WHEN m>-1 THEN '2_-0.5~-1%' WHEN m>-2 THEN '3_-1~-2%' WHEN m>-4 THEN '4_-2~-4%' ELSE '5_-4%+' END ORDER BY LossMoveBkt" | Format-Table -AutoSize

Write-Host "=== 승리 거래 청산사유 (큰 수익은 어떻게 나가나) ===" -ForegroundColor Yellow
Q "SELECT CASE WHEN ExitReason LIKE 'TP%' THEN 'TP익절' WHEN ExitReason LIKE 'SL%' THEN 'SL' WHEN ExitReason LIKE '%Trail%' OR ExitReason LIKE '%트레일%' THEN '트레일' WHEN ExitReason LIKE '%MACD%' THEN 'MACD' WHEN ExitReason LIKE '%본절%' OR ExitReason LIKE '%BE%' THEN '본절' WHEN ExitReason LIKE 'EXTERNAL%' THEN '외부' ELSE 'OTHER' END AS Reason, COUNT(*) AS N, CAST(AVG((ExitPrice/EntryPrice-1)*100) AS DECIMAL(6,3)) AS AvgMovePct, AVG(holdingMinutes) AS AvgHold $valid AND ExitPrice>EntryPrice GROUP BY CASE WHEN ExitReason LIKE 'TP%' THEN 'TP익절' WHEN ExitReason LIKE 'SL%' THEN 'SL' WHEN ExitReason LIKE '%Trail%' OR ExitReason LIKE '%트레일%' THEN '트레일' WHEN ExitReason LIKE '%MACD%' THEN 'MACD' WHEN ExitReason LIKE '%본절%' OR ExitReason LIKE '%BE%' THEN '본절' WHEN ExitReason LIKE 'EXTERNAL%' THEN '외부' ELSE 'OTHER' END ORDER BY N DESC" | Format-Table -AutoSize
