# =====================================================
# [긴급] holdingMinutes 계산 열 제거 PowerShell 스크립트
# 사용법: .\fix-holdingminutes.ps1
# =====================================================

Write-Host "🔧 holdingMinutes 계산 열 제거 스크립트 시작..." -ForegroundColor Cyan
Write-Host ""

# SQL 파일 경로
$sqlFile = Join-Path $PSScriptRoot "fix-holdingminutes.sql"

if (-not (Test-Path $sqlFile)) {
    Write-Host "❌ 오류: fix-holdingminutes.sql 파일을 찾을 수 없습니다." -ForegroundColor Red
    Write-Host "   경로: $sqlFile" -ForegroundColor Yellow
    exit 1
}

# DB 연결 문자열 (실제 DB 이름 사용)
$serverName = "COFFEE-MACHINE\SQLEXPRESS"
$databaseName = "TradingDB"

Write-Host "📊 데이터베이스 연결 정보:" -ForegroundColor Yellow
Write-Host "   서버: $serverName" -ForegroundColor Gray
Write-Host "   DB: $databaseName" -ForegroundColor Gray
Write-Host ""

# sqlcmd 실행
Write-Host "🔄 SQL 스크립트 실행 중..." -ForegroundColor Yellow

try {
    $result = sqlcmd -S $serverName -d $databaseName -E -i $sqlFile -b

    if ($LASTEXITCODE -eq 0) {
        Write-Host ""
        Write-Host "✅ 마이그레이션 성공!" -ForegroundColor Green
        Write-Host ""
        Write-Host "📝 결과:" -ForegroundColor Cyan
        $result | ForEach-Object { Write-Host "   $_" -ForegroundColor Gray }
        Write-Host ""
        Write-Host "🚀 이제 TradingBot을 다시 시작하세요." -ForegroundColor Green
    } else {
        Write-Host ""
        Write-Host "❌ 마이그레이션 실패 (Exit Code: $LASTEXITCODE)" -ForegroundColor Red
        Write-Host ""
        Write-Host "📝 오류 메시지:" -ForegroundColor Yellow
        $result | ForEach-Object { Write-Host "   $_" -ForegroundColor Red }
        Write-Host ""
        Write-Host "💡 해결 방법:" -ForegroundColor Yellow
        Write-Host "   1. SQL Server가 실행 중인지 확인하세요" -ForegroundColor Gray
        Write-Host "   2. TradingDB 데이터베이스가 존재하는지 확인하세요" -ForegroundColor Gray
        Write-Host "   3. 관리자 권한으로 PowerShell을 실행해보세요" -ForegroundColor Gray
        Write-Host "   4. fix-holdingminutes.sql을 SSMS에서 직접 실행해보세요" -ForegroundColor Gray
        exit 1
    }
} catch {
    Write-Host ""
    Write-Host "❌ 스크립트 실행 중 오류 발생:" -ForegroundColor Red
    Write-Host "   $($_.Exception.Message)" -ForegroundColor Red
    Write-Host ""
    Write-Host "💡 sqlcmd가 설치되어 있는지 확인하세요." -ForegroundColor Yellow
    Write-Host "   설치: https://aka.ms/sqlcmd" -ForegroundColor Gray
    exit 1
}

Write-Host ""
Write-Host "📖 참고:" -ForegroundColor Cyan
Write-Host "   - holdingMinutes 계산 열이 제거되었습니다" -ForegroundColor Gray
Write-Host "   - ExitTime은 이제 NULL을 허용합니다 (진입 시 NULL)" -ForegroundColor Gray
Write-Host "   - 보유 시간이 필요한 경우 쿼리에서 DATEDIFF(MINUTE, EntryTime, ExitTime)을 사용하세요" -ForegroundColor Gray
Write-Host ""
