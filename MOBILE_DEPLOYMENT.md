# 📱 TradingBot 모바일 배포 가이드

## 📋 목차
1. [현재 상태](#현재-상태)
2. [Android APK 배포](#android-apk-배포)
3. [iOS 배포](#ios-배포)
4. [MAUI 프로젝트 설정](#maui-프로젝트-설정)
5. [대안 방법](#대안-방법)

---

## 🔍 현재 상태

### 프로젝트 구조
- **WPF 프로젝트**: `TradingBot.csproj` (Windows 데스크톱 전용)
- **MAUI 프로젝트**: `CoinFF.Mobile.csproj` (모바일 준비 중, 미완성)

### 지원 플랫폼
```xml
<TargetFrameworks>
  net9.0-android;
  net9.0-ios;
  net9.0-maccatalyst;
  net9.0-windows10.0.19041.0
</TargetFrameworks>
```

---

## 📦 Android APK 배포

### 방법 1: .NET MAUI로 빌드 (권장)

#### 1️⃣ 사전 준비

**필수 도구 설치**
```powershell
# Visual Studio 2022 Workloads 설치
# - .NET Multi-platform App UI development
# - Android SDK (API 34+)

# 또는 명령줄 도구 설치
dotnet workload install maui-android
```

**Android SDK 확인**
```powershell
# Android SDK 경로 확인
${env:ANDROID_HOME}
# 예: C:\Program Files (x86)\Android\android-sdk

# SDK 버전 확인
${env:ANDROID_HOME}\platform-tools\adb.exe version
```

#### 2️⃣ MAUI 프로젝트 완성

**CoinFF.Mobile.csproj 업데이트**
```xml
<Project Sdk="Microsoft.NET.Sdk">
	<PropertyGroup>
		<TargetFrameworks>net9.0-android;net9.0-ios</TargetFrameworks>
		<OutputType>Exe</OutputType>
		<RootNamespace>CoinFF.Mobile</RootNamespace>
		<UseMaui>true</UseMaui>
		<SingleProject>true</SingleProject>
		<ImplicitUsings>enable</ImplicitUsings>
		<Nullable>enable</Nullable>
		
		<!-- 앱 정보 -->
		<ApplicationTitle>TradingBot Monitor</ApplicationTitle>
		<ApplicationId>com.tradingbot.monitor</ApplicationId>
		<ApplicationIdGuid>your-guid-here</ApplicationIdGuid>
		<ApplicationVersion>1</ApplicationVersion>
		<ApplicationDisplayVersion>1.2.7</ApplicationDisplayVersion>
		
		<!-- Android 설정 -->
		<SupportedOSPlatformVersion Condition="$([MSBuild]::GetTargetPlatformIdentifier('$(TargetFramework)')) == 'android'">21.0</SupportedOSPlatformVersion>
		<AndroidPackageFormat>apk</AndroidPackageFormat>
		<AndroidSigningKeyStore>your-keystore.keystore</AndroidSigningKeyStore>
		<AndroidSigningKeyAlias>your-alias</AndroidSigningKeyAlias>
		<AndroidSigningKeyPass>your-password</AndroidSigningKeyPass>
		<AndroidSigningStorePass>your-store-password</AndroidSigningStorePass>
	</PropertyGroup>

	<ItemGroup>
		<!-- 공통 코드 참조 -->
		<ProjectReference Include="..\TradingBot.Core\TradingBot.Core.csproj" />
		
		<!-- MAUI 필수 패키지 -->
		<PackageReference Include="Microsoft.Maui.Controls" Version="9.0.0" />
		<PackageReference Include="Microsoft.Maui.Controls.Compatibility" Version="9.0.0" />
		<PackageReference Include="Microsoft.Extensions.Logging.Debug" Version="9.0.0" />
	</ItemGroup>

	<ItemGroup>
		<!-- Android 아이콘 -->
		<MauiIcon Include="Resources\AppIcon\appicon.svg" ForegroundFile="Resources\AppIcon\appiconfg.svg" Color="#512BD4" />
		<MauiSplashScreen Include="Resources\Splash\splash.svg" Color="#512BD4" BaseSize="128,128" />
		
		<!-- 이미지 및 폰트 -->
		<MauiImage Include="Resources\Images\*" />
		<MauiFont Include="Resources\Fonts\*" />
	</ItemGroup>
</Project>
```

#### 3️⃣ APK 빌드

**디버그 APK 생성 (테스트용)**
```powershell
# 프로젝트 디렉터리로 이동
cd E:\PROJECT\CoinFF\TradingBot\TradingBot

# Android APK 빌드
dotnet build CoinFF.Mobile.csproj -f net9.0-android -c Debug

# 출력 위치
# bin\Debug\net9.0-android\com.tradingbot.monitor-Signed.apk
```

**릴리스 APK 생성 (배포용)**
```powershell
# 서명된 릴리스 APK 빌드
dotnet publish CoinFF.Mobile.csproj -f net9.0-android -c Release

# 출력 위치
# bin\Release\net9.0-android\publish\com.tradingbot.monitor-Signed.apk
```

#### 4️⃣ APK 서명 (Google Play 배포용)

**키스토어 생성**
```powershell
# Java keytool 사용
keytool -genkey -v -keystore tradingbot.keystore -alias tradingbot -keyalg RSA -keysize 2048 -validity 10000

# 정보 입력
# - Keystore 비밀번호
# - 이름, 조직, 도시, 국가
```

**서명 설정**
```xml
<!-- CoinFF.Mobile.csproj에 추가 -->
<PropertyGroup Condition="'$(Configuration)' == 'Release'">
  <AndroidKeyStore>true</AndroidKeyStore>
  <AndroidSigningKeyStore>tradingbot.keystore</AndroidSigningKeyStore>
  <AndroidSigningKeyAlias>tradingbot</AndroidSigningKeyAlias>
  <AndroidSigningKeyPass>your-password</AndroidSigningKeyPass>
  <AndroidSigningStorePass>your-password</AndroidSigningStorePass>
</PropertyGroup>
```

#### 5️⃣ 배포 방법

**방법 A: Google Play Store**
1. [Google Play Console](https://play.google.com/console) 접속
2. 개발자 계정 생성 (일회성 $25)
3. 새 앱 생성
4. APK 또는 AAB 업로드
5. 스토어 등록 정보 입력 (스크린샷, 설명 등)
6. 심사 제출

**방법 B: 직접 배포 (사이드로딩)**
```powershell
# 1. APK 파일을 웹 서버에 업로드
Copy-Item bin\Release\net9.0-android\publish\*.apk -Destination C:\inetpub\wwwroot\downloads\

# 2. 사용자에게 다운로드 링크 제공
# https://yoursite.com/downloads/tradingbot-v1.2.7.apk

# 3. 사용자 설치 안내
# - Android 설정 > 보안 > "알 수 없는 출처" 허용
# - APK 다운로드 및 설치
```

**방법 C: Firebase App Distribution (베타 테스트)**
```powershell
# Firebase CLI 설치
npm install -g firebase-tools

# Firebase 로그인
firebase login

# 앱 배포
firebase appdistribution:distribute `
  bin\Release\net9.0-android\publish\tradingbot.apk `
  --app your-firebase-app-id `
  --release-notes "v1.2.7 release" `
  --groups testers
```

---

## 🍎 iOS 배포

### 사전 준비

**필수 요구사항**
- ✅ macOS (Xcode 실행 필수)
- ✅ Apple Developer Account ($99/년)
- ✅ Xcode 15+ 설치
- ✅ .NET 9.0 SDK for macOS

### 빌드 프로세스

#### 1️⃣ Mac에서 빌드

**Visual Studio for Mac 사용**
```bash
# Mac에서
cd /path/to/TradingBot

# iOS 빌드
dotnet build CoinFF.Mobile.csproj -f net9.0-ios -c Release

# 실제 기기용 IPA 생성
dotnet publish CoinFF.Mobile.csproj -f net9.0-ios -c Release -p:RuntimeIdentifier=ios-arm64
```

#### 2️⃣ App Store 배포

**Xcode에서 서명 및 제출**
```bash
# 1. Apple Developer Certificate 생성
# - developer.apple.com 접속
# - Certificates, Identifiers & Profiles

# 2. Provisioning Profile 생성

# 3. Xcode에서 Archive 생성
# - Product > Archive

# 4. App Store Connect에서 제출
# - Xcode > Window > Organizer > Upload to App Store
```

#### 3️⃣ TestFlight (베타 테스트)
```bash
# Archive 업로드 후 App Store Connect에서
# 1. TestFlight 섹션 이동
# 2. 내부 테스터 또는 외부 테스터 추가
# 3. 테스트 정보 입력 후 제출
```

---

## 🔧 MAUI 프로젝트 설정

### 프로젝트 구조 재구성

**1. 공통 코드 분리**
```
TradingBot/
├── TradingBot.Core/              # 공통 비즈니스 로직
│   ├── Models/
│   ├── Services/
│   ├── ViewModels/
│   └── TradingBot.Core.csproj
├── TradingBot.Desktop/           # WPF 프로젝트 (기존)
│   └── TradingBot.csproj
└── TradingBot.Mobile/            # MAUI 프로젝트
    ├── Platforms/
    │   ├── Android/
    │   ├── iOS/
    │   └── Windows/
    ├── Resources/
    ├── MauiProgram.cs
    └── CoinFF.Mobile.csproj
```

**2. 공통 프로젝트 생성**
```powershell
# Core 라이브러리 생성
dotnet new classlib -n TradingBot.Core -f net9.0

# 공통 코드 이동
Move-Item TradingBot\Models\* TradingBot.Core\Models\
Move-Item TradingBot\Services\* TradingBot.Core\Services\
Move-Item TradingBot\ViewModels\* TradingBot.Core\ViewModels\
```

**3. MAUI 페이지 구현**

**MainPage.xaml (모바일 대시보드)**
```xml
<?xml version="1.0" encoding="utf-8" ?>
<ContentPage xmlns="http://schemas.microsoft.com/dotnet/2021/maui"
             xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
             x:Class="CoinFF.Mobile.MainPage"
             Title="TradingBot Monitor">
    
    <ScrollView>
        <VerticalStackLayout Padding="20" Spacing="20">
            <!-- 계좌 요약 -->
            <Frame BackgroundColor="#1E293B" CornerRadius="10" Padding="15">
                <VerticalStackLayout Spacing="10">
                    <Label Text="총 자산" FontSize="14" TextColor="#94A3B8"/>
                    <Label Text="{Binding TotalEquity}" FontSize="28" FontAttributes="Bold" TextColor="#00E676"/>
                    <Label Text="{Binding AvailableBalance}" FontSize="14" TextColor="#64748B"/>
                </VerticalStackLayout>
            </Frame>

            <!-- 활성 포지션 -->
            <Frame BackgroundColor="#1E293B" CornerRadius="10" Padding="15">
                <VerticalStackLayout Spacing="10">
                    <Label Text="활성 포지션" FontSize="16" FontAttributes="Bold" TextColor="White"/>
                    <CollectionView ItemsSource="{Binding ActivePositions}">
                        <CollectionView.ItemTemplate>
                            <DataTemplate>
                                <Grid ColumnDefinitions="*,Auto" Padding="0,5">
                                    <Label Text="{Binding Symbol}" TextColor="White"/>
                                    <Label Grid.Column="1" Text="{Binding PnL}" TextColor="{Binding PnLColor}"/>
                                </Grid>
                            </DataTemplate>
                        </CollectionView.ItemTemplate>
                    </CollectionView>
                </VerticalStackLayout>
            </Frame>

            <!-- 최근 알림 -->
            <Frame BackgroundColor="#1E293B" CornerRadius="10" Padding="15">
                <VerticalStackLayout Spacing="10">
                    <Label Text="최근 알림" FontSize="16" FontAttributes="Bold" TextColor="White"/>
                    <CollectionView ItemsSource="{Binding RecentAlerts}" HeightRequest="200">
                        <CollectionView.ItemTemplate>
                            <DataTemplate>
                                <Label Text="{Binding Message}" TextColor="#94A3B8" Padding="0,5"/>
                            </DataTemplate>
                        </CollectionView.ItemTemplate>
                    </CollectionView>
                </VerticalStackLayout>
            </Frame>

            <!-- 제어 버튼 -->
            <Grid ColumnDefinitions="*,*" ColumnSpacing="10">
                <Button Text="시작" Command="{Binding StartCommand}" BackgroundColor="#00E676" TextColor="Black"/>
                <Button Grid.Column="1" Text="정지" Command="{Binding StopCommand}" BackgroundColor="#FF5252" TextColor="White"/>
            </Grid>
        </VerticalStackLayout>
    </ScrollView>
</ContentPage>
```

**MauiProgram.cs**
```csharp
using Microsoft.Extensions.Logging;
using CoinFF.Mobile.ViewModels;
using CoinFF.Mobile.Services;

namespace CoinFF.Mobile;

public static class MauiProgram
{
    public static MauiApp CreateMauiApp()
    {
        var builder = MauiApp.CreateBuilder();
        builder
            .UseMauiApp<App>()
            .ConfigureFonts(fonts =>
            {
                fonts.AddFont("OpenSans-Regular.ttf", "OpenSansRegular");
                fonts.AddFont("OpenSans-Semibold.ttf", "OpenSansSemibold");
            });

        // 서비스 등록
        builder.Services.AddSingleton<MainViewModel>();
        builder.Services.AddSingleton<ITradingService, TradingService>();
        builder.Services.AddSingleton<IApiService, BinanceApiService>();

        // 페이지 등록
        builder.Services.AddTransient<MainPage>();
        builder.Services.AddTransient<LogsPage>();
        builder.Services.AddTransient<ChartPage>();

#if DEBUG
        builder.Logging.AddDebug();
#endif

        return builder.Build();
    }
}
```

---

## 🔄 대안 방법

### 옵션 1: Progressive Web App (PWA)

**장점**
- ✅ 단일 코드베이스
- ✅ 스토어 승인 불필요
- ✅ 즉시 업데이트 가능
- ✅ 모든 플랫폼 지원

**구현**
```csharp
// Blazor WebAssembly 프로젝트 생성
dotnet new blazorwasm -n TradingBot.Web

// PWA 지원 추가
// wwwroot/manifest.json
{
  "name": "TradingBot Monitor",
  "short_name": "TradingBot",
  "start_url": "/",
  "display": "standalone",
  "background_color": "#0F111A",
  "theme_color": "#00E676",
  "icons": [
    {
      "src": "icon-192.png",
      "sizes": "192x192",
      "type": "image/png"
    },
    {
      "src": "icon-512.png",
      "sizes": "512x512",
      "type": "image/png"
    }
  ]
}

// Service Worker 등록
// wwwroot/service-worker.js
self.addEventListener('install', event => event.waitUntil(onInstall(event)));
self.addEventListener('activate', event => event.waitUntil(onActivate(event)));
self.addEventListener('fetch', event => event.respondWith(onFetch(event)));
```

**배포**
```powershell
# 빌드
dotnet publish TradingBot.Web\TradingBot.Web.csproj -c Release -o publish

# Azure Static Web Apps, Netlify, Vercel 등에 배포
# 또는 자체 서버에 호스팅
```

### 옵션 2: 하이브리드 앱 (WebView)

**Cordova/Capacitor 사용**
```bash
# Capacitor 설치
npm install -g @capacitor/cli

# 프로젝트 초기화
npx cap init TradingBot com.tradingbot.monitor

# Android 플랫폼 추가
npx cap add android

# iOS 플랫폼 추가
npx cap add ios

# 빌드
npm run build
npx cap sync
npx cap open android
```

### 옵션 3: React Native / Flutter

**장점**
- ✅ 네이티브 성능
- ✅ 풍부한 생태계
- ✅ 직관적인 개발

**단점**
- ❌ 기존 C# 코드 재작성 필요
- ❌ 학습 곡선

---

## 📊 배포 방법 비교

| 방법 | 장점 | 단점 | 권장도 |
|------|------|------|--------|
| **.NET MAUI** | C# 코드 재사용, 네이티브 성능 | 설정 복잡 | ⭐⭐⭐⭐⭐ |
| **PWA** | 빠른 배포, 크로스 플랫폼 | 오프라인 제한, 네이티브 기능 제한 | ⭐⭐⭐⭐ |
| **Cordova** | 빠른 개발 | 성능 제한 | ⭐⭐⭐ |
| **React Native** | 큰 커뮤니티 | 코드 재작성 | ⭐⭐ |

---

## 🚀 빠른 시작 (권장 플로우)

### 1단계: 모바일 모니터링 앱 (읽기 전용)
```powershell
# MAUI 프로젝트로 간단한 모니터링 앱 구현
# - 실시간 계좌 정보 확인
# - 포지션 모니터링
# - 알림 수신
# - 매매 시작/정지만 가능
```

### 2단계: 전체 기능 구현
```powershell
# 고급 기능 추가
# - 차트 분석
# - 백테스트
# - 설정 변경
# - 수동 매매
```

---

## 🔐 모바일 보안 고려사항

### API 키 보호
```csharp
// Secure Storage 사용
using Microsoft.Maui.Storage;

public class SecureStorageService
{
    public async Task<string> GetApiKeyAsync()
    {
        return await SecureStorage.GetAsync("binance_api_key");
    }

    public async Task SetApiKeyAsync(string apiKey)
    {
        await SecureStorage.SetAsync("binance_api_key", apiKey);
    }
}
```

### HTTPS 통신
```csharp
// HttpClient 설정
var httpClient = new HttpClient(new HttpClientHandler
{
    ServerCertificateCustomValidationCallback = (message, cert, chain, errors) =>
    {
        return cert.Issuer == "CN=api.binance.com";
    }
});
```

---

## 📝 다음 단계

1. **CoinFF.Mobile.csproj 완성** - 앱 메타데이터, 아이콘, 권한 설정
2. **공통 코드 분리** - TradingBot.Core 프로젝트 생성
3. **MAUI 페이지 구현** - MainPage, LogsPage, ChartPage
4. **API 통신 구현** - Binance API, 백엔드 통신
5. **테스트 배포** - 내부 테스트용 APK 생성
6. **스토어 등록** - Google Play, App Store 제출

---

## 💬 도움말

**문제 발생 시**
- Android SDK 오류: Visual Studio Installer에서 재설치
- iOS 빌드 오류: Xcode Command Line Tools 확인
- 서명 오류: 키스토어 비밀번호 확인

**참고 문서**
- [.NET MAUI 공식 문서](https://learn.microsoft.com/dotnet/maui/)
- [Android 개발자 가이드](https://developer.android.com/)
- [Apple Developer 문서](https://developer.apple.com/)

---

**작성일**: 2026-02-27  
**버전**: TradingBot v1.2.7  
**작성자**: TradingBot Development Team
