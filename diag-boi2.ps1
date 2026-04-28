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

Write-Host "[A] BOI pattern search"
$a = "SELECT DISTINCT Symbol FROM TradeHistory WHERE Symbol LIKE '%BOI%' OR Symbol LIKE 'B%USDT'"
Q $a | Format-Table -AutoSize

Write-Host "`n[B] last 6h biggest losses (Roi <= -50%)"
$b = "SELECT TOP 30 Symbol, Strategy, Side, CAST(EntryPrice AS DECIMAL(18,8)) AS Entry, CAST(ExitPrice AS DECIMAL(18,8)) AS Exit_, CAST(PnL AS DECIMAL(18,4)) AS PnL, CAST(PnLPercent AS DECIMAL(8,2)) AS Roi, holdingMinutes AS Hold, ExitReason, EntryTime FROM TradeHistory WHERE UserId=1 AND IsClosed=1 AND IsSimulation=0 AND ExitTime >= DATEADD(HOUR,-6,GETUTCDATE()) AND PnLPercent <= -50 ORDER BY PnLPercent ASC"
Q $b | Format-Table -AutoSize -Wrap

Write-Host "`n[C] active positions on Binance (positionRisk)"
Write-Host "(직접 audit-binance-orders2.ps1 실행 권장)"
