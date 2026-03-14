param(
    [Parameter(Mandatory = $true)]
    [string]$ServerInstance,

    [Parameter(Mandatory = $true)]
    [string]$Database,

    [switch]$UseSqlAuth,
    [string]$SqlUser,
    [string]$SqlPassword
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Invoke-SqlScript {
    param(
        [Parameter(Mandatory = $true)]
        [string]$FilePath
    )

    if (-not (Test-Path $FilePath)) {
        throw "스크립트 파일을 찾을 수 없습니다: $FilePath"
    }

    Write-Host "\n▶ 실행: $FilePath" -ForegroundColor Cyan

    $args = @('-S', $ServerInstance, '-d', $Database, '-b', '-i', $FilePath)
    if ($UseSqlAuth) {
        if ([string]::IsNullOrWhiteSpace($SqlUser) -or [string]::IsNullOrWhiteSpace($SqlPassword)) {
            throw 'SQL 인증 모드 사용 시 -SqlUser, -SqlPassword가 필요합니다.'
        }
        $args += @('-U', $SqlUser, '-P', $SqlPassword)
    }
    else {
        $args += '-E'
    }

    & sqlcmd @args
    if ($LASTEXITCODE -ne 0) {
        throw "실패: $FilePath (exit=$LASTEXITCODE)"
    }

    Write-Host "✅ 완료: $FilePath" -ForegroundColor Green
}

function Invoke-SqlQuery {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Title,
        [Parameter(Mandatory = $true)]
        [string]$Query
    )

    Write-Host "\n🔎 검증: $Title" -ForegroundColor Yellow

    $args = @('-S', $ServerInstance, '-d', $Database, '-b', '-Q', $Query)
    if ($UseSqlAuth) {
        $args += @('-U', $SqlUser, '-P', $SqlPassword)
    }
    else {
        $args += '-E'
    }

    & sqlcmd @args
    if ($LASTEXITCODE -ne 0) {
        throw "검증 실패: $Title (exit=$LASTEXITCODE)"
    }
}

if (-not (Get-Command sqlcmd -ErrorAction SilentlyContinue)) {
    throw 'sqlcmd가 설치되어 있지 않습니다. SQL Server Command Line Utilities를 설치 후 다시 실행하세요.'
}

$dbDir = Join-Path $PSScriptRoot 'Database'
$scripts = @(
    (Join-Path $dbDir 'GeneralSettings_Schema.sql'),
    (Join-Path $dbDir 'GeneralSettings_AddPumpStairAndTpRatioColumns.sql'),
    (Join-Path $dbDir 'GeneralSettings_NormalizeMajorDefaults_20260314.sql'),
    (Join-Path $dbDir 'TradeLogging_Schema.sql')
)

Write-Host '=== User Scope DB Migration 시작 ===' -ForegroundColor Magenta
Write-Host "서버: $ServerInstance"
Write-Host "DB: $Database"

foreach ($script in $scripts) {
    Invoke-SqlScript -FilePath $script
}

Invoke-SqlQuery -Title 'UserId 컬럼 확인' -Query @"
SELECT
    t.name AS TableName,
    c.name AS ColumnName
FROM sys.tables t
JOIN sys.columns c ON c.object_id = t.object_id
WHERE t.name IN ('TradeLogs', 'TradeHistory', 'FundTransferLog', 'PortfolioRebalancingLog', 'RebalancingAction', 'ArbitrageExecutionLog')
  AND c.name = 'UserId'
ORDER BY t.name;
"@

Invoke-SqlQuery -Title '인덱스 확인' -Query @"
SELECT name, object_name(object_id) AS TableName
FROM sys.indexes
WHERE name IN ('IX_TradeLogs_UserId_Time', 'IX_TradeHistory_UserId_ExitTime', 'IX_TradeHistory_UserId_EntryTime')
ORDER BY TableName, name;
"@

Invoke-SqlQuery -Title 'GeneralSettings FK 확인' -Query @"
SELECT fk.name, OBJECT_NAME(fk.parent_object_id) AS TableName, OBJECT_NAME(fk.referenced_object_id) AS RefTable
FROM sys.foreign_keys fk
WHERE fk.name = 'FK_GeneralSettings_Users_Id';
"@

Invoke-SqlQuery -Title 'GeneralSettings PUMP 튜닝 컬럼 확인' -Query @"
SELECT c.name AS ColumnName, t.name AS DataType, c.precision, c.scale, c.is_nullable
FROM sys.columns c
JOIN sys.types t ON c.user_type_id = t.user_type_id
WHERE c.object_id = OBJECT_ID('dbo.GeneralSettings')
  AND c.name IN ('PumpFirstTakeProfitRatioPct', 'PumpStairStep1Roe', 'PumpStairStep2Roe', 'PumpStairStep3Roe')
ORDER BY c.column_id;
"@

Write-Host "\n🎉 User Scope DB Migration + 검증 완료" -ForegroundColor Green
