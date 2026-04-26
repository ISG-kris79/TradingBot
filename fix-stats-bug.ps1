$ErrorActionPreference = "Stop"
[Console]::OutputEncoding = [Text.Encoding]::UTF8

$AesKey = [byte[]](0x43, 0x6F, 0x69, 0x6E, 0x46, 0x46, 0x2D, 0x54, 0x72, 0x61, 0x64, 0x69, 0x6E, 0x67, 0x42, 0x6F, 0x74, 0x2D, 0x41, 0x45, 0x53, 0x32, 0x35, 0x36, 0x2D, 0x4B, 0x65, 0x79, 0x2D, 0x33, 0x32, 0x42)
function AesDecrypt($enc) {
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
$json = Get-Content "$PSScriptRoot\appsettings.json" -Raw | ConvertFrom-Json
$cn = New-Object System.Data.SqlClient.SqlConnection (AesDecrypt $json.ConnectionStrings.DefaultConnection)
$cn.Open()

# === [STEP 1] sp_GetTodayStatsByCategory 재배포 (ExitTime 기준) ===
$sp = @"
CREATE OR ALTER PROCEDURE dbo.sp_GetTodayStatsByCategory
    @todayStart DATETIME2,
    @userId     INT
AS
BEGIN
    SET NOCOUNT ON;
    WITH ClosedToday AS (
        SELECT Category, Symbol, EntryTime, SUM(ISNULL(PnL, 0)) AS TotalPnL
        FROM dbo.TradeHistory
        WHERE Category IS NOT NULL AND IsClosed = 1
          AND ExitTime >= @todayStart
          AND (@userId = 0 OR UserId = @userId)
        GROUP BY Category, Symbol, EntryTime
    ),
    ClosedAgg AS (
        SELECT Category, COUNT(*) AS ClosedCount,
               SUM(CASE WHEN TotalPnL > 0 THEN 1 ELSE 0 END) AS Wins,
               SUM(CASE WHEN TotalPnL < 0 THEN 1 ELSE 0 END) AS Losses,
               SUM(TotalPnL) AS TotalPnL
        FROM ClosedToday GROUP BY Category
    ),
    OpenToday AS (
        SELECT Category,
               COUNT(DISTINCT Symbol + '|' + CONVERT(NVARCHAR(30), EntryTime, 121)) AS NewEntries
        FROM dbo.TradeHistory
        WHERE Category IS NOT NULL AND IsClosed = 0
          AND EntryTime >= @todayStart
          AND (@userId = 0 OR UserId = @userId)
        GROUP BY Category
    ),
    AllCats AS (
        SELECT Category FROM ClosedAgg UNION SELECT Category FROM OpenToday
    )
    SELECT
        a.Category,
        ISNULL(c.ClosedCount, 0) + ISNULL(o.NewEntries, 0) AS Entries,
        ISNULL(c.Wins, 0) AS Wins,
        ISNULL(c.Losses, 0) AS Losses,
        ISNULL(c.TotalPnL, 0) AS TotalPnL
    FROM AllCats a
    LEFT JOIN ClosedAgg c ON a.Category = c.Category
    LEFT JOIN OpenToday o ON a.Category = o.Category;
END
"@
$cmd = $cn.CreateCommand(); $cmd.CommandText = $sp; [void]$cmd.ExecuteNonQuery()
Write-Host "[OK] sp_GetTodayStatsByCategory v5.20.7 재배포 (ExitTime 기준)"

# === [STEP 2] 결과 비교 ===
$today = [DateTime]::Today.ToString("yyyy-MM-dd HH:mm:ss")
Write-Host ""
Write-Host "=== [수정 SP 결과] (오늘 KST 자정 이후 청산) ==="
$cmd2 = $cn.CreateCommand()
$cmd2.CommandText = "EXEC dbo.sp_GetTodayStatsByCategory @todayStart=@t, @userId=0"
[void]$cmd2.Parameters.AddWithValue("@t", [DateTime]::Today)
$rdr = $cmd2.ExecuteReader()
$tbl = New-Object System.Data.DataTable; $tbl.Load($rdr)
$tbl | Format-Table -AutoSize | Out-String -Width 200 | Write-Host

# === [STEP 3] PnL=0 인 외부 청산 행 보고 (수동 백필 후보) ===
Write-Host ""
Write-Host "=== [PnL=0 외부 청산 후보 - 24h] ==="
$cmd3 = $cn.CreateCommand()
$cmd3.CommandText = @"
SELECT Id, Symbol, Category, EntryTime, ExitTime, PnL, ExitReason
FROM dbo.TradeHistory
WHERE IsClosed = 1 AND PnL = 0
  AND ExitReason LIKE '%EXTERNAL%'
  AND ExitTime >= DATEADD(HOUR, -24, SYSDATETIME())
ORDER BY ExitTime DESC
"@
$rdr3 = $cmd3.ExecuteReader()
$tbl3 = New-Object System.Data.DataTable; $tbl3.Load($rdr3)
$tbl3 | Format-Table -AutoSize | Out-String -Width 200 | Write-Host
Write-Host ("  -> {0} 건. 봇 재시작 시 v5.20.7 코드가 자동으로 Binance Income API에서 실제 PnL 백필 (단, 새로 청산된 것만)" -f $tbl3.Rows.Count)

$cn.Close()
