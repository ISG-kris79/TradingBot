. "$PSScriptRoot\check-db.ps1" > $null 2>&1

function Get-DecryptedConnectionString {
    $json = Get-Content "$PSScriptRoot\appsettings.json" -Raw | ConvertFrom-Json
    $enc = $json.ConnectionStrings.DefaultConnection
    if (-not $json.ConnectionStrings.IsEncrypted) { return $enc }
    $aesKey = [byte[]](0x43, 0x6F, 0x69, 0x6E, 0x46, 0x46, 0x2D, 0x54,
                       0x72, 0x61, 0x64, 0x69, 0x6E, 0x67, 0x42, 0x6F,
                       0x74, 0x2D, 0x41, 0x45, 0x53, 0x32, 0x35, 0x36,
                       0x2D, 0x4B, 0x65, 0x79, 0x2D, 0x33, 0x32, 0x42)
    $full = [Convert]::FromBase64String($enc)
    $aes = [System.Security.Cryptography.Aes]::Create()
    $aes.Key = $aesKey
    $ivLen = $aes.IV.Length
    $iv = New-Object byte[] $ivLen
    $cipher = New-Object byte[] ($full.Length - $ivLen)
    [Buffer]::BlockCopy($full, 0, $iv, 0, $ivLen)
    [Buffer]::BlockCopy($full, $ivLen, $cipher, 0, $cipher.Length)
    $aes.IV = $iv
    $dec = $aes.CreateDecryptor($aes.Key, $aes.IV)
    $out = [System.Text.Encoding]::UTF8.GetString($dec.TransformFinalBlock($cipher, 0, $cipher.Length))
    $aes.Dispose(); $dec.Dispose()
    return $out
}

function Q($sql) {
    $cs = Get-DecryptedConnectionString
    $conn = New-Object System.Data.SqlClient.SqlConnection $cs
    $conn.Open()
    $cmd = $conn.CreateCommand()
    $cmd.CommandText = $sql
    $cmd.CommandTimeout = 60
    $a = New-Object System.Data.SqlClient.SqlDataAdapter $cmd
    $ds = New-Object System.Data.DataSet
    [void]$a.Fill($ds)
    $conn.Close()
    return $ds.Tables[0]
}

[Console]::OutputEncoding = [System.Text.Encoding]::UTF8

Write-Host "=== [1] CandleData 스키마 ===" -ForegroundColor Cyan
Q "SELECT COLUMN_NAME, DATA_TYPE FROM INFORMATION_SCHEMA.COLUMNS WHERE TABLE_NAME='CandleData' ORDER BY ORDINAL_POSITION" | Format-Table -AutoSize

Write-Host "=== [2] DB에 있는 로그/신호 테이블 리스트 ===" -ForegroundColor Cyan
Q @"
SELECT TABLE_NAME FROM INFORMATION_SCHEMA.TABLES
WHERE TABLE_NAME LIKE '%Log%' OR TABLE_NAME LIKE '%Signal%'
   OR TABLE_NAME LIKE '%Decision%' OR TABLE_NAME LIKE '%Entry%'
   OR TABLE_NAME LIKE '%Footer%'
ORDER BY TABLE_NAME
"@ | Format-Table -AutoSize

Write-Host "=== [3] XRP 2026-04-12 최근 12시간 캔들 (컬럼명 파악 후) ===" -ForegroundColor Cyan
# 일단 wildcard 조회
Q "SELECT TOP 5 * FROM CandleData WHERE Symbol='XRPUSDT' ORDER BY Id DESC" | Format-Table -AutoSize
