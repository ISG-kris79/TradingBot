param(
    [int]$LookbackMinutes = 30,
    [string]$OutputFile = "entry-block-30m-report.txt"
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

$connectionString = Get-DecryptedConnectionString
$conn = New-Object System.Data.SqlClient.SqlConnection $connectionString
$conn.Open()

$query = @"
SET NOCOUNT ON;
IF OBJECT_ID('tempdb..#parsed') IS NOT NULL DROP TABLE #parsed;

;WITH src AS (
    SELECT [Timestamp], Category, Symbol, Message,
        CHARINDEX('[', Message) AS firstOpen,
        CHARINDEX(']', Message, CHARINDEX('[', Message) + 1) AS firstClose
    FROM LiveLogs
        WHERE [Timestamp] >= DATEADD(MINUTE,-$LookbackMinutes,GETDATE())
            AND Message LIKE N'%차단%'
            AND (
                        Message LIKE N'%진입 차단%'
                        OR Message LIKE N'%진입 필터%'
                        OR Message LIKE N'%진입 금지%'
                        OR CHARINDEX('[진입][', Message) > 0
                    )
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

SELECT SymbolGroup, ReasonLabel, COUNT(*) AS Cnt
FROM #parsed
GROUP BY SymbolGroup, ReasonLabel
ORDER BY SymbolGroup, Cnt DESC;

SELECT COUNT(*) AS TotalEntryBlocks30m FROM #parsed;
"@

$cmd = $conn.CreateCommand()
$cmd.CommandText = $query
$adapter = New-Object System.Data.SqlClient.SqlDataAdapter $cmd
$ds = New-Object System.Data.DataSet
[void]$adapter.Fill($ds)

$outPath = Join-Path $PSScriptRoot $OutputFile
$lines = New-Object System.Collections.Generic.List[string]

$lines.Add("=== Top reasons (${LookbackMinutes}m) ===")
foreach ($row in $ds.Tables[0].Rows) {
    $lines.Add("$($row.ReasonLabel)`t$($row.Cnt)")
}

$lines.Add("")
$lines.Add("=== By symbol group ===")
foreach ($row in $ds.Tables[1].Rows) {
    $lines.Add("$($row.SymbolGroup)`t$($row.ReasonLabel)`t$($row.Cnt)")
}

$lines.Add("")
$lines.Add("=== Total ===")
foreach ($row in $ds.Tables[2].Rows) {
    $lines.Add("TotalEntryBlocks30m`t$($row.TotalEntryBlocks30m)")
}

Set-Content -Path $outPath -Value $lines -Encoding UTF8
Write-Host "Saved: $outPath"

$conn.Close()
