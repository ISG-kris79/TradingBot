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
SELECT TOP 200 CONVERT(varchar(19), [Timestamp], 120) AS Ts, Category, Symbol, Message
FROM LiveLogs WITH (INDEX(IX_LiveLogs_Timestamp))
WHERE [Timestamp] >= DATEADD(HOUR,-3,GETDATE())
  AND Symbol = 'BLURUSDT'
  AND (
       Message LIKE '%[ENTRY]%' OR
       Message LIKE '%[진입]%' OR
       Message LIKE '%AI_GATE%' OR
       Message LIKE '%CANDLE%' OR
       Message LIKE '%차단%' OR
       Message LIKE '%BLOCK%' OR
       Message LIKE '%PASS%' OR
       Message LIKE '%ORDER%' OR
       Message LIKE '%SCOUT%' OR
       Message LIKE '%VOLUME%'
  )
ORDER BY [Timestamp] DESC;
"@
$cmd = $conn.CreateCommand()
$cmd.CommandTimeout = 120
$cmd.CommandText = $sql
$adapter = New-Object System.Data.SqlClient.SqlDataAdapter $cmd
$dt = New-Object System.Data.DataTable
[void]$adapter.Fill($dt)
$conn.Close()

$out = New-Object System.Collections.Generic.List[string]
foreach ($row in $dt.Rows) {
    $out.Add("$($row.Ts)`t$($row.Category)`t$($row.Symbol)`t$($row.Message)")
}
Set-Content -Path (Join-Path $PSScriptRoot 'tmp_blur_entry_path.txt') -Value $out -Encoding UTF8
Write-Host 'Saved: tmp_blur_entry_path.txt'
