$cs = "Server=localhost;Database=TradingBot;Integrated Security=True;TrustServerCertificate=True"
$conn = New-Object System.Data.SqlClient.SqlConnection($cs)
$conn.Open()

Write-Host "=== v5.10.2 DB 마이그레이션: GeneralSettings 컬럼 추가 ===" -ForegroundColor Cyan

$migrations = @(
    @{ col = "EnableMajorTrading"; sql = "ALTER TABLE dbo.GeneralSettings ADD EnableMajorTrading BIT NOT NULL DEFAULT 1" },
    @{ col = "MaxMajorSlots";      sql = "ALTER TABLE dbo.GeneralSettings ADD MaxMajorSlots INT NOT NULL DEFAULT 4" },
    @{ col = "MaxPumpSlots";       sql = "ALTER TABLE dbo.GeneralSettings ADD MaxPumpSlots INT NOT NULL DEFAULT 3" },
    @{ col = "MaxDailyEntries";    sql = "ALTER TABLE dbo.GeneralSettings ADD MaxDailyEntries INT NOT NULL DEFAULT 60" }
)

foreach ($m in $migrations) {
    $check = "SELECT COUNT(1) FROM sys.columns WHERE object_id = OBJECT_ID('dbo.GeneralSettings') AND name = '$($m.col)'"
    $cm = $conn.CreateCommand(); $cm.CommandText = $check
    $exists = [int]$cm.ExecuteScalar()
    if ($exists -eq 0) {
        $cm2 = $conn.CreateCommand(); $cm2.CommandText = $m.sql
        $cm2.ExecuteNonQuery() | Out-Null
        Write-Host "  [추가] $($m.col)" -ForegroundColor Green
    } else {
        Write-Host "  [기존] $($m.col) 이미 존재" -ForegroundColor Gray
    }
}

Write-Host ""
Write-Host "=== 현재 설정값 확인 ===" -ForegroundColor Cyan
$cm3 = $conn.CreateCommand()
$cm3.CommandText = "SELECT Id, EnableMajorTrading, MaxMajorSlots, MaxPumpSlots, MaxDailyEntries, PumpMargin FROM dbo.GeneralSettings"
$rd = $cm3.ExecuteReader()
while ($rd.Read()) {
    Write-Host "  UserId=$($rd['Id'])  EnableMajor=$($rd['EnableMajorTrading'])  MajorSlots=$($rd['MaxMajorSlots'])  PumpSlots=$($rd['MaxPumpSlots'])  DailyEntries=$($rd['MaxDailyEntries'])  PumpMargin=$($rd['PumpMargin'])"
}
$rd.Close()
$conn.Close()
Write-Host ""
Write-Host "완료. 봇 재시작 후 설정 적용." -ForegroundColor Yellow
