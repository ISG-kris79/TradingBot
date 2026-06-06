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
Write-Host "=== 최근 2일 트레이드 (카테고리별) ===" -ForegroundColor Cyan
Q "SELECT Category AS Cat, COUNT(*) AS N, SUM(CASE WHEN PnL>0 THEN 1 ELSE 0 END) AS Wins, ROUND(SUM(PnL),2) AS Net FROM TradeHistory WITH (NOLOCK) WHERE UserId=1 AND IsClosed=1 AND PnL<>0 AND EntryTime>=DATEADD(DAY,-2,GETDATE()) GROUP BY Category ORDER BY Net" | Format-Table -AutoSize
Write-Host "=== 최근 2일 개별 (시간순) ===" -ForegroundColor Yellow
Q "SELECT EntryTime, Symbol, Category AS Cat, ROUND(PnL,2) AS PnL, ROUND(PnLPercent,1) AS Roe, holdingMinutes AS Hold FROM TradeHistory WITH (NOLOCK) WHERE UserId=1 AND IsClosed=1 AND PnL<>0 AND EntryTime>=DATEADD(DAY,-2,GETDATE()) ORDER BY EntryTime" | Format-Table -AutoSize
