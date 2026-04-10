param(
    [string]$PivotTime = "2026-03-18 18:42:20",
    [int]$BeforeMinutes = 60,
    [string]$OutputFile = "compare-before-after-report.txt"
)

function Get-DecryptedConnectionString {
    $app = Get-Content "appsettings.json" -Raw | ConvertFrom-Json
    $encryptedConnectionString = $app.ConnectionStrings.DefaultConnection
    $isEncrypted = [bool]$app.ConnectionStrings.IsEncrypted
    if (-not $isEncrypted) { return $encryptedConnectionString }
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

$cs = Get-DecryptedConnectionString
$conn = New-Object System.Data.SqlClient.SqlConnection $cs
$conn.Open()

# 공통 시간 파라미터
$pivotDt      = [datetime]$PivotTime
$beforeFromDt = $pivotDt.AddMinutes(-$BeforeMinutes)
$nowStr       = (Get-Date).ToString("yyyy-MM-dd HH:mm:ss")

function Invoke-ScalarQuery($conn, $sql, $timeout=300) {
    $cmd = $conn.CreateCommand()
    $cmd.CommandTimeout = $timeout
    $cmd.CommandText = $sql
    return $cmd.ExecuteScalar()
}
function Invoke-TableQuery($conn, $sql, $timeout=300) {
    $cmd = $conn.CreateCommand()
    $cmd.CommandTimeout = $timeout
    $cmd.CommandText = $sql
    $adapter = New-Object System.Data.SqlClient.SqlDataAdapter $cmd
    $dt = New-Object System.Data.DataTable
    [void]$adapter.Fill($dt)
    return $dt
}

$beforeFrom = $beforeFromDt.ToString("yyyy-MM-dd HH:mm:ss")
$pivot      = $pivotDt.ToString("yyyy-MM-dd HH:mm:ss")

# [1] BEFORE summary
$sqlBefore = @"
SELECT
  COUNT(*) AS TotalLogs,
  SUM(CASE WHEN UNICODE(SUBSTRING(Message,1,1))=9940 THEN 1 ELSE 0 END) AS BlockTotal,
  SUM(CASE WHEN UNICODE(SUBSTRING(Message,1,1))=9940 AND Message LIKE '%RSI=%' THEN 1 ELSE 0 END) AS RsiBlocks,
  SUM(CASE WHEN UNICODE(SUBSTRING(Message,1,1))=9940 AND Message LIKE '%RR=%'  THEN 1 ELSE 0 END) AS RrBlocks,
  SUM(CASE WHEN UNICODE(SUBSTRING(Message,1,1))=9940 AND Message LIKE '%SLOT%' THEN 1 ELSE 0 END) AS SlotBlocks
FROM LiveLogs WITH (INDEX(IX_LiveLogs_Timestamp))
WHERE [Timestamp] >= '$beforeFrom' AND [Timestamp] < '$pivot'
"@

# [2] AFTER summary
$sqlAfter = @"
SELECT
  COUNT(*) AS TotalLogs,
  SUM(CASE WHEN UNICODE(SUBSTRING(Message,1,1))=9940 THEN 1 ELSE 0 END) AS BlockTotal,
  SUM(CASE WHEN UNICODE(SUBSTRING(Message,1,1))=9940 AND Message LIKE '%RSI=%' THEN 1 ELSE 0 END) AS RsiBlocks,
  SUM(CASE WHEN UNICODE(SUBSTRING(Message,1,1))=9940 AND Message LIKE '%RR=%'  THEN 1 ELSE 0 END) AS RrBlocks,
  SUM(CASE WHEN UNICODE(SUBSTRING(Message,1,1))=9940 AND Message LIKE '%SLOT%' THEN 1 ELSE 0 END) AS SlotBlocks,
  DATEDIFF(MINUTE,'$pivot',GETDATE()) AS DurMin
FROM LiveLogs WITH (INDEX(IX_LiveLogs_Timestamp))
WHERE [Timestamp] >= '$pivot' AND [Timestamp] <= '$nowStr'
"@

# [3] RSI block symbols AFTER
$sqlSymbols = @"
SELECT Symbol, COUNT(*) AS Cnt
FROM LiveLogs WITH (INDEX(IX_LiveLogs_Timestamp))
WHERE [Timestamp] >= '$pivot'
  AND UNICODE(SUBSTRING(Message,1,1))=9940
  AND Message LIKE '%RSI=%'
GROUP BY Symbol
ORDER BY Cnt DESC
"@

# [4] Hourly block rate AFTER
$sqlHourly = @"
SELECT DATEPART(HOUR,[Timestamp]) AS hr,
  COUNT(*) AS TotalLogs_hr,
  SUM(CASE WHEN UNICODE(SUBSTRING(Message,1,1))=9940 THEN 1 ELSE 0 END) AS Blocks_hr
FROM LiveLogs WITH (INDEX(IX_LiveLogs_Timestamp))
WHERE [Timestamp] >= '$pivot'
GROUP BY DATEPART(HOUR,[Timestamp])
ORDER BY hr
"@

Write-Host "Query 1/4: BEFORE summary ($beforeFrom ~ $pivot)..."
$dtBefore = Invoke-TableQuery $conn $sqlBefore
Write-Host "Query 2/4: AFTER summary ($pivot ~ $nowStr)..."
$dtAfter  = Invoke-TableQuery $conn $sqlAfter
Write-Host "Query 3/4: RSI symbols..."
$dtSymbols = Invoke-TableQuery $conn $sqlSymbols
Write-Host "Query 4/4: Hourly..."
$dtHourly  = Invoke-TableQuery $conn $sqlHourly
$conn.Close()

$lines = New-Object System.Collections.Generic.List[string]
$lines.Add("=== BEFORE vs AFTER Block Rate Comparison ===")
$lines.Add("Pivot: $PivotTime")
$lines.Add("")
$lines.Add("Phase`tTotalLogs`tBlockTotal`tRsiBlocks`tRrBlocks`tSlotBlocks`tDurMin`tBlocks/min`tRsi/min`tRr/min")

# BEFORE 행
$rb = $dtBefore.Rows[0]
$totalB = [int]$rb.TotalLogs; $blockB = [int]$rb.BlockTotal
$rsiB   = [int]$rb.RsiBlocks; $rrB = [int]$rb.RrBlocks; $slotB = [int]$rb.SlotBlocks
$bpmB   = if ($BeforeMinutes -gt 0) { [math]::Round($blockB/$BeforeMinutes,3) } else { 0 }
$rpmB   = if ($BeforeMinutes -gt 0) { [math]::Round($rsiB/$BeforeMinutes,3) } else { 0 }
$rrpmB  = if ($BeforeMinutes -gt 0) { [math]::Round($rrB/$BeforeMinutes,3) } else { 0 }
$lines.Add("BEFORE`t$totalB`t$blockB`t$rsiB`t$rrB`t$slotB`t$BeforeMinutes`t$bpmB`t$rpmB`t$rrpmB")

# AFTER 행
$ra = $dtAfter.Rows[0]
$totalA = [int]$ra.TotalLogs; $blockA = [int]$ra.BlockTotal
$rsiA   = [int]$ra.RsiBlocks; $rrA = [int]$ra.RrBlocks; $slotA = [int]$ra.SlotBlocks
$durA   = [int]$ra.DurMin
if ($durA -le 0) { $durA = 1 }
$bpmA  = [math]::Round($blockA/$durA,3)
$rpmA  = [math]::Round($rsiA/$durA,3)
$rrpmA = [math]::Round($rrA/$durA,3)
$lines.Add("AFTER`t$totalA`t$blockA`t$rsiA`t$rrA`t$slotA`t$durA`t$bpmA`t$rpmA`t$rrpmA")

# 변화율 계산
$lines.Add("")
$lines.Add("=== Change (BEFORE → AFTER) ===")
function Pct($before,$after) { if ($before -gt 0) { [math]::Round(100.0*($after-$before)/$before,1) } else { "N/A" } }
$lines.Add("Blocks/min: $bpmB → $bpmA  (" + (Pct $bpmB $bpmA) + "%)")
$lines.Add("RSI/min   : $rpmB → $rpmA  (" + (Pct $rpmB $rpmA) + "%)")
$lines.Add("RR/min    : $rrpmB → $rrpmA  (" + (Pct $rrpmB $rrpmA) + "%)")
$lines.Add("Slot/min  : " + [math]::Round($slotB/$BeforeMinutes,3) + " → " + [math]::Round($slotA/$durA,3))

foreach ($_ in @()) { # dummy, replaced by direct lines above
}

$lines.Add("")
$lines.Add("=== RSI Block Symbols (AFTER) ===")
foreach ($row in $dtSymbols.Rows) {
    $lines.Add("$($row.Symbol)`t$($row.Cnt)")
}

$lines.Add("")
$lines.Add("=== Hourly Block Rate (AFTER) ===")
foreach ($row in $dtHourly.Rows) {
    $hr      = [int]$row.hr
    $logsHr  = [int]$row.TotalLogs_hr
    $blkHr   = [int]$row.Blocks_hr
    $blkRate = if ($logsHr -gt 0) { [math]::Round(100.0 * $blkHr / $logsHr, 2) } else { 0 }
    $lines.Add("hr=$hr`tLogs=$logsHr`tBlocks=$blkHr`tBlkRate=${blkRate}%")
}

$outPath = Join-Path $PSScriptRoot $OutputFile
Set-Content -Path $outPath -Value $lines -Encoding UTF8
Write-Host "Saved: $outPath"
Get-Content $outPath
