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
Write-Host "=== PositionState 컬럼명 ===" -ForegroundColor Cyan
Q "SELECT TOP 1 * FROM PositionState WITH (NOLOCK) WHERE UserId=1" | Get-Member -MemberType Property | Select-Object Name | Format-Table -AutoSize
Write-Host "=== 현재 열린 포지션 ===" -ForegroundColor Yellow
Q "SELECT * FROM PositionState WITH (NOLOCK) WHERE UserId=1" | Format-Table -AutoSize
Write-Host "=== 봇 최근 진입시도 로그 (24h) ===" -ForegroundColor Yellow
Q "SELECT TOP 15 EventTime, Symbol, Allowed, LEFT(Reason,45) AS Reason FROM Bot_Log WITH (NOLOCK) WHERE UserId=1 AND EventTime>=DATEADD(DAY,-1,GETDATE()) ORDER BY EventTime DESC" | Format-Table -AutoSize
