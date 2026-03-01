# 자동 업데이트 문제 해결 가이드

## ❌ 증상
v2.0.8 실행 시 v2.0.9 업데이트 알림이 표시되지 않음

## 🔍 원인 체크리스트

### 1. Setup.exe로 설치했는지 확인 ⭐ 가장 중요
**Velopack 자동 업데이트는 Setup.exe로 설치한 경우에만 작동합니다.**

❌ **작동하지 않는 경우**:
- zip 파일 압축 해제 후 직접 실행
- bin/Debug 또는 bin/Release 폴더에서 직접 실행  
- Visual Studio에서 F5로 실행

✅ **정상 작동하는 경우**:
- GitHub Releases에서 `TradingBot-win-Setup.exe` 다운로드 후 설치
- Velopack이 생성한 설치 경로에서 실행 (보통 `C:\Users\사용자명\AppData\Local\TradingBot\`)

**확인 방법**:
```powershell
# 설치 위치 확인
Get-Process TradingBot | Select-Object Path
```

설치 경로에 `current\` 폴더가 있고 `Update.exe`가 있으면 정상 설치됨.

---

### 2. Debug 모드로 실행하지 않았는지 확인

`App.xaml.cs`에 다음 코드가 있습니다:
```csharp
#if DEBUG
    Debug.WriteLine("[Update] 디버그 모드 - 업데이트 확인 건너뜀");
    return false;
#endif
```

❌ **Debug 빌드는 업데이트 체크를 건너뜁니다.**

**해결 방법**:
- Visual Studio에서 Configuration을 **Release**로 변경
- 또는 GitHub Releases에서 다운로드한 Setup.exe 사용

---

### 3. Velopack 초기화 실패 확인

`App.xaml.cs` 시작 부분에서 Velopack 초기화:
```csharp
try
{
    VelopackApp.Build().Run();
}
catch
{
    Debug.WriteLine("[App] Velopack 초기화 스킵됨");
}
```

Velopack이 초기화에 실패하면 자동 업데이트가 작동하지 않습니다.

**로그 확인 방법**:
1. Visual Studio Output 창 확인
2. 또는 로그 파일 확인 (있다면)

---

## ✅ 해결 방법

### 방법 1: 정상 설치로 재설치 (권장)
1. 현재 앱 종료
2. GitHub Releases에서 **v2.0.8 Setup.exe** 다운로드
   ```
   https://github.com/ISG-kris79/TradingBot/releases/tag/v2.0.8
   ```
3. Setup.exe 실행 및 설치
4. 설치된 앱 실행 → 자동으로 v2.0.9 업데이트 알림 표시됨

### 방법 2: 직접 v2.0.9로 업데이트
1. 현재 앱 완전 종료
2. GitHub Releases에서 **v2.0.9 Setup.exe** 다운로드
   ```
   https://github.com/ISG-kris79/TradingBot/releases/tag/v2.0.9
   ```
3. Setup.exe 실행 (기존 설치 위치에 덮어쓰기)
4. 앱 실행 후 버전 확인 (로그인 창에 v2.0.9 표시되어야 함)

---

## 🧪 테스트 방법

### 1. 업데이트 체크 강제 실행
다음 코드를 `App.xaml.cs`의 `OnStartup`에 임시로 추가:

```csharp
protected override async void OnStartup(StartupEventArgs e)
{
    base.OnStartup(e);
    
    // 강제 업데이트 체크 (테스트용)
    var updateUrl = "https://github.com/ISG-kris79/TradingBot/releases/latest/download/releases.win.json";
    try
    {
        var mgr = new Velopack.UpdateManager(updateUrl);
        var updateInfo = await mgr.CheckForUpdatesAsync();
        
        if (updateInfo != null)
        {
            System.Windows.MessageBox.Show(
                $"업데이트 발견: {updateInfo.TargetFullRelease.Version}\n" +
                $"현재 버전: {System.Reflection.Assembly.GetExecutingAssembly().GetName().Version}",
                "업데이트 테스트");
        }
        else
        {
            System.Windows.MessageBox.Show("최신 버전입니다.", "업데이트 테스트");
        }
    }
    catch (Exception ex)
    {
        System.Windows.MessageBox.Show($"업데이트 체크 실패: {ex.Message}", "오류");
    }
    
    // 기존 코드...
}
```

### 2. releases.win.json 직접 확인
브라우저에서 다음 URL 접속:
```
https://github.com/ISG-kris79/TradingBot/releases/latest/download/releases.win.json
```

정상이면 다음과 같은 JSON 표시:
```json
{
  "Assets": [
    {
      "PackageId": "TradingBot",
      "Version": "2.0.9",
      "Type": "Full",
      "FileName": "TradingBot-2.0.9-full.nupkg",
      ...
    }
  ]
}
```

---

## 📝 향후 예방책

1. **항상 Setup.exe로 배포**
   - Portable.zip은 자동 업데이트 불가능
   - 사용자에게 Setup.exe 설치 권장

2. **Release 빌드로 테스트**
   - Debug 빌드는 업데이트 체크 비활성화됨
   - 배포 전 Release 빌드로 자동 업데이트 테스트 필수

3. **로그 추가**
   - 업데이트 체크 과정을 로그로 기록
   - 문제 발생 시 빠른 진단 가능

---

## 🔗 관련 문서
- [DEPLOYMENT_GUIDE.md](DEPLOYMENT_GUIDE.md) - 배포 가이드
- [Velopack 공식 문서](https://velopack.io/)
