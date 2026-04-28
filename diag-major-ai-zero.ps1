$ErrorActionPreference = "Stop"
[Console]::OutputEncoding = [Text.Encoding]::UTF8
$AesKey = [byte[]](0x43,0x6F,0x69,0x6E,0x46,0x46,0x2D,0x54,0x72,0x61,0x64,0x69,0x6E,0x67,0x42,0x6F,0x74,0x2D,0x41,0x45,0x53,0x32,0x35,0x36,0x2D,0x4B,0x65,0x79,0x2D,0x33,0x32,0x42)
function AesDecrypt($enc) {
    $f = [Convert]::FromBase64String($enc)
    $a = [System.Security.Cryptography.Aes]::Create(); $a.Key = $AesKey
    $iv = New-Object byte[] $a.IV.Length
    $c = New-Object byte[] ($f.Length - $a.IV.Length)
    [Buffer]::BlockCopy($f, 0, $iv, 0, $a.IV.Length)
    [Buffer]::BlockCopy($f, $a.IV.Length, $c, 0, $c.Length)
    $a.IV = $iv; $d = $a.CreateDecryptor($a.Key, $a.IV)
    $s = [Text.Encoding]::UTF8.GetString($d.TransformFinalBlock($c, 0, $c.Length))
    $a.Dispose(); $d.Dispose(); return $s
}
$json = Get-Content "$PSScriptRoot\appsettings.json" -Raw | ConvertFrom-Json
$cn = New-Object System.Data.SqlClient.SqlConnection (AesDecrypt $json.ConnectionStrings.DefaultConnection)
$cn.Open()
function Run($sql, $title) {
    Write-Host ""; Write-Host "=== $title ==="
    $cmd = $cn.CreateCommand(); $cmd.CommandText = $sql; $cmd.CommandTimeout = 60
    $rdr = $cmd.ExecuteReader()
    $tbl = New-Object System.Data.DataTable; $tbl.Load($rdr)
    $tbl | Format-Table -AutoSize | Out-String -Width 250 | Write-Host
}

# 1. Model files on disk
$modelDir = Join-Path $env:LOCALAPPDATA "TradingBot\Models"
Write-Host ""
Write-Host "=== S1: Model files in $modelDir ==="
if (Test-Path $modelDir) {
    Get-ChildItem $modelDir -File | Select-Object Name, Length, LastWriteTime | Format-Table -AutoSize
} else {
    Write-Host "  ❌ Model directory NOT FOUND"
}

# 2. Bot_Log MAJOR 진입 시도 — ML_Conf / TF_Conf 분포 (12h)
$sql2 = "SELECT TOP 20 EventTime, Symbol, CoinType, ML_Conf, TF_Conf, TrendScore, Allowed, Reason FROM dbo.Bot_Log WHERE EventTime >= DATEADD(HOUR, -12, SYSDATETIME()) AND CoinType LIKE '%Major%' ORDER BY EventTime DESC"
Run $sql2 "S2: Major coin entry attempts last 12h - ML_Conf / TF_Conf"

# 3. ML_Conf 분포 12h (전체)
$sql3 = "SELECT CoinType, COUNT(*) AS n, AVG(ML_Conf) AS avg_ml, AVG(TF_Conf) AS avg_tf, MAX(ML_Conf) AS max_ml, MAX(TF_Conf) AS max_tf FROM dbo.Bot_Log WHERE EventTime >= DATEADD(HOUR, -12, SYSDATETIME()) GROUP BY CoinType"
Run $sql3 "S3: ML/TF score distribution by CoinType last 12h"

# 4. AiTrainingRun 최근 (학습 성공/실패)
$sql4 = "SELECT TOP 10 RunId, Variant, StartedAt, FinishedAt, Status, ModelPath, LossOrError FROM dbo.AiTrainingRun ORDER BY StartedAt DESC"
try {
    Run $sql4 "S4: AiTrainingRun history (last 10)"
} catch {
    Write-Host "  S4 fail: $($_.Exception.Message)"
}

$cn.Close()
