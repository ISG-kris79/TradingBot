function Get-CS {
    $json = Get-Content "$PSScriptRoot\appsettings.json" -Raw | ConvertFrom-Json
    $enc = $json.ConnectionStrings.DefaultConnection
    $k = [byte[]](0x43,0x6F,0x69,0x6E,0x46,0x46,0x2D,0x54,0x72,0x61,0x64,0x69,0x6E,0x67,0x42,0x6F,0x74,0x2D,0x41,0x45,0x53,0x32,0x35,0x36,0x2D,0x4B,0x65,0x79,0x2D,0x33,0x32,0x42)
    $f = [Convert]::FromBase64String($enc); $a = [System.Security.Cryptography.Aes]::Create(); $a.Key = $k
    $iv = New-Object byte[] $a.IV.Length; $c = New-Object byte[] ($f.Length - $a.IV.Length)
    [Buffer]::BlockCopy($f,0,$iv,0,$a.IV.Length); [Buffer]::BlockCopy($f,$a.IV.Length,$c,0,$c.Length); $a.IV = $iv
    $d = $a.CreateDecryptor($a.Key,$a.IV); $s = [Text.Encoding]::UTF8.GetString($d.TransformFinalBlock($c,0,$c.Length)); $a.Dispose(); $d.Dispose(); return $s
}
function Q($sql){ $cn=New-Object System.Data.SqlClient.SqlConnection (Get-CS);$cn.Open();$cm=$cn.CreateCommand();$cm.CommandText=$sql;$cm.CommandTimeout=90;$ap=New-Object System.Data.SqlClient.SqlDataAdapter $cm;$ds=New-Object System.Data.DataSet;[void]$ap.Fill($ds);$cn.Close();return $ds.Tables[0]}
[Console]::OutputEncoding=[System.Text.Encoding]::UTF8
Write-Host "=== UID10 현재 열린 포지션 (HighestROE/현재상태) ===" -ForegroundColor Cyan
Q "SELECT * FROM PositionState WITH (NOLOCK) WHERE UserId=10" | Format-Table -AutoSize
Write-Host "=== UID10 최근 3일 큰 손실 거래 (ROE 기준, SL 안터진 흔적) ===" -ForegroundColor Yellow
Q "SELECT TOP 20 EntryTime, Symbol, Category AS Cat, ROUND(PnLPercent,1) AS RoePct, ROUND((ExitPrice/EntryPrice-1)*100,2) AS PxMovePct, holdingMinutes AS Hold, LEFT(ExitReason,32) AS Exit FROM TradeHistory WITH (NOLOCK) WHERE UserId=10 AND IsClosed=1 AND EntryPrice>0 AND ExitPrice>0 AND EntryTime>=DATEADD(DAY,-3,GETDATE()) ORDER BY (ExitPrice/EntryPrice-1) ASC" | Format-Table -AutoSize
Write-Host "=== UID10 최근 3일 청산사유 분포 (SL이 실제로 작동하나) ===" -ForegroundColor Yellow
Q "SELECT CASE WHEN ExitReason LIKE 'SL%' OR ExitReason LIKE '%STOP%' THEN 'SL손절' WHEN ExitReason LIKE 'TP%' THEN 'TP' WHEN ExitReason LIKE 'EXTERNAL%' THEN '외부청산' WHEN ExitReason LIKE '%MANUAL%' OR ExitReason LIKE N'%사용자%' THEN '수동' ELSE 'OTHER' END AS Kind, COUNT(*) AS N, ROUND(AVG((ExitPrice/EntryPrice-1)*100),2) AS AvgPxMove FROM TradeHistory WITH (NOLOCK) WHERE UserId=10 AND IsClosed=1 AND EntryPrice>0 AND ExitPrice>0 AND EntryTime>=DATEADD(DAY,-3,GETDATE()) GROUP BY CASE WHEN ExitReason LIKE 'SL%' OR ExitReason LIKE '%STOP%' THEN 'SL손절' WHEN ExitReason LIKE 'TP%' THEN 'TP' WHEN ExitReason LIKE 'EXTERNAL%' THEN '외부청산' WHEN ExitReason LIKE '%MANUAL%' OR ExitReason LIKE N'%사용자%' THEN '수동' ELSE 'OTHER' END ORDER BY N DESC" | Format-Table -AutoSize
