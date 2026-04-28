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

function ListCols($table) {
    Write-Host ""; Write-Host "=== $table columns ==="
    $cmd = $cn.CreateCommand()
    $cmd.CommandText = "SELECT TOP 1 * FROM $table"
    $rdr = $cmd.ExecuteReader()
    for ($i = 0; $i -lt $rdr.FieldCount; $i++) {
        Write-Host ("  [{0,2}] {1} ({2})" -f $i, $rdr.GetName($i), $rdr.GetFieldType($i).Name)
    }
    $rdr.Close()
}

ListCols "dbo.Bot_Log"
ListCols "dbo.AiTrainingRun"

# Now run actual error log query with discovered columns (try common variants)
Write-Host ""
Write-Host "=== Recent ML/train/error logs (best-effort) ==="
$try = @(
  "SELECT TOP 30 * FROM dbo.Bot_Log WHERE Message LIKE '%ML%' OR Message LIKE '%train%' OR Message LIKE '%error%' ORDER BY 1 DESC",
  "SELECT TOP 30 * FROM dbo.Bot_Log ORDER BY 1 DESC"
)
foreach ($s in $try) {
    try {
        $cmd = $cn.CreateCommand(); $cmd.CommandText = $s; $cmd.CommandTimeout = 30
        $rdr = $cmd.ExecuteReader()
        $tbl = New-Object System.Data.DataTable; $tbl.Load($rdr)
        $tbl | Select-Object -First 15 | Format-Table -AutoSize | Out-String -Width 250 | Write-Host
        break
    } catch { Write-Host "  failed: $($_.Exception.Message)" }
}

$cn.Close()
