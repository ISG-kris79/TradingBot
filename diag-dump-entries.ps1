function Get-CS {
    $json = Get-Content "$PSScriptRoot\appsettings.json" -Raw | ConvertFrom-Json
    $enc = $json.ConnectionStrings.DefaultConnection
    $k = [byte[]](0x43,0x6F,0x69,0x6E,0x46,0x46,0x2D,0x54,0x72,0x61,0x64,0x69,0x6E,0x67,0x42,0x6F,0x74,0x2D,0x41,0x45,0x53,0x32,0x35,0x36,0x2D,0x4B,0x65,0x79,0x2D,0x33,0x32,0x42)
    $f = [Convert]::FromBase64String($enc); $a = [System.Security.Cryptography.Aes]::Create(); $a.Key = $k
    $iv = New-Object byte[] $a.IV.Length; $c = New-Object byte[] ($f.Length - $a.IV.Length)
    [Buffer]::BlockCopy($f,0,$iv,0,$a.IV.Length); [Buffer]::BlockCopy($f,$a.IV.Length,$c,0,$c.Length); $a.IV = $iv
    $d = $a.CreateDecryptor($a.Key,$a.IV); $s = [Text.Encoding]::UTF8.GetString($d.TransformFinalBlock($c,0,$c.Length)); $a.Dispose(); $d.Dispose(); return $s
}
$cn = New-Object System.Data.SqlClient.SqlConnection (Get-CS); $cn.Open()
$cm = $cn.CreateCommand()
# EntryTime을 UTC epoch ms로 (저장이 로컬 KST라 가정 → UTC=-9h). 앵커는 검증기에서 가격으로 보정.
$cm.CommandText = @"
SELECT Symbol,
  CAST(DATEDIFF_BIG(MILLISECOND,'1970-01-01', DATEADD(HOUR,-9,EntryTime)) AS BIGINT) AS EntryUtcMs,
  EntryPrice, ExitPrice, Category,
  CASE WHEN ExitPrice>EntryPrice THEN 1 ELSE 0 END AS Win
FROM TradeHistory WITH (NOLOCK)
WHERE UserId=10 AND IsClosed=1 AND EntryPrice>0 AND ExitPrice>0
  AND ABS(ExitPrice/EntryPrice-1)<0.8
  AND EntryTime >= DATEADD(DAY,-90,GETDATE())
ORDER BY EntryTime
"@
$ap = New-Object System.Data.SqlClient.SqlDataAdapter $cm
$dt = New-Object System.Data.DataTable; [void]$ap.Fill($dt); $cn.Close()
$out = "Tools\LorentzianValidator\live-entries.csv"
$sw = New-Object System.IO.StreamWriter($out, $false, [Text.Encoding]::ASCII)
$sw.WriteLine("Symbol,EntryUtcMs,EntryPrice,ExitPrice,Category,Win")
foreach ($r in $dt.Rows) { $sw.WriteLine("$($r.Symbol),$($r.EntryUtcMs),$($r.EntryPrice),$($r.ExitPrice),$($r.Category),$($r.Win)") }
$sw.Close()
Write-Host "dumped $($dt.Rows.Count) entries -> $out"
