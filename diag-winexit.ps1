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
$valid = "FROM TradeHistory WITH (NOLOCK) WHERE UserId=10 AND IsClosed=1 AND Category='LORENTZIAN' AND EntryPrice>0 AND ExitPrice>0 AND ABS(ExitPrice/EntryPrice-1)<0.8 AND EntryTime>=DATEADD(DAY,-30,GETDATE()) AND ExitPrice>EntryPrice"
$rk = "CASE WHEN ExitReason LIKE 'TP%' THEN 'TP' WHEN ExitReason LIKE '%Trail%' THEN 'TRAIL' WHEN ExitReason LIKE '%MACD%' THEN 'MACD' WHEN ExitReason LIKE '%BE%' OR ExitReason LIKE N'%bon%' THEN 'BE' WHEN ExitReason LIKE 'EXTERNAL%' THEN 'EXT' WHEN ExitReason LIKE 'SL%' THEN 'SL' ELSE 'OTHER' END"
Write-Host "=== 승리거래 청산사유별 (N / 평균수익move% / 평균보유분) ===" -ForegroundColor Cyan
Q "SELECT $rk AS Reason, COUNT(*) AS N, CAST(AVG((ExitPrice/EntryPrice-1)*100) AS DECIMAL(6,3)) AS AvgMovePct, AVG(holdingMinutes) AS AvgHoldMin $valid GROUP BY $rk ORDER BY N DESC" | Format-Table -AutoSize
Write-Host "=== 참고: 승리거래 ExitReason 원문 TOP 12 ===" -ForegroundColor Yellow
Q "SELECT TOP 12 LEFT(ExitReason,40) AS ExitReason, COUNT(*) AS N $valid GROUP BY LEFT(ExitReason,40) ORDER BY N DESC" | Format-Table -AutoSize
