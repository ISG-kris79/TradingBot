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

function Run($sql, $title) {
    Write-Host ""; Write-Host "=== $title ==="
    $cmd = $cn.CreateCommand(); $cmd.CommandText = $sql; $cmd.CommandTimeout = 30
    $rdr = $cmd.ExecuteReader()
    $tbl = New-Object System.Data.DataTable; $tbl.Load($rdr)
    $tbl | Format-Table -AutoSize -Wrap | Out-String -Width 250 | Write-Host
}

# Last 30 min logs containing ml/train/error
$sql1 = "SELECT TOP 30 CreatedAt, LogLevel, Source, Message FROM dbo.Bot_Log WHERE CreatedAt >= DATEADD(MINUTE, -60, SYSDATETIME()) AND (Message LIKE N'%ML%' OR Message LIKE N'%train%' OR Message LIKE N'%error%' OR Message LIKE N'%??%' OR Message LIKE N'%FAIL%' OR Message LIKE N'%ERROR%') ORDER BY CreatedAt DESC"
Run $sql1 "ML/Train/Error logs - last 60min"

# Recent AI training runs
$sql2 = "SELECT TOP 10 RunId, Variant, StartedAt, FinishedAt, Status, ModelPath, LossOrError FROM dbo.AiTrainingRun ORDER BY StartedAt DESC"
Run $sql2 "AiTrainingRun recent"

$cn.Close()
