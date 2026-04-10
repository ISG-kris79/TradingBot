param(
    [int]$DurationMinutes = 20,
    [int]$BaselineMinutes = 30,
    [string]$OutputFile = "entry-block-monitor-report.txt"
)

function Get-DecryptedConnectionString {
    $app = Get-Content "appsettings.json" -Raw | ConvertFrom-Json
    $encryptedConnectionString = $app.ConnectionStrings.DefaultConnection
    $isEncrypted = [bool]$app.ConnectionStrings.IsEncrypted

    if (-not $isEncrypted) {
        return $encryptedConnectionString
    }

    $aesKey = [byte[]](0x43,0x6F,0x69,0x6E,0x46,0x46,0x2D,0x54,0x72,0x61,0x64,0x69,0x6E,0x67,0x42,0x6F,0x74,0x2D,0x41,0x45,0x53,0x32,0x35,0x36,0x2D,0x4B,0x65,0x79,0x2D,0x33,0x32,0x42)
    $fullCipher = [Convert]::FromBase64String($encryptedConnectionString)

    $aes = [System.Security.Cryptography.Aes]::Create()
    $aes.Key = $aesKey

    $ivLength = $aes.IV.Length
    $iv = New-Object byte[] $ivLength
    $cipher = New-Object byte[] ($fullCipher.Length - $ivLength)

    [Buffer]::BlockCopy($fullCipher, 0, $iv, 0, $ivLength)
    [Buffer]::BlockCopy($fullCipher, $ivLength, $cipher, 0, $cipher.Length)

    $aes.IV = $iv
    $decryptor = $aes.CreateDecryptor($aes.Key, $aes.IV)
    $decryptedBytes = $decryptor.TransformFinalBlock($cipher, 0, $cipher.Length)
    $decrypted = [System.Text.Encoding]::UTF8.GetString($decryptedBytes)

    $decryptor.Dispose()
    $aes.Dispose()

    return $decrypted
}

function Invoke-BlockStats {
    param(
        [System.Data.SqlClient.SqlConnection]$Connection,
        [datetime]$FromTime,
        [datetime]$ToTime
    )

    $fromLiteral = $FromTime.ToString("yyyy-MM-dd HH:mm:ss")
    $toLiteral = $ToTime.ToString("yyyy-MM-dd HH:mm:ss")

    $sql = @"
SET NOCOUNT ON;
IF OBJECT_ID('tempdb..#parsed') IS NOT NULL DROP TABLE #parsed;

;WITH src AS (
    SELECT [Timestamp], Category, Symbol, Message,
           CHARINDEX('[', Message) AS firstOpen,
           CHARINDEX(']', Message, CHARINDEX('[', Message) + 1) AS firstClose
    FROM LiveLogs
    WHERE [Timestamp] >= '$fromLiteral'
      AND [Timestamp] < '$toLiteral'
      AND (
            Message LIKE N'%차단%'
            OR Message LIKE N'%BLOCK%'
            OR Message LIKE N'%포화%'
          )
      AND Category IN ('PUMP','MAJOR','ENTRY','SCAN','INFO')
)
SELECT [Timestamp], Category, Symbol, Message,
    CASE WHEN firstOpen > 0 AND firstClose > firstOpen
         THEN SUBSTRING(Message, firstOpen + 1, firstClose - firstOpen - 1)
         ELSE N'기타'
    END AS ReasonLabel,
    CASE
      WHEN Symbol IN ('BTCUSDT','ETHUSDT','SOLUSDT','XRPUSDT') THEN 'MAJOR'
      WHEN Message LIKE N'%PUMP%' OR Category='PUMP' THEN 'PUMP'
      ELSE 'NORMAL'
    END AS SymbolGroup
INTO #parsed
FROM src;

SELECT TOP 10 ReasonLabel, COUNT(*) AS Cnt
FROM #parsed
GROUP BY ReasonLabel
ORDER BY Cnt DESC;

SELECT SymbolGroup, COUNT(*) AS Cnt
FROM #parsed
GROUP BY SymbolGroup
ORDER BY Cnt DESC;

SELECT
    COUNT(*) AS TotalBlocks,
    SUM(CASE WHEN SymbolGroup='PUMP' THEN 1 ELSE 0 END) AS PumpBlocks,
    SUM(CASE WHEN ReasonLabel=N'RSI 과확장 차단' THEN 1 ELSE 0 END) AS RsiBlocks
FROM #parsed;
"@

    $cmd = $Connection.CreateCommand()
    $cmd.CommandText = $sql
    $adapter = New-Object System.Data.SqlClient.SqlDataAdapter $cmd
    $ds = New-Object System.Data.DataSet
    [void]$adapter.Fill($ds)
    return $ds
}

function Get-IntOrZero {
    param([object]$Value)

    if ($null -eq $Value -or $Value -is [System.DBNull]) {
        return 0
    }

    return [int]$Value
}

