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
Write-Host "=== UID10 카테고리별 WR (30일, 카운트=신뢰가능 / ROE중앙값) ===" -ForegroundColor Cyan
Q "SELECT Category AS Cat, COUNT(*) AS N, SUM(CASE WHEN PnLPercent>0 THEN 1 ELSE 0 END) AS Wins, CAST(100.0*SUM(CASE WHEN PnLPercent>0 THEN 1 ELSE 0 END)/COUNT(*) AS DECIMAL(5,1)) AS WR, ROUND(AVG(CASE WHEN PnLPercent>0 THEN PnLPercent END),1) AS AvgWinRoe, ROUND(AVG(CASE WHEN PnLPercent<=0 THEN PnLPercent END),1) AS AvgLossRoe FROM TradeHistory WITH (NOLOCK) WHERE UserId=10 AND IsClosed=1 AND PnLPercent<>0 AND EntryTime>=DATEADD(DAY,-30,GETDATE()) GROUP BY Category ORDER BY N DESC" | Format-Table -AutoSize
Write-Host "=== UID10 최근 3일 개별 (ROE 기준) ===" -ForegroundColor Yellow
Q "SELECT EntryTime, Symbol, Category AS Cat, ROUND(PnLPercent,1) AS Roe, holdingMinutes AS Hold, LEFT(ExitReason,26) AS Exit FROM TradeHistory WITH (NOLOCK) WHERE UserId=10 AND IsClosed=1 AND PnLPercent<>0 AND EntryTime>=DATEADD(DAY,-3,GETDATE()) ORDER BY EntryTime DESC" | Format-Table -AutoSize
Write-Host "=== UID10 현재 열린 포지션 6개 ===" -ForegroundColor Yellow
Q "SELECT * FROM PositionState WITH (NOLOCK) WHERE UserId=10" | Format-Table -AutoSize
