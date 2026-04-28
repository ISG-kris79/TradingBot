function Get-CS {
    $json = Get-Content "$PSScriptRoot\appsettings.json" -Raw | ConvertFrom-Json
    $enc = $json.ConnectionStrings.DefaultConnection
    $k = [byte[]](0x43, 0x6F, 0x69, 0x6E, 0x46, 0x46, 0x2D, 0x54, 0x72, 0x61, 0x64, 0x69, 0x6E, 0x67, 0x42, 0x6F, 0x74, 0x2D, 0x41, 0x45, 0x53, 0x32, 0x35, 0x36, 0x2D, 0x4B, 0x65, 0x79, 0x2D, 0x33, 0x32, 0x42)
    $f = [Convert]::FromBase64String($enc)
    $a = [System.Security.Cryptography.Aes]::Create(); $a.Key = $k
    $iv = New-Object byte[] $a.IV.Length; $c = New-Object byte[] ($f.Length - $a.IV.Length)
    [Buffer]::BlockCopy($f, 0, $iv, 0, $a.IV.Length); [Buffer]::BlockCopy($f, $a.IV.Length, $c, 0, $c.Length)
    $a.IV = $iv; $d = $a.CreateDecryptor($a.Key, $a.IV)
    $s = [Text.Encoding]::UTF8.GetString($d.TransformFinalBlock($c, 0, $c.Length)); $a.Dispose(); $d.Dispose(); return $s
}
function Q($sql) {
    $cn = New-Object System.Data.SqlClient.SqlConnection (Get-CS); $cn.Open()
    $cm = $cn.CreateCommand(); $cm.CommandText = $sql; $cm.CommandTimeout = 60
    $ap = New-Object System.Data.SqlClient.SqlDataAdapter $cm; $ds = New-Object System.Data.DataSet; [void]$ap.Fill($ds); $cn.Close()
    return $ds.Tables[0]
}
[Console]::OutputEncoding = [Text.Encoding]::UTF8

Write-Host "==== A. SP 존재 확인 ====" -ForegroundColor Cyan
Q "SELECT name, create_date, modify_date FROM sys.procedures WHERE name='sp_BulkPreloadOpenTime'" | Format-Table -AutoSize

Write-Host ""
Write-Host "==== B. SP 직접 실행 (5m) ====" -ForegroundColor Cyan
try {
    $cn = New-Object System.Data.SqlClient.SqlConnection (Get-CS); $cn.Open()
    $cm = $cn.CreateCommand()
    $cm.CommandText = "dbo.sp_BulkPreloadOpenTime"
    $cm.CommandType = [System.Data.CommandType]::StoredProcedure
    $cm.CommandTimeout = 60
    $p = $cm.Parameters.Add("@Interval", [System.Data.SqlDbType]::NVarChar, 8)
    $p.Value = "5m"
    $rd = $cm.ExecuteReader()
    $cnt = 0
    while ($rd.Read() -and $cnt -lt 3) {
        Write-Host ("  Row " + $cnt + ": sym=" + $rd.GetValue(0) + " ot=" + $rd.GetValue(1))
        $cnt++
    }
    Write-Host "  Total rows read: $cnt (sample only)"
    $rd.Close(); $cn.Close()
} catch {
    Write-Host "  ❌ EXCEPTION: $($_.Exception.Message)" -ForegroundColor Red
    Write-Host "  Inner: $($_.Exception.InnerException.Message)"
}

Write-Host ""
Write-Host "==== C. CandleData 테이블 5m 행 수 (마지막 24h) ====" -ForegroundColor Cyan
Q "SELECT COUNT(*) AS Cnt5m FROM CandleData WHERE [Interval]='5m' AND OpenTime >= DATEADD(HOUR,-24,GETDATE())" | Format-Table -AutoSize

Write-Host ""
Write-Host "==== D. SP 본문 보기 ====" -ForegroundColor Cyan
Q "SELECT OBJECT_DEFINITION(OBJECT_ID('sp_BulkPreloadOpenTime')) AS Body" | Format-Table -Wrap
