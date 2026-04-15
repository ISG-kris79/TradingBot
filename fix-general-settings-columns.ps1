. "$PSScriptRoot\query-open-pos.ps1"

$sql = @"
IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('dbo.GeneralSettings') AND name = 'MajorTrendProfile')
    ALTER TABLE dbo.GeneralSettings ADD MajorTrendProfile NVARCHAR(50) NULL DEFAULT 'Balanced';
IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('dbo.GeneralSettings') AND name = 'PumpTp1Roe')
    ALTER TABLE dbo.GeneralSettings ADD PumpTp1Roe DECIMAL(18,4) NULL DEFAULT 20;
IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('dbo.GeneralSettings') AND name = 'PumpTp2Roe')
    ALTER TABLE dbo.GeneralSettings ADD PumpTp2Roe DECIMAL(18,4) NULL DEFAULT 100;
IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('dbo.GeneralSettings') AND name = 'PumpTimeStopMinutes')
    ALTER TABLE dbo.GeneralSettings ADD PumpTimeStopMinutes DECIMAL(18,4) NULL DEFAULT 120;
IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('dbo.GeneralSettings') AND name = 'PumpStopDistanceWarnPct')
    ALTER TABLE dbo.GeneralSettings ADD PumpStopDistanceWarnPct DECIMAL(18,4) NULL DEFAULT 1;
IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('dbo.GeneralSettings') AND name = 'PumpStopDistanceBlockPct')
    ALTER TABLE dbo.GeneralSettings ADD PumpStopDistanceBlockPct DECIMAL(18,4) NULL DEFAULT 1.3;
"@

$cn = New-Object System.Data.SqlClient.SqlConnection (Get-CS); $cn.Open()
$cm = $cn.CreateCommand(); $cm.CommandText = $sql; $cm.CommandTimeout = 60
$cm.ExecuteNonQuery()
$cn.Close()

Write-Host "OK: MajorTrendProfile + PumpTp1Roe + PumpTp2Roe + PumpTimeStopMinutes + PumpStopDistanceWarnPct + PumpStopDistanceBlockPct added" -ForegroundColor Green
