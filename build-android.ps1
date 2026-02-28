<#
.SYNOPSIS
    Builds the .NET MAUI Android application (CoinFF.Mobile) and generates an APK.

.DESCRIPTION
    This script publishes the MAUI project for the Android platform.
    It supports both Debug and Release configurations.
    For Release builds, it's recommended to configure signing in the .csproj file.

.PARAMETER Configuration
    The build configuration to use. 'Debug' or 'Release'.

.PARAMETER Install
    If specified, tries to install the generated APK on a connected Android device using ADB.

.EXAMPLE
    # Build a Debug APK
    .\build-android.ps1

    # Clean and Build a Debug APK
    .\build-android.ps1 -Clean

    # Build a Release APK
    .\build-android.ps1 -Configuration Release

    # Build and install on a connected device
    .\build-android.ps1 -Install
#>
param(
    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Debug",
    [switch]$Install,
    [switch]$Clean,
    [string]$KeyStorePath = "your-release.keystore",
    [string]$KeyStoreAlias = "your-alias",
    [string]$KeyStorePassword = "",
    [string]$StorePassword = ""
)

Write-Host "🚀 Starting Android APK build (Configuration: $Configuration)..." -ForegroundColor Cyan

# .NET MAUI 워크로드 확인 및 자동 설치
Write-Host "🔎 Checking for .NET MAUI workload..."
$workloads = dotnet workload list
if ($workloads -notlike "*maui-android*") {
    Write-Host "⚠️ .NET MAUI for Android workload not found. Attempting to install..." -ForegroundColor Yellow
    dotnet workload install maui-android
    if ($LASTEXITCODE -ne 0) {
        Write-Host "❌ Failed to install MAUI workload. Please install it manually." -ForegroundColor Red
        exit 1
    }
    Write-Host "✅ MAUI workload installed successfully." -ForegroundColor Green
}
else {
    Write-Host "✅ .NET MAUI workload is already installed."
}

# 프로젝트 경로 설정 (스크립트 위치 기준)
$PSScriptRoot = Split-Path -Parent -Path $MyInvocation.MyCommand.Definition
$projectPath = Join-Path $PSScriptRoot "..\MobileApp\CoinFF.Mobile\CoinFF.Mobile.csproj"

# Clean 옵션 처리
if ($Clean) {
    Write-Host "🧹 Cleaning previous build artifacts..." -ForegroundColor Cyan
    dotnet clean $projectPath -c $Configuration
    if ($LASTEXITCODE -eq 0) { Write-Host "✅ Clean successful." -ForegroundColor Green }
}

# 빌드 명령어 실행
$publishCommand = "dotnet publish $projectPath -f net9.0-android -c $Configuration"

# 릴리스 빌드 시 서명 옵션 추가
if ($Configuration -eq "Release") {
    if (-not (Test-Path $KeyStorePath)) {
        Write-Host "❌ Keystore file not found at '$KeyStorePath'. Please provide a valid path for Release builds." -ForegroundColor Red
        exit 1
    }

    # 비밀번호가 제공되지 않은 경우 대화형으로 입력 받기
    if ([string]::IsNullOrEmpty($KeyStorePassword)) {
        Write-Host "🔑 Enter Keystore Password:" -ForegroundColor Yellow -NoNewline
        $secureKeyPass = Read-Host -AsSecureString
        $KeyStorePassword = [System.Runtime.InteropServices.Marshal]::PtrToStringAuto([System.Runtime.InteropServices.Marshal]::SecureStringToBSTR($secureKeyPass))
        Write-Host "" # 줄바꿈
    }

    if ([string]::IsNullOrEmpty($StorePassword)) {
        Write-Host "🔐 Enter Store Password:" -ForegroundColor Yellow -NoNewline
        $secureStorePass = Read-Host -AsSecureString
        $StorePassword = [System.Runtime.InteropServices.Marshal]::PtrToStringAuto([System.Runtime.InteropServices.Marshal]::SecureStringToBSTR($secureStorePass))
        Write-Host "" # 줄바꿈
    }

    $publishCommand += " -p:AndroidKeyStore=true -p:AndroidSigningKeyStore='$KeyStorePath' -p:AndroidSigningKeyAlias='$KeyStoreAlias' -p:AndroidSigningKeyPass='$KeyStorePassword' -p:AndroidSigningStorePass='$StorePassword'"
}

Invoke-Expression $publishCommand

if ($LASTEXITCODE -ne 0) {
    Write-Host "❌ Build failed." -ForegroundColor Red
    exit 1
}

Write-Host "✅ Build successful!" -ForegroundColor Green

$apkPath = Get-ChildItem -Path (Join-Path $PSScriptRoot "..\MobileApp\CoinFF.Mobile\bin\$Configuration\net9.0-android\publish") -Filter "*-Signed.apk" | Select-Object -First 1

if ($apkPath) {
    Write-Host "📦 APK created at: $($apkPath.FullName)" -ForegroundColor Yellow

    # [추가] 버전 정보 읽기 및 파일명 변경
    $csprojContent = Get-Content $projectPath
    if ($csprojContent -match "<ApplicationDisplayVersion>(.*?)</ApplicationDisplayVersion>") {
        $version = $matches[1]
        $newFileName = "com.coinff.monitor-$version.apk"
        $newPath = Join-Path $apkPath.DirectoryName $newFileName
        
        Rename-Item -Path $apkPath.FullName -NewName $newFileName -Force
        $apkPath = Get-Item $newPath # 경로 업데이트
        
        Write-Host "🔖 Renamed APK to: $newFileName" -ForegroundColor Cyan
    }


    # [추가] 파일 해시(SHA256) 계산 및 출력
    $fileHash = Get-FileHash -Path $apkPath.FullName -Algorithm SHA256
    Write-Host "🔑 SHA256 Hash: $($fileHash.Hash)" -ForegroundColor Magenta

    if ($Install) {
        Write-Host "🔎 Checking for connected devices or emulators via ADB..."
        $adbDevices = (adb devices) -split '\r?\n' | Where-Object { $_.Trim() -ne '' }

        if ($adbDevices.Length -le 1) {
            Write-Host "⚠️ No Android device or emulator found. Skipping installation." -ForegroundColor Yellow
            Write-Host "   Please connect a device with USB debugging enabled or start an emulator."
        }
        else {
            Write-Host "📲 Installing APK on connected device..."
            adb install -r $apkPath.FullName
            if ($LASTEXITCODE -eq 0) {
                Write-Host "✅ Installation successful!" -ForegroundColor Green
            }
            else {
                Write-Host "❌ Installation failed. Make sure the device is ready and unlocked." -ForegroundColor Red
            }
        }
    }
}
else {
    Write-Host "❓ Could not find the generated APK file." -ForegroundColor Yellow
}