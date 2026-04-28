# AI 학습 검증 스크립트 — 봇 재시작 후 zip 생성 여부 및 학습 진행 확인
# 사용: ./verify-ai-learning.ps1
#   오전 7시까지 주기적으로 실행하여 AI 학습 정상 작동 확인

$ErrorActionPreference = "Continue"
[Console]::OutputEncoding = [Text.Encoding]::UTF8

$ModelDir = Join-Path $env:LOCALAPPDATA "TradingBot\Models"
$RequiredFiles = @(
    "EntryTimingModel.zip",
    "EntryTimingModel_Major.zip",
    "EntryTimingModel_Pump.zip",
    "EntryTimingModel_Spike.zip"
)

Write-Host "==========================================" -ForegroundColor Cyan
Write-Host "AI Learning Verification @ $(Get-Date -Format 'yyyy-MM-dd HH:mm:ss')" -ForegroundColor Cyan
Write-Host "Model dir: $ModelDir" -ForegroundColor Cyan
Write-Host "==========================================" -ForegroundColor Cyan

# 1. 4-variant zip 파일 존재 확인
Write-Host ""
Write-Host "[1] 4-variant 모델 zip 파일 상태" -ForegroundColor Yellow
$allExist = $true
foreach ($name in $RequiredFiles) {
    $path = Join-Path $ModelDir $name
    if (Test-Path $path) {
        $file = Get-Item $path
        $age = (Get-Date) - $file.LastWriteTime
        $status = if ($age.TotalMinutes -lt 60) { "✅ 최근 학습" } else { "⚠️ 오래됨" }
        Write-Host ("  [{0}] {1,-35} {2,-8} ({3} | {4}KB | 업데이트 {5:F0}분 전)" -f $status, $name, "", $file.LastWriteTime.ToString("HH:mm:ss"), [math]::Round($file.Length/1024, 0), $age.TotalMinutes) -ForegroundColor $(if ($age.TotalMinutes -lt 60) { "Green" } else { "Yellow" })
    } else {
        Write-Host ("  [❌ 미생성] {0}" -f $name) -ForegroundColor Red
        $allExist = $false
    }
}

# 2. 기타 모델 파일 (비교용)
Write-Host ""
Write-Host "[2] 기타 AI 모델 파일 (참고)" -ForegroundColor Yellow
if (Test-Path $ModelDir) {
    Get-ChildItem $ModelDir -Filter "*.zip" | Where-Object { $RequiredFiles -notcontains $_.Name } | ForEach-Object {
        Write-Host ("  {0,-35} {1,-10}KB | {2}" -f $_.Name, [math]::Round($_.Length/1024, 0), $_.LastWriteTime.ToString("HH:mm:ss"))
    }
}

# 3. DB 로그에서 학습 이벤트 조회
Write-Host ""
Write-Host "[3] 최근 학습 이벤트 (DB AiTrainingRuns)" -ForegroundColor Yellow

try {
    $AesKey = [byte[]](0x43, 0x6F, 0x69, 0x6E, 0x46, 0x46, 0x2D, 0x54, 0x72, 0x61, 0x64, 0x69, 0x6E, 0x67, 0x42, 0x6F, 0x74, 0x2D, 0x41, 0x45, 0x53, 0x32, 0x35, 0x36, 0x2D, 0x4B, 0x65, 0x79, 0x2D, 0x33, 0x32, 0x42)
    function AesDecrypt($enc) {
        if ([string]::IsNullOrEmpty($enc)) { return "" }
        $f = [Convert]::FromBase64String($enc)
        $a = [System.Security.Cryptography.Aes]::Create(); $a.Key = $AesKey
        $iv = New-Object byte[] $a.IV.Length; $c = New-Object byte[] ($f.Length - $a.IV.Length)
        [Buffer]::BlockCopy($f, 0, $iv, 0, $a.IV.Length); [Buffer]::BlockCopy($f, $a.IV.Length, $c, 0, $c.Length)
        $a.IV = $iv; $d = $a.CreateDecryptor($a.Key, $a.IV)
        $s = [Text.Encoding]::UTF8.GetString($d.TransformFinalBlock($c, 0, $c.Length)); $a.Dispose(); $d.Dispose(); return $s
    }
    $json = Get-Content "$PSScriptRoot\appsettings.json" -Raw | ConvertFrom-Json
    $connStr = AesDecrypt $json.ConnectionStrings.DefaultConnection

    $cn = New-Object System.Data.SqlClient.SqlConnection $connStr
    $cn.Open()
    $cm = $cn.CreateCommand()
    $cm.CommandText = @"
SELECT TOP 20 RunId, ProjectName, Stage, Success, SampleCount,
  CAST(Accuracy AS DECIMAL(6,4)) AS Acc,
  CAST(F1Score AS DECIMAL(6,4)) AS F1
FROM AiTrainingRuns
ORDER BY Id DESC
"@
    $ap = New-Object System.Data.SqlClient.SqlDataAdapter $cm
    $ds = New-Object System.Data.DataSet
    [void]$ap.Fill($ds)
    $cn.Close()
    $ds.Tables[0] | Format-Table -AutoSize
} catch {
    Write-Host ("  DB 조회 실패: {0}" -f $_.Exception.Message) -ForegroundColor Red
}

# 4. Bootstrap 로그 검색
Write-Host ""
Write-Host "[4] BOOTSTRAP 로그 (FooterLogs 최근 1시간)" -ForegroundColor Yellow
try {
    $cn2 = New-Object System.Data.SqlClient.SqlConnection $connStr
    $cn2.Open()
    $cm2 = $cn2.CreateCommand()
    $cm2.CommandText = @"
SELECT TOP 15 Timestamp AT TIME ZONE 'UTC' AT TIME ZONE 'Korea Standard Time' AS KST,
  LEFT(Message, 180) AS Msg
FROM FooterLogs
WHERE Timestamp >= DATEADD(HOUR, -1, GETUTCDATE())
  AND (Message LIKE '%BOOTSTRAP%' OR Message LIKE '%ONLINE_ML%' OR Message LIKE '%OnlineLearning%' OR Message LIKE '%재학습%')
ORDER BY Timestamp DESC
"@
    $ap2 = New-Object System.Data.SqlClient.SqlDataAdapter $cm2
    $ds2 = New-Object System.Data.DataSet
    [void]$ap2.Fill($ds2)
    $cn2.Close()
    $ds2.Tables[0] | Format-Table -AutoSize -Wrap
} catch {
    Write-Host ("  로그 조회 실패: {0}" -f $_.Exception.Message) -ForegroundColor Red
}

# 5. 종합 판정
Write-Host ""
Write-Host "========== 종합 판정 ==========" -ForegroundColor Cyan
if ($allExist) {
    Write-Host "✅ 4-variant 모델 zip 전부 존재 → AI 학습 정상" -ForegroundColor Green
} else {
    Write-Host "⚠️ 일부 zip 파일 미생성 → 봇 재시작 후 30분 이내 BOOTSTRAP 완료 대기" -ForegroundColor Yellow
    Write-Host "  - 재시작 직후: Bootstrap 이 DB TradeHistory 로 자동 일괄 학습" -ForegroundColor Gray
    Write-Host "  - 15분마다: 재학습 timer 발동" -ForegroundColor Gray
    Write-Host "  - 5건 쌓일 때마다: 샘플 트리거 재학습" -ForegroundColor Gray
}
Write-Host ""
Write-Host "다음 확인 권장: 15~30분 후 이 스크립트 재실행" -ForegroundColor Cyan
