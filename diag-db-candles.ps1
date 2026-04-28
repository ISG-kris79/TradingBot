$ErrorActionPreference = "Stop"
[Console]::OutputEncoding = [Text.Encoding]::UTF8
$AesKey = [byte[]](0x43, 0x6F, 0x69, 0x6E, 0x46, 0x46, 0x2D, 0x54, 0x72, 0x61, 0x64, 0x69, 0x6E, 0x67, 0x42, 0x6F, 0x74, 0x2D, 0x41, 0x45, 0x53, 0x32, 0x35, 0x36, 0x2D, 0x4B, 0x65, 0x79, 0x2D, 0x33, 0x32, 0x42)
function AesDecrypt($enc) {
    if ([string]::IsNullOrEmpty($enc)) { return "" }
    $f = [Convert]::FromBase64String($enc)
    $a = [System.Security.Cryptography.Aes]::Create(); $a.Key = $AesKey
    $iv = New-Object byte[] $a.IV.Length
    $c = New-Object byte[] ($f.Length - $a.IV.Length)
    [Buffer]::BlockCopy($f, 0, $iv, 0, $a.IV.Length)
    [Buffer]::BlockCopy($f, $a.IV.Length, $c, 0, $c.Length)
    $a.IV = $iv
    $d = $a.CreateDecryptor($a.Key, $a.IV)
    $s = [Text.Encoding]::UTF8.GetString($d.TransformFinalBlock($c, 0, $c.Length))
    $a.Dispose(); $d.Dispose()
    return $s
}
function Q($sql) {
    $cn = New-Object System.Data.SqlClient.SqlConnection (AesDecrypt (Get-Content "$PSScriptRoot\appsettings.json" -Raw | ConvertFrom-Json).ConnectionStrings.DefaultConnection)
    $cn.Open()
    $cm = $cn.CreateCommand(); $cm.CommandText = $sql; $cm.CommandTimeout = 60
    $ap = New-Object System.Data.SqlClient.SqlDataAdapter $cm
    $ds = New-Object System.Data.DataSet; [void]$ap.Fill($ds); $cn.Close()
    return $ds.Tables[0]
}

Write-Host "==== [1] CandleData 5m 최근 5분 INSERT 추이 (수집 LIVE 여부) ===="
Q "SELECT COUNT(*) AS Recent5min FROM CandleData WITH (NOLOCK) WHERE IntervalText='5m' AND OpenTime >= DATEADD(MINUTE, -10, GETUTCDATE())" | Format-List

Write-Host ""
Write-Host "==== [2] CandleData 5m 최근 1시간 INSERT (활발 수집 확인) ===="
Q "SELECT COUNT(*) AS Recent1Hour, COUNT(DISTINCT Symbol) AS UniqueSymbols FROM CandleData WITH (NOLOCK) WHERE IntervalText='5m' AND OpenTime >= DATEADD(HOUR, -1, GETUTCDATE())" | Format-List

Write-Host ""
Write-Host "==== [3] 5분봉 심볼 커버리지 TOP 30 (Top60 추적 심볼 수집 여부) ===="
Q "SELECT TOP 30 Symbol, COUNT(*) AS Rows, MAX(OpenTime) AS LatestKST FROM CandleData WHERE IntervalText='5m' GROUP BY Symbol ORDER BY Rows DESC" | Format-Table -AutoSize

Write-Host ""
Write-Host "==== [4] APEUSDT 5분봉 DB 보유 (Backtest 가능 여부) ===="
Q "SELECT COUNT(*) AS Cnt, MIN(OpenTime) AS Earliest, MAX(OpenTime) AS Latest FROM CandleData WITH (NOLOCK) WHERE Symbol='APEUSDT' AND IntervalText='5m'" | Format-List

Write-Host ""
Write-Host "==== [5] CandleHistory / MarketCandles 백업 테이블 ===="
Q "SELECT 'CandleHistory' AS Tbl, COUNT(*) AS Rows, MIN(OpenTime) AS Earliest, MAX(OpenTime) AS Latest FROM CandleHistory WHERE [Interval]='5m' UNION ALL SELECT 'MarketCandles', COUNT(*), MIN(OpenTime), MAX(OpenTime) FROM MarketCandles" | Format-Table -AutoSize
