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
    $cm = $cn.CreateCommand(); $cm.CommandText = $sql; $cm.CommandTimeout = 30
    $ap = New-Object System.Data.SqlClient.SqlDataAdapter $cm; $ds = New-Object System.Data.DataSet; [void]$ap.Fill($ds); $cn.Close()
    return $ds.Tables[0]
}
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8

Write-Host "=== Order_Error 최근 30분 ===" -ForegroundColor Cyan
Q @"
SELECT ErrorCode, LEFT(ErrorMsg,60) AS Msg, COUNT(*) AS Cnt
FROM Order_Error WHERE UserId=1 AND EventTime >= DATEADD(MINUTE,-30,GETDATE())
GROUP BY ErrorCode, LEFT(ErrorMsg,60) ORDER BY Cnt DESC
"@ | Format-Table -AutoSize

Write-Host "=== DB 설정 확인 ===" -ForegroundColor Cyan
Q "SELECT Id, DefaultMargin, PumpMargin, EnableMajorTrading, DefaultLeverage, MaxMajorSlots, MaxPumpSlots FROM GeneralSettings WHERE Id=1" | Format-List

Write-Host "=== GeneralSettings 컬럼 확인 ===" -ForegroundColor Cyan
Q @"
SELECT COLUMN_NAME, DATA_TYPE FROM INFORMATION_SCHEMA.COLUMNS
WHERE TABLE_NAME='GeneralSettings' AND COLUMN_NAME IN ('MaxMajorSlots','MaxPumpSlots','EnableMajorTrading')
"@ | Format-Table -AutoSize
