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
$base = "FROM TradeHistory WITH (NOLOCK) WHERE UserId=10 AND IsClosed=1 AND PnLPercent<>0 AND Category='LORENTZIAN'"

Write-Host "=== A. AiScore 버킷별 WR (점수 높을수록 승률↑?) ===" -ForegroundColor Cyan
Q "SELECT CASE WHEN AiScore<60 THEN '1_<60' WHEN AiScore<70 THEN '2_60-70' WHEN AiScore<80 THEN '3_70-80' WHEN AiScore<90 THEN '4_80-90' ELSE '5_90+' END AS ScoreBkt, COUNT(*) AS N, CAST(100.0*SUM(CASE WHEN PnLPercent>0 THEN 1 ELSE 0 END)/COUNT(*) AS DECIMAL(5,1)) AS WR $base GROUP BY CASE WHEN AiScore<60 THEN '1_<60' WHEN AiScore<70 THEN '2_60-70' WHEN AiScore<80 THEN '3_70-80' WHEN AiScore<90 THEN '4_80-90' ELSE '5_90+' END ORDER BY ScoreBkt" | Format-Table -AutoSize

Write-Host "=== B. 진입 시각(KST시)별 WR ===" -ForegroundColor Yellow
Q "SELECT DATEPART(HOUR,EntryTime) AS Hr, COUNT(*) AS N, CAST(100.0*SUM(CASE WHEN PnLPercent>0 THEN 1 ELSE 0 END)/COUNT(*) AS DECIMAL(5,1)) AS WR $base GROUP BY DATEPART(HOUR,EntryTime) HAVING COUNT(*)>=15 ORDER BY WR DESC" | Format-Table -AutoSize

Write-Host "=== C. 심볼별 WR (15건+ , 최고/최저) ===" -ForegroundColor Yellow
Q "SELECT TOP 12 Symbol, COUNT(*) AS N, CAST(100.0*SUM(CASE WHEN PnLPercent>0 THEN 1 ELSE 0 END)/COUNT(*) AS DECIMAL(5,1)) AS WR $base GROUP BY Symbol HAVING COUNT(*)>=15 ORDER BY WR DESC" | Format-Table -AutoSize
Q "SELECT TOP 12 Symbol, COUNT(*) AS N, CAST(100.0*SUM(CASE WHEN PnLPercent>0 THEN 1 ELSE 0 END)/COUNT(*) AS DECIMAL(5,1)) AS WR $base GROUP BY Symbol HAVING COUNT(*)>=15 ORDER BY WR ASC" | Format-Table -AutoSize

Write-Host "=== D. 보유시간별 WR ===" -ForegroundColor Yellow
Q "SELECT CASE WHEN holdingMinutes<10 THEN '1_<10m' WHEN holdingMinutes<30 THEN '2_10-30m' WHEN holdingMinutes<60 THEN '3_30-60m' WHEN holdingMinutes<180 THEN '4_1-3h' ELSE '5_3h+' END AS HoldBkt, COUNT(*) AS N, CAST(100.0*SUM(CASE WHEN PnLPercent>0 THEN 1 ELSE 0 END)/COUNT(*) AS DECIMAL(5,1)) AS WR $base GROUP BY CASE WHEN holdingMinutes<10 THEN '1_<10m' WHEN holdingMinutes<30 THEN '2_10-30m' WHEN holdingMinutes<60 THEN '3_30-60m' WHEN holdingMinutes<180 THEN '4_1-3h' ELSE '5_3h+' END ORDER BY HoldBkt" | Format-Table -AutoSize
