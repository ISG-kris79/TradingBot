# [v5.24.4] Performance = daily SUM(PnL) by EntryTime (KST). Per-trade rows NOT shown.
#   Real user only (UserId=1, IsSimulation=0, IsClosed=1). ASCII labels (PS5.1 Korean breaks).
$ErrorActionPreference="Stop";[Console]::OutputEncoding=[Text.Encoding]::UTF8
$AesKey=[byte[]](0x43,0x6F,0x69,0x6E,0x46,0x46,0x2D,0x54,0x72,0x61,0x64,0x69,0x6E,0x67,0x42,0x6F,0x74,0x2D,0x41,0x45,0x53,0x32,0x35,0x36,0x2D,0x4B,0x65,0x79,0x2D,0x33,0x32,0x42)
function AesDecrypt($e){if([string]::IsNullOrEmpty($e)){return ""}$f=[Convert]::FromBase64String($e);$a=[System.Security.Cryptography.Aes]::Create();$a.Key=$AesKey;$iv=New-Object byte[] $a.IV.Length;$c=New-Object byte[] ($f.Length-$a.IV.Length);[Buffer]::BlockCopy($f,0,$iv,0,$a.IV.Length);[Buffer]::BlockCopy($f,$a.IV.Length,$c,0,$c.Length);$a.IV=$iv;$d=$a.CreateDecryptor($a.Key,$a.IV);$s=[Text.Encoding]::UTF8.GetString($d.TransformFinalBlock($c,0,$c.Length));$a.Dispose();$d.Dispose();return $s}
function Q($sql){$cn=New-Object System.Data.SqlClient.SqlConnection (AesDecrypt (Get-Content "$PSScriptRoot\appsettings.json" -Raw|ConvertFrom-Json).ConnectionStrings.DefaultConnection);$cn.Open();$cm=$cn.CreateCommand();$cm.CommandText=$sql;$cm.CommandTimeout=60;$ap=New-Object System.Data.SqlClient.SqlDataAdapter $cm;$ds=New-Object System.Data.DataSet;[void]$ap.Fill($ds);$cn.Close();return $ds.Tables[0]}

$days = 30
if ($args.Count -ge 1) { $n=0; if ([int]::TryParse($args[0],[ref]$n)) { $days=$n } }
$flt = "UserId=1 AND IsSimulation=0 AND IsClosed=1 AND EntryTime >= DATEADD(day,-$days,GETUTCDATE())"

Write-Host "==== Performance: daily SUM(PnL) by EntryTime (KST), last $days days ====" -ForegroundColor Cyan
Write-Host "     (UserId=1, IsSimulation=0, IsClosed=1 -- live only, no per-trade rows)" -ForegroundColor DarkGray

$kstDay = "CONVERT(date, EntryTime AT TIME ZONE 'UTC' AT TIME ZONE 'Korea Standard Time')"
$daily = Q "SELECT $kstDay AS EntryDayKST, COUNT(*) AS N, SUM(CASE WHEN PnL>0 THEN 1 ELSE 0 END) AS Wins, CAST(ROUND(100.0*SUM(CASE WHEN PnL>0 THEN 1 ELSE 0 END)/NULLIF(COUNT(*),0),1) AS decimal(5,1)) AS WinPct, CAST(ROUND(SUM(PnL),2) AS decimal(18,2)) AS DayPnlUSD FROM TradeHistory WITH (NOLOCK) WHERE $flt GROUP BY $kstDay ORDER BY EntryDayKST DESC"
$daily | Format-Table -AutoSize

$tot = Q "SELECT COUNT(*) AS TotalN, SUM(CASE WHEN PnL>0 THEN 1 ELSE 0 END) AS Wins, CAST(ROUND(100.0*SUM(CASE WHEN PnL>0 THEN 1 ELSE 0 END)/NULLIF(COUNT(*),0),1) AS decimal(5,1)) AS WinPct, CAST(ROUND(SUM(PnL),2) AS decimal(18,2)) AS TotalPnlUSD, CAST(ROUND(AVG(PnL),3) AS decimal(18,3)) AS AvgPerTradeUSD FROM TradeHistory WITH (NOLOCK) WHERE $flt"
Write-Host "---- Total ($days days) ----" -ForegroundColor Yellow
$tot | Format-Table -AutoSize
