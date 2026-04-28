function Get-CS {
    $json = Get-Content "$PSScriptRoot\appsettings.json" -Raw | ConvertFrom-Json
    $enc = $json.ConnectionStrings.DefaultConnection
    $k = [byte[]](0x43, 0x6F, 0x69, 0x6E, 0x46, 0x46, 0x2D, 0x54,
                  0x72, 0x61, 0x64, 0x69, 0x6E, 0x67, 0x42, 0x6F,
                  0x74, 0x2D, 0x41, 0x45, 0x53, 0x32, 0x35, 0x36,
                  0x2D, 0x4B, 0x65, 0x79, 0x2D, 0x33, 0x32, 0x42)
    $f = [Convert]::FromBase64String($enc)
    $a = [System.Security.Cryptography.Aes]::Create(); $a.Key = $k
    $iv = New-Object byte[] $a.IV.Length
    $c = New-Object byte[] ($f.Length - $a.IV.Length)
    [Buffer]::BlockCopy($f, 0, $iv, 0, $a.IV.Length)
    [Buffer]::BlockCopy($f, $a.IV.Length, $c, 0, $c.Length)
    $a.IV = $iv
    $d = $a.CreateDecryptor($a.Key, $a.IV)
    $s = [System.Text.Encoding]::UTF8.GetString($d.TransformFinalBlock($c, 0, $c.Length))
    $a.Dispose(); $d.Dispose()
    return $s
}

$cs = Get-CS
$cn = New-Object System.Data.SqlClient.SqlConnection $cs
$cn.Open()

# Step 1: Add column
$cm1 = $cn.CreateCommand()
$cm1.CommandText = "IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('dbo.TradeHistory') AND name = 'Category') BEGIN ALTER TABLE dbo.TradeHistory ADD Category NVARCHAR(10) NULL END"
$cm1.ExecuteNonQuery()
Write-Host "Step 1: Column added" -ForegroundColor Green

# Step 2: Add index
$cm2 = $cn.CreateCommand()
$cm2.CommandText = "IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID('dbo.TradeHistory') AND name = 'IX_TradeHistory_Category_EntryTime') BEGIN CREATE INDEX IX_TradeHistory_Category_EntryTime ON dbo.TradeHistory (Category, EntryTime) WHERE Category IS NOT NULL END"
$cm2.ExecuteNonQuery()
Write-Host "Step 2: Index created" -ForegroundColor Green

$cn.Close()
Write-Host "Done!" -ForegroundColor Green

# Verify
$cn2 = New-Object System.Data.SqlClient.SqlConnection $cs
$cn2.Open()
$cm3 = $cn2.CreateCommand()
$cm3.CommandText = "SELECT COLUMN_NAME FROM INFORMATION_SCHEMA.COLUMNS WHERE TABLE_NAME='TradeHistory' AND COLUMN_NAME='Category'"
$r = $cm3.ExecuteReader()
if ($r.HasRows) { Write-Host "Verified: Category column exists" -ForegroundColor Cyan } else { Write-Host "ERROR: Category column NOT found" -ForegroundColor Red }
$r.Close()
$cn2.Close()
