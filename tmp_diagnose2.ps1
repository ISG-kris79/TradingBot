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

-- 1) RAVE 오늘 블랙리스트/차단/LONG 관련 로그
SELECT TOP 80
    CONVERT(varchar(19), [Timestamp], 120) AS Ts,
    Category, Symbol, Message
FROM LiveLogs WITH (INDEX(IX_LiveLogs_Timestamp))
WHERE [Timestamp] >= '2026-04-10 00:00:00'
  AND (Symbol = 'RAVEUSDT' OR Message LIKE '%RAVEUSDT%')
  AND (    Message LIKE '%LONG%'
        OR Message LIKE '%블랙%'
        OR Message LIKE '%blacklist%'
        OR Message LIKE '%차단%'
        OR Message LIKE '%BLOCK%'
        OR Message LIKE '%block%'
        OR Message LIKE '%진입%'
        OR Message LIKE '%방향=LONG%'
        OR Message LIKE '%START%'
        OR Message LIKE '%슬롯%'
        OR Message LIKE '%warmup%'
  )
ORDER BY [Timestamp] DESC;

-- 2) ZEREBRO/AGT 9:30 근처 AI gate 로그 (오전 9:00~10:30)
SELECT TOP 60
    CONVERT(varchar(19), [Timestamp], 120) AS Ts,
    Category, Symbol, Message
FROM LiveLogs WITH (INDEX(IX_LiveLogs_Timestamp))
WHERE [Timestamp] >= '2026-04-10 00:00:00'
  AND [Timestamp] <= '2026-04-10 10:30:00'
  AND (Symbol IN ('ZEREBROUSDT','AGTUSDT')
       OR Message LIKE '%ZEREBROUSDT%'
       OR Message LIKE '%AGTUSDT%')
ORDER BY [Timestamp] DESC;

-- 3) 오늘 전체 진입 START 로그 (실제 진입 시도)
SELECT TOP 30
    CONVERT(varchar(19), [Timestamp], 120) AS Ts,
    Category, Symbol, Message
FROM LiveLogs WITH (INDEX(IX_LiveLogs_Timestamp))
WHERE [Timestamp] >= '2026-04-10 00:00:00'
  AND (Message LIKE '%[START]%' OR Message LIKE '%전략=MAJOR_MEME%' OR Message LIKE '%[진입][START]%')
  AND (Symbol IN ('RAVEUSDT','ZEREBROUSDT','AGTUSDT')
       OR Message LIKE '%RAVEUSDT%'
       OR Message LIKE '%ZEREBROUSDT%'
       OR Message LIKE '%AGTUSDT%')
ORDER BY [Timestamp] DESC;
"@

$cmd = $conn.CreateCommand()
$cmd.CommandTimeout = 120
$cmd.CommandText = $sql
$adapter = New-Object System.Data.SqlClient.SqlDataAdapter $cmd
$ds = New-Object System.Data.DataSet
[void]$adapter.Fill($ds)
$conn.Close()

$out = New-Object System.Collections.Generic.List[string]
$sections = @('=== RAVE 오늘 LONG/블랙/차단 로그 ===',
               '=== ZEREBRO/AGT 오전 9:00~10:30 로그 ===',
               '=== 오늘 진입 START 로그 ===')

for ($i = 0; $i -lt [Math]::Min($ds.Tables.Count, $sections.Count); $i++) {
    $out.Add('')
    $out.Add($sections[$i])
    $tbl = $ds.Tables[$i]
    if ($tbl.Rows.Count -eq 0) {
        $out.Add('  (결과 없음)')
    }
    foreach ($row in $tbl.Rows) {
        $line = ($row.ItemArray | ForEach-Object { "$_" }) -join "`t"
        $out.Add($line)
    }
}

$out | Set-Content "tmp_diagnose2.txt" -Encoding UTF8
Write-Host "완료 → tmp_diagnose2.txt ($($out.Count) lines)"
