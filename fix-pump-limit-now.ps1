$ErrorActionPreference = "Stop"
[Console]::OutputEncoding = [Text.Encoding]::UTF8
$AesKey = [byte[]](0x43, 0x6F, 0x69, 0x6E, 0x46, 0x46, 0x2D, 0x54, 0x72, 0x61, 0x64, 0x69, 0x6E, 0x67, 0x42, 0x6F, 0x74, 0x2D, 0x41, 0x45, 0x53, 0x32, 0x35, 0x36, 0x2D, 0x4B, 0x65, 0x79, 0x2D, 0x33, 0x32, 0x42)
function AesDecrypt($enc) {
    if ([string]::IsNullOrEmpty($enc)) { return "" }
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
$connStr = AesDecrypt $json.ConnectionStrings.DefaultConnection
$cn = New-Object System.Data.SqlClient.SqlConnection $connStr
$cn.Open()

Write-Host "[STEP 1] Id=10 행 MaxDailyEntries NULL → 500 업데이트"
$cm = $cn.CreateCommand()
$cm.CommandText = "UPDATE GeneralSettings SET MaxDailyEntries=500, UpdatedAt=GETUTCDATE() WHERE Id=10"
$n = $cm.ExecuteNonQuery()
Write-Host ("  업데이트된 행: {0}" -f $n) -ForegroundColor Green

Write-Host ""
Write-Host "[STEP 2] 두 행 모두 500 으로 통일 (안전장치)"
$cm2 = $cn.CreateCommand()
$cm2.CommandText = "UPDATE GeneralSettings SET MaxDailyEntries=500, UpdatedAt=GETUTCDATE() WHERE MaxDailyEntries IS NULL OR MaxDailyEntries < 100"
$n2 = $cm2.ExecuteNonQuery()
Write-Host ("  통일된 행: {0}" -f $n2) -ForegroundColor Green

Write-Host ""
Write-Host "[STEP 3] 검증 - 현재 설정값"
$cm3 = $cn.CreateCommand()
$cm3.CommandText = "SELECT Id, MaxDailyEntries, MaxMajorSlots, MaxPumpSlots, EnableMajorTrading, UpdatedAt FROM GeneralSettings"
$rd = $cm3.ExecuteReader()
while ($rd.Read()) {
    Write-Host ("  Id={0} | MaxDailyEntries={1} | MaxMajorSlots={2} | MaxPumpSlots={3} | EnableMajor={4} | Updated={5}" -f $rd["Id"], $rd["MaxDailyEntries"], $rd["MaxMajorSlots"], $rd["MaxPumpSlots"], $rd["EnableMajorTrading"], $rd["UpdatedAt"])
}
$rd.Close()
$cn.Close()

Write-Host ""
Write-Host "==========================================" -ForegroundColor Cyan
Write-Host "✅ DB 값 수정 완료" -ForegroundColor Green
Write-Host "⚠️ 봇 재시작 필요 — _settings 캐시 갱신을 위해" -ForegroundColor Yellow
Write-Host "   재시작 시 새 limit 500 적용됨" -ForegroundColor Yellow
Write-Host "==========================================" -ForegroundColor Cyan
