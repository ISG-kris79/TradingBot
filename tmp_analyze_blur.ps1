Set-Location $PSScriptRoot

$app = Get-Content "appsettings.json" -Raw | ConvertFrom-Json
$cs = $app.ConnectionStrings.DefaultConnection

if ([bool]$app.ConnectionStrings.IsEncrypted) {
    $aesKey = [byte[]](0x43,0x6F,0x69,0x6E,0x46,0x46,0x2D,0x54,0x72,0x61,0x64,0x69,0x6E,0x67,0x42,0x6F,0x74,0x2D,0x41,0x45,0x53,0x32,0x35,0x36,0x2D,0x4B,0x65,0x79,0x2D,0x33,0x32,0x42)
    $fullCipher = [Convert]::FromBase64String($cs)
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
    $cs = [System.Text.Encoding]::UTF8.GetString($decryptedBytes)
    $decryptor.Dispose()
    $aes.Dispose()
}

$conn = New-Object System.Data.SqlClient.SqlConnection $cs
$conn.Open()

$sql = @"
SET NOCOUNT ON;

SELECT TOP 80 CONVERT(varchar(19), [Timestamp], 120) AS Ts, Category, Symbol, Message
FROM LiveLogs WITH (INDEX(IX_LiveLogs_Timestamp))
WHERE [Timestamp] >= DATEADD(HOUR,-24,GETDATE())
  AND (Symbol = 'BLURUSDT' OR Message LIKE '%BLURUSDT%')
ORDER BY [Timestamp] DESC;

SELECT TOP 20 Symbol, EntryTime, ExitTime, Side, PnLPercent, ExitReason
FROM TradeHistory
WHERE Symbol = 'BLURUSDT'
ORDER BY EntryTime DESC;
"@

$cmd = $conn.CreateCommand()
$cmd.CommandTimeout = 120
$cmd.CommandText = $sql
$adapter = New-Object System.Data.SqlClient.SqlDataAdapter $cmd
$ds = New-Object System.Data.DataSet
[void]$adapter.Fill($ds)
$conn.Close()

$out = New-Object System.Collections.Generic.List[string]
$out.Add('=== LiveLogs ===')
if ($ds.Tables.Count -gt 0) {
    foreach ($row in $ds.Tables[0].Rows) {
        $out.Add("$($row.Ts)`t$($row.Category)`t$($row.Symbol)`t$($row.Message)")
    }
}
$out.Add('')
$out.Add('=== TradeHistory ===')
if ($ds.Tables.Count -gt 1) {
    foreach ($row in $ds.Tables[1].Rows) {
        $out.Add("$($row.Symbol)`t$($row.EntryTime)`t$($row.ExitTime)`t$($row.Side)`t$($row.PnLPercent)`t$($row.ExitReason)")
    }
}

Set-Content -Path (Join-Path $PSScriptRoot 'tmp_blur_report.txt') -Value $out -Encoding UTF8
Write-Host 'Saved: tmp_blur_report.txt'