$reportLines = New-Object System.Collections.Generic.List[string]
$startTime = Get-Date
$baselineStart = $startTime.AddMinutes(-$BaselineMinutes)
$endTime = $startTime.AddMinutes($DurationMinutes)

$reportLines.Add("=== Monitor Configuration ===")
$reportLines.Add("StartTime`t$($startTime.ToString('yyyy-MM-dd HH:mm:ss'))")
$reportLines.Add("DurationMinutes`t$DurationMinutes")
$reportLines.Add("BaselineMinutes`t$BaselineMinutes")
$reportLines.Add("")

$connectionString = Get-DecryptedConnectionString

# 베이스라인 수집 (연결 열고 즉시 닫기)
$conn1 = New-Object System.Data.SqlClient.SqlConnection $connectionString
$conn1.Open()
$baselineDs = Invoke-BlockStats -Connection $conn1 -FromTime $baselineStart -ToTime $startTime
$conn1.Close()

$reportLines.Add("=== Baseline Window ===")
$reportLines.Add("From`t$($baselineStart.ToString('yyyy-MM-dd HH:mm:ss'))")
$reportLines.Add("To`t$($startTime.ToString('yyyy-MM-dd HH:mm:ss'))")
foreach ($row in $baselineDs.Tables[2].Rows) {
    $bTotal = Get-IntOrZero $row.TotalBlocks
    $bPump = Get-IntOrZero $row.PumpBlocks
    $bRsi = Get-IntOrZero $row.RsiBlocks
    $bRate = if ($BaselineMinutes -gt 0) { [math]::Round($bTotal / $BaselineMinutes, 3) } else { 0 }
    $reportLines.Add("TotalBlocks`t$bTotal")
    $reportLines.Add("PumpBlocks`t$bPump")
    $reportLines.Add("RsiBlocks`t$bRsi")
    $reportLines.Add("BlocksPerMinute`t$bRate")
}
$reportLines.Add("")
$reportLines.Add("TopReasons")
foreach ($row in $baselineDs.Tables[0].Rows) {
    $reportLines.Add("$($row.ReasonLabel)`t$($row.Cnt)")
}
$reportLines.Add("")

Write-Host "[$((Get-Date).ToString('HH:mm:ss'))] monitor start - waiting $DurationMinutes minutes..."
Start-Sleep -Seconds ($DurationMinutes * 60)

$observedEnd = Get-Date
# 관측 수집 (새 연결로 재쿼리)
$conn2 = New-Object System.Data.SqlClient.SqlConnection $connectionString
$conn2.Open()
$observedDs = Invoke-BlockStats -Connection $conn2 -FromTime $startTime -ToTime $observedEnd
$conn2.Close()

$reportLines.Add("=== Observed Window ===")
$reportLines.Add("From`t$($startTime.ToString('yyyy-MM-dd HH:mm:ss'))")
$reportLines.Add("To`t$($observedEnd.ToString('yyyy-MM-dd HH:mm:ss'))")
foreach ($row in $observedDs.Tables[2].Rows) {
    $oTotal = Get-IntOrZero $row.TotalBlocks
    $oPump = Get-IntOrZero $row.PumpBlocks
    $oRsi = Get-IntOrZero $row.RsiBlocks
    $oRate = if ($DurationMinutes -gt 0) { [math]::Round($oTotal / $DurationMinutes, 3) } else { 0 }
    $pumpRatio = if ($oTotal -gt 0) { [math]::Round(100.0 * $oPump / $oTotal, 2) } else { 0 }

    $reportLines.Add("TotalBlocks`t$oTotal")
    $reportLines.Add("PumpBlocks`t$oPump")
    $reportLines.Add("RsiBlocks`t$oRsi")
    $reportLines.Add("PumpRatioPct`t$pumpRatio")
    $reportLines.Add("BlocksPerMinute`t$oRate")

    foreach ($bRow in $baselineDs.Tables[2].Rows) {
        $bTotal2 = [double](Get-IntOrZero $bRow.TotalBlocks)
        $bRate2 = if ($BaselineMinutes -gt 0) { $bTotal2 / $BaselineMinutes } else { 0 }
        $reductionPct = if ($bRate2 -gt 0) { [math]::Round((($bRate2 - $oRate) / $bRate2) * 100.0, 2) } else { 0 }
        $reportLines.Add("RateReductionPct_vsBaseline`t$reductionPct")
    }
}
$reportLines.Add("")
$reportLines.Add("TopReasons")
foreach ($row in $observedDs.Tables[0].Rows) {
    $reportLines.Add("$($row.ReasonLabel)`t$($row.Cnt)")
}
$reportLines.Add("")
$reportLines.Add("BySymbolGroup")
foreach ($row in $observedDs.Tables[1].Rows) {
    $reportLines.Add("$($row.SymbolGroup)`t$($row.Cnt)")
}

$outPath = Join-Path $PSScriptRoot $OutputFile
Set-Content -Path $outPath -Value $reportLines -Encoding UTF8
Write-Host "Saved: $outPath"
