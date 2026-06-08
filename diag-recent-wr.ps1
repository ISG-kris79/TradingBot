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
Write-Host "=== UID10 일자별 승률 (가격기반, 최근 7일) ===" -ForegroundColor Cyan
Q "SELECT CAST(EntryTime AS DATE) AS D, COUNT(*) AS N, SUM(CASE WHEN ExitPrice>EntryPrice THEN 1 ELSE 0 END) AS Wins, CAST(100.0*SUM(CASE WHEN ExitPrice>EntryPrice THEN 1 ELSE 0 END)/COUNT(*) AS DECIMAL(5,1)) AS WR FROM TradeHistory WITH (NOLOCK) WHERE UserId=10 AND IsClosed=1 AND EntryPrice>0 AND ExitPrice>0 AND EntryTime>=DATEADD(DAY,-7,GETDATE()) GROUP BY CAST(EntryTime AS DATE) ORDER BY D" | Format-Table -AutoSize
Write-Host "=== UID10 최근 7일 카테고리별 (가격기반) ===" -ForegroundColor Yellow
Q "SELECT Category AS Cat, COUNT(*) AS N, CAST(100.0*SUM(CASE WHEN ExitPrice>EntryPrice THEN 1 ELSE 0 END)/COUNT(*) AS DECIMAL(5,1)) AS WR FROM TradeHistory WITH (NOLOCK) WHERE UserId=10 AND IsClosed=1 AND EntryPrice>0 AND ExitPrice>0 AND EntryTime>=DATEADD(DAY,-7,GETDATE()) GROUP BY Category ORDER BY N DESC" | Format-Table -AutoSize
Write-Host "=== 부분청산(EXTERNAL_PARTIAL) 제외 시 — 포지션단위 근사 ===" -ForegroundColor Yellow
Q "SELECT COUNT(*) AS N, CAST(100.0*SUM(CASE WHEN ExitPrice>EntryPrice THEN 1 ELSE 0 END)/COUNT(*) AS DECIMAL(5,1)) AS WR FROM TradeHistory WITH (NOLOCK) WHERE UserId=10 AND IsClosed=1 AND EntryPrice>0 AND ExitPrice>0 AND ExitReason NOT LIKE 'EXTERNAL_PARTIAL%' AND EntryTime>=DATEADD(DAY,-7,GETDATE())" | Format-Table -AutoSize
