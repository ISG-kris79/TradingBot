# AIOTUSDT TRAILING_STOP 수동 테스트 스크립트
# 실행: powershell -ExecutionPolicy Bypass -File ./test-trailing-stop.ps1
# 주의: 운영 계정에 AIOTUSDT 포지션이 있어야 함 (reduceOnly=true)

param(
    [string]$Symbol = "AIOTUSDT",
    [decimal]$CallbackRate = 2.0,
    [decimal]$Quantity = 0
)

# appsettings.json 에서 API Key 로드
$settings = Get-Content "$PSScriptRoot\appsettings.json" -Raw | ConvertFrom-Json
$apiKey = $settings.Binance.ApiKey
$apiSecret = $settings.Binance.ApiSecret

if (-not $apiKey -or -not $apiSecret) {
    Write-Host "❌ API Key/Secret 없음 (appsettings.json 에 Binance.ApiKey, Binance.ApiSecret 설정 필요)" -ForegroundColor Red
    Write-Host "또는 암호화된 UserSecrets 를 사용 중인 경우 운영 PC에서만 테스트 가능" -ForegroundColor Yellow
    exit 1
}

$baseUrl = "https://fapi.binance.com"
$endpoint = "/fapi/v1/order"
$timestamp = [int64](([datetime]::UtcNow - (Get-Date "1970-01-01")).TotalMilliseconds)

# 1. 현재 포지션 확인
Write-Host "=== [1] $Symbol 포지션 조회 ===" -ForegroundColor Cyan
$posQuery = "timestamp=$timestamp"
$hmac = New-Object System.Security.Cryptography.HMACSHA256
$hmac.Key = [System.Text.Encoding]::UTF8.GetBytes($apiSecret)
$sig = [BitConverter]::ToString($hmac.ComputeHash([System.Text.Encoding]::UTF8.GetBytes($posQuery))).Replace("-","").ToLower()
$posUrl = "$baseUrl/fapi/v2/positionRisk?$posQuery&signature=$sig"

try {
    $positions = Invoke-RestMethod -Uri $posUrl -Method Get -Headers @{ "X-MBX-APIKEY" = $apiKey }
    $pos = $positions | Where-Object { $_.symbol -eq $Symbol -and [decimal]$_.positionAmt -ne 0 }
    if (-not $pos) {
        Write-Host "❌ $Symbol 활성 포지션 없음 — 먼저 포지션 진입 필요" -ForegroundColor Red
        exit 1
    }
    Write-Host "  positionAmt: $($pos.positionAmt)"
    Write-Host "  entryPrice:  $($pos.entryPrice)"
    Write-Host "  leverage:    $($pos.leverage)"
    Write-Host "  unrealizedPnL: $($pos.unRealizedProfit)"

    if ($Quantity -eq 0) {
        $Quantity = [math]::Abs([decimal]$pos.positionAmt)
    }
} catch {
    Write-Host "❌ 포지션 조회 실패: $($_.Exception.Message)" -ForegroundColor Red
    exit 1
}

# 2. TRAILING_STOP_MARKET 주문
Write-Host ""
Write-Host "=== [2] $Symbol TRAILING_STOP_MARKET 주문 시도 ===" -ForegroundColor Cyan
$side = if ([decimal]$pos.positionAmt -gt 0) { "SELL" } else { "BUY" }
$timestamp2 = [int64](([datetime]::UtcNow - (Get-Date "1970-01-01")).TotalMilliseconds)
$params = "symbol=$Symbol&side=$side&type=TRAILING_STOP_MARKET&quantity=$Quantity&callbackRate=$CallbackRate&reduceOnly=true&timestamp=$timestamp2"

$hmac2 = New-Object System.Security.Cryptography.HMACSHA256
$hmac2.Key = [System.Text.Encoding]::UTF8.GetBytes($apiSecret)
$sig2 = [BitConverter]::ToString($hmac2.ComputeHash([System.Text.Encoding]::UTF8.GetBytes($params))).Replace("-","").ToLower()

$url = "$baseUrl$endpoint?$params&signature=$sig2"
Write-Host "  Side: $side, Qty: $Quantity, CallbackRate: $CallbackRate%"

try {
    $response = Invoke-RestMethod -Uri $url -Method Post -Headers @{ "X-MBX-APIKEY" = $apiKey }
    Write-Host "✅ 성공!" -ForegroundColor Green
    Write-Host "  Order ID: $($response.orderId)"
    Write-Host "  Status:   $($response.status)"
    Write-Host "  Type:     $($response.type)"
    Write-Host "  Side:     $($response.side)"
    Write-Host "  Qty:      $($response.origQty)"
    Write-Host "  ActivationPrice: $($response.activatePrice)"
    Write-Host "  CallbackRate:    $($response.priceRate)"
    $response | ConvertTo-Json
} catch {
    $err = $_.Exception.Message
    if ($_.ErrorDetails.Message) {
        $errBody = $_.ErrorDetails.Message
        Write-Host "❌ 실패!" -ForegroundColor Red
        Write-Host "  Response: $errBody" -ForegroundColor Red
    } else {
        Write-Host "❌ 실패: $err" -ForegroundColor Red
    }
}
