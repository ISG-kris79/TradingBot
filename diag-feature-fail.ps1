$ErrorActionPreference = "Stop"
[Console]::OutputEncoding = [Text.Encoding]::UTF8
$AesKey = [byte[]](0x43, 0x6F, 0x69, 0x6E, 0x46, 0x46, 0x2D, 0x54, 0x72, 0x61, 0x64, 0x69, 0x6E, 0x67, 0x42, 0x6F, 0x74, 0x2D, 0x41, 0x45, 0x53, 0x32, 0x35, 0x36, 0x2D, 0x4B, 0x65, 0x79, 0x2D, 0x33, 0x32, 0x42)
function AesDecrypt($enc) {
    if ([string]::IsNullOrEmpty($enc)) { return "" }
    $f = [Convert]::FromBase64String($enc)
    $a = [System.Security.Cryptography.Aes]::Create(); $a.Key = $AesKey
    $iv = New-Object byte[] $a.IV.Length; $c = New-Object byte[] ($f.Length - $a.IV.Length)
    [Buffer]::BlockCopy($f, 0, $iv, 0, $a.IV.Length); [Buffer]::BlockCopy($f, $a.IV.Length, $c, 0, $c.Length)
    $a.IV = $iv; $d = $a.CreateDecryptor($a.Key, $a.IV)
    $s = [Text.Encoding]::UTF8.GetString($d.TransformFinalBlock($c, 0, $c.Length)); $a.Dispose(); $d.Dispose(); return $s
}
function Q($sql) {
    $cn = New-Object System.Data.SqlClient.SqlConnection (AesDecrypt (Get-Content "$PSScriptRoot\appsettings.json" -Raw | ConvertFrom-Json).ConnectionStrings.DefaultConnection); $cn.Open()
    $cm = $cn.CreateCommand(); $cm.CommandText = $sql; $cm.CommandTimeout = 60
    $ap = New-Object System.Data.SqlClient.SqlDataAdapter $cm
    $ds = New-Object System.Data.DataSet; [void]$ap.Fill($ds); $cn.Close()
    return $ds.Tables[0]
}

$cutoff = "2026-04-23 03:00:00"
Write-Host "[A] Feature_Extraction_Failed - 5 unique symbols + counts since noon"
$a = "SELECT Symbol, COUNT(*) AS Cnt, MIN(EventTime) AS FirstAt, MAX(EventTime) AS LastAt FROM Bot_Log WHERE UserId=1 AND Reason='Feature_Extraction_Failed' AND EventTime >= '$cutoff' GROUP BY Symbol ORDER BY Cnt DESC"
Q $a | Format-Table -AutoSize

Write-Host "`n[B] FooterLogs - Feature 추출 실패 (데이터 부족) 메시지"
$b = "SELECT TOP 30 Timestamp, LEFT(Message,200) AS Msg FROM FooterLogs WHERE Timestamp >= '$cutoff' AND (Message LIKE '%Feature 추출 실패%' OR Message LIKE '%데이터 부족%') ORDER BY Timestamp DESC"
Q $b | Format-Table -AutoSize -Wrap
