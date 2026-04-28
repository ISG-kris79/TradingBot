
function Get-CS {
    $json = Get-Content "$PSScriptRoot\appsettings.json" -Raw | ConvertFrom-Json
    $enc = $json.ConnectionStrings.DefaultConnection
    $k = [byte[]](0x43,0x6F,0x69,0x6E,0x46,0x46,0x2D,0x54,0x72,0x61,0x64,0x69,0x6E,0x67,0x42,0x6F,0x74,0x2D,0x41,0x45,0x53,0x32,0x35,0x36,0x2D,0x4B,0x65,0x79,0x2D,0x33,0x32,0x42)
    $f = [Convert]::FromBase64String($enc)
    $a = [System.Security.Cryptography.Aes]::Create(); $a.Key = $k
    $iv = New-Object byte[] $a.IV.Length; $c = New-Object byte[] ($f.Length - $a.IV.Length)
    [Buffer]::BlockCopy($f,0,$iv,0,$a.IV.Length); [Buffer]::BlockCopy($f,$a.IV.Length,$c,0,$c.Length)
    $a.IV = $iv; $d = $a.CreateDecryptor($a.Key,$a.IV)
    $s = [Text.Encoding]::UTF8.GetString($d.TransformFinalBlock($c,0,$c.Length)); $a.Dispose(); $d.Dispose(); return $s
}
function Q($sql,$t=30) {
    $cn = New-Object System.Data.SqlClient.SqlConnection (Get-CS); $cn.Open()
    $cm = $cn.CreateCommand(); $cm.CommandText = $sql; $cm.CommandTimeout = $t
    $ap = New-Object System.Data.SqlClient.SqlDataAdapter $cm; $ds = New-Object System.Data.DataSet; [void]$ap.Fill($ds); $cn.Close()
    return $ds.Tables[0]
}
function Exec($sql,$t=30) {
    $cn = New-Object System.Data.SqlClient.SqlConnection (Get-CS); $cn.Open()
    $cm = $cn.CreateCommand(); $cm.CommandText = $sql; $cm.CommandTimeout = $t
    [void]$cm.ExecuteNonQuery(); $cn.Close()
}

Write-Host "=== [1] FooterLogs 상태 ===" -ForegroundColor Cyan
Q "SELECT MAX(Id) AS MaxId, COUNT(*) AS Total, CONVERT(varchar(30),MAX(Timestamp),120) AS LastInsert, DATEDIFF(MINUTE,MAX(Timestamp),GETDATE()) AS MinAgo FROM FooterLogs" | Format-Table -AutoSize

Write-Host "=== [2] 서버 시각 ===" -ForegroundColor Cyan
Q "SELECT CONVERT(varchar(30),GETDATE(),120) AS Now_Server" | Format-Table -AutoSize

Write-Host "=== [3] INSERT 직접 테스트 ===" -ForegroundColor Yellow
try {
    Exec "INSERT INTO FooterLogs (Timestamp, Message) VALUES (GETDATE(), '[DIAG] test')" 10
    Write-Host "INSERT OK" -ForegroundColor Green
} catch { Write-Host "INSERT FAIL: $($_.Exception.Message)" -ForegroundColor Red }

Write-Host "=== [4] INSERT 후 최신 3건 ===" -ForegroundColor Cyan
Q "SELECT TOP 3 Id, CONVERT(varchar(30),Timestamp,120) AS TS, LEFT(Message,100) AS Msg FROM FooterLogs ORDER BY Id DESC" | Format-Table -AutoSize

Write-Host "=== [5] FooterLogs 컬럼 구조 ===" -ForegroundColor Cyan
Q "SELECT name, TYPE_NAME(user_type_id) AS Type, max_length, is_nullable FROM sys.columns WHERE object_id=OBJECT_ID(N'FooterLogs')" | Format-Table -AutoSize

Write-Host "=== [6] Full-Text Index 크롤 상태 ===" -ForegroundColor Cyan
Q "SELECT fi.change_tracking_state_desc, fi.crawl_type_desc, fi.has_crawl_completed FROM sys.fulltext_indexes fi JOIN sys.tables t ON fi.object_id=t.object_id WHERE t.name='FooterLogs'" | Format-Table -AutoSize

Write-Host "=== [7] 오류 로그 DB (rollback 관련) ===" -ForegroundColor Red
Q "SELECT TOP 10 CONVERT(varchar(30),Timestamp,120) AS TS, Message FROM FooterLogs WHERE Message LIKE '%rollback%' OR Message LIKE '%롤백%' OR Message LIKE '%Footer%저장%실패%' ORDER BY Id DESC" | Format-Table -AutoSize
