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

-- 1) RAVEUSDT 진입 경로 로그 (최근 6시간)
SELECT TOP 120
    CONVERT(varchar(19), [Timestamp], 120) AS Ts,
    Category, Symbol, Message
FROM LiveLogs WITH (INDEX(IX_LiveLogs_Timestamp))
WHERE [Timestamp] >= DATEADD(HOUR,-6,GETDATE())
  AND (Symbol = 'RAVEUSDT' OR Message LIKE '%RAVEUSDT%')
  AND (    Category IN ('진입','SIGNAL','ENTRY','VOLUME','AI','START')
        OR Message LIKE '%후보%'
        OR Message LIKE '%진입%'
        OR Message LIKE '%LONG%'
        OR Message LIKE '%WAIT%'
        OR Message LIKE '%신호%'
        OR Message LIKE '%BYPASS%'
        OR Message LIKE '%lowVolume%'
  )
ORDER BY [Timestamp] DESC;

-- 2) ZEREBROUSDT 진입 경로 로그 (최근 6시간)
SELECT TOP 120
    CONVERT(varchar(19), [Timestamp], 120) AS Ts,
    Category, Symbol, Message
FROM LiveLogs WITH (INDEX(IX_LiveLogs_Timestamp))
WHERE [Timestamp] >= DATEADD(HOUR,-6,GETDATE())
  AND (Symbol = 'ZEREBROUSDT' OR Message LIKE '%ZEREBROUSDT%')
ORDER BY [Timestamp] DESC;

-- 3) AGTUSDT 진입 경로 로그 (최근 6시간)
SELECT TOP 120
    CONVERT(varchar(19), [Timestamp], 120) AS Ts,
    Category, Symbol, Message
FROM LiveLogs WITH (INDEX(IX_LiveLogs_Timestamp))
WHERE [Timestamp] >= DATEADD(HOUR,-6,GETDATE())
  AND (Symbol = 'AGTUSDT' OR Message LIKE '%AGTUSDT%')
ORDER BY [Timestamp] DESC;

-- 4) 각 심볼 최근 거래 내역
SELECT TOP 10 Symbol, EntryTime, ExitTime, Side, PnLPercent, ExitReason
FROM TradeHistory
WHERE Symbol IN ('RAVEUSDT','ZEREBROUSDT','AGTUSDT')
ORDER BY EntryTime DESC;

-- 5) AI 승인 목록 현재 상태 확인 (LiveLogs 최근 AI_GATE/START 로그)
SELECT TOP 40
    CONVERT(varchar(19), [Timestamp], 120) AS Ts,
    Category, Symbol, Message
FROM LiveLogs WITH (INDEX(IX_LiveLogs_Timestamp))
WHERE [Timestamp] >= DATEADD(MINUTE,-30,GETDATE())
  AND (Symbol IN ('ZEREBROUSDT','AGTUSDT')
       OR Message LIKE '%ZEREBRO%'
       OR Message LIKE '%AGT%')
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

$sections = @('=== RAVEUSDT 진입 경로 (최근 6h) ===',
               '=== ZEREBROUSDT 로그 (최근 6h) ===',
               '=== AGTUSDT 로그 (최근 6h) ===',
               '=== 최근 거래 내역 ===',
               '=== AI 승인 최근 30분 (ZEREBRO/AGT) ===')

for ($i = 0; $i -lt $ds.Tables.Count; $i++) {
    $out.Add('')
    $out.Add($sections[$i])
    $tbl = $ds.Tables[$i]
    foreach ($row in $tbl.Rows) {
        $line = ($row.ItemArray | ForEach-Object { "$_" }) -join "`t"
        $out.Add($line)
    }
}

$out | Set-Content "tmp_multi_diagnose.txt" -Encoding UTF8
Write-Host "완료 → tmp_multi_diagnose.txt ($($out.Count) lines)"
