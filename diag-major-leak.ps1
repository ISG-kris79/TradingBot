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

Write-Host "[1] EnableMajorTrading current setting"
Q "SELECT EnableMajorTrading, MaxMajorSlots, MaxPumpSlots FROM GeneralSettings WHERE Id=1" | Format-List

Write-Host "`n[2] Recent 6h entries - major coins"
$sql = "SELECT TOP 30 EntryTime, Symbol, Strategy, Side, CAST(EntryPrice AS DECIMAL(18,6)) AS Entry, CAST(Quantity AS DECIMAL(18,4)) AS Qty FROM TradeHistory WHERE UserId=1 AND IsSimulation=0 AND EntryTime >= DATEADD(HOUR,-6,GETUTCDATE()) AND Symbol IN ('BTCUSDT','ETHUSDT','SOLUSDT','XRPUSDT','BNBUSDT','DOGEUSDT') ORDER BY EntryTime DESC"
Q $sql | Format-Table -AutoSize

Write-Host "`n[3] Bot_Log recent 6h - major coins"
$sql2 = "SELECT TOP 30 EventTime, Symbol, Direction, Allowed, Reason FROM Bot_Log WHERE UserId=1 AND EventTime >= DATEADD(HOUR,-6,GETUTCDATE()) AND Symbol IN ('BTCUSDT','ETHUSDT','SOLUSDT','XRPUSDT','BNBUSDT','DOGEUSDT') ORDER BY EventTime DESC"
Q $sql2 | Format-Table -AutoSize -Wrap
