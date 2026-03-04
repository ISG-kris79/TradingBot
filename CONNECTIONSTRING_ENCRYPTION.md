# ConnectionString 암호화 가이드

## 왜 암호화가 필요한가요?

appsettings.json에 DB 비밀번호를 평문으로 저장하면:
- ❌ 파일을 보는 사람이 비밀번호를 바로 알 수 있음
- ❌ Git에 실수로 커밋하면 보안 위험
- ❌ 프로그램 배포 시 비밀번호 노출

**Windows DPAPI 암호화**를 사용하면:
- ✅ 현재 Windows 사용자만 복호화 가능
- ✅ 파일을 봐도 암호화된 값만 보임
- ✅ 다른 PC로 복사해도 복호화 불가 (보안 강화)

---

## 사용 방법

### 1단계: ConnectionString 암호화하기

**방법 A: 명령줄 도구 사용** (권장)

1. CMD나 PowerShell에서 실행:
```cmd
TradingBot.exe --encrypt-connection
```

2. 프롬프트에 연결 문자열 입력:
```
Server=localhost;Database=TradingBotDB;User Id=sa;Password=1234;TrustServerCertificate=True;
```

3. 출력된 암호화된 값을 복사

**방법 B: C# 코드로 직접 암호화**

```csharp
using TradingBot.Services;

string original = "Server=localhost;Database=TradingBotDB;User Id=sa;Password=1234;TrustServerCertificate=True;";
string encrypted = SecurityService.EncryptString(original);
Console.WriteLine(encrypted);
```

---

### 2단계: appsettings.json에 암호화된 값 설정

#### 옵션 1: IsEncrypted 플래그 사용 (권장)

```json
{
  "ConnectionStrings": {
    "DefaultConnection": "AQAAANCMnd8BFdERjHoAwE/Cl+sBAAAA...(암호화된 긴 문자열)",
    "IsEncrypted": true
  },
  "Binance": {
    ...
  }
}
```

#### 옵션 2: 평문 사용 (개발 환경)

```json
{
  "ConnectionStrings": {
    "DefaultConnection": "Server=localhost;Database=TradingBotDB;Integrated Security=True;TrustServerCertificate=True;",
    "IsEncrypted": false
  }
}
```

---

### 3단계: 프로그램 실행

프로그램이 시작되면:
- `IsEncrypted`가 `true`면 자동으로 복호화
- `IsEncrypted`가 `false`면 평문 그대로 사용

**디버그 출력 확인**:
- Visual Studio 출력 창에서 `[AppConfig] ConnectionString 복호화 성공` 메시지 확인

---

## 주의사항

⚠️ **암호화된 ConnectionString은 암호화한 Windows 계정에서만 복호화 가능합니다!**

- **같은 PC의 다른 사용자**: 복호화 불가
- **다른 PC**: 복호화 불가
- **Windows 재설치 후**: 복호화 불가

### 배포 시 권장 방법

#### 방법 1: 각 사용자가 직접 암호화

1. Setup.exe로 설치
2. 설치 폴더에서 `TradingBot.exe --encrypt-connection` 실행
3. 생성된 암호화 값을 appsettings.json에 복사

#### 방법 2: 첫 실행 시 설정 UI

- 프로그램 첫 실행 시 DB 설정 창이 자동으로 뜸
- 연결 문자열 입력하면 자동으로 암호화 저장 (구현 예정)

---

## 예시

### 예시 1: SQL Server Express (Windows 인증)

**평문**:
```
Server=localhost\SQLEXPRESS;Database=TradingBotDB;Integrated Security=True;TrustServerCertificate=True;
```

**암호화 후**:
```
AQAAANCMnd8BFdERjHoAwE/Cl+sBAAAA2X5OZVqh4UG7nrqR7gX3VwAAAAACAAAAAAAQZgAAAAEAACAAAAA...
```

### 예시 2: SQL Server (SQL 인증)

**평문**:
```
Server=192.168.1.100;Database=TradingBotDB;User Id=trader;Password=MySecr3tP@ss;TrustServerCertificate=True;
```

**암호화 후**:
```
AQAAANCMnd8BFdERjHoAwE/Cl+sBAAAA5K9pQwLm4kG3oX1Y8Vz2WQAAAAACAAAAAAAQZgAAAAEAACAAAAB...
```

---

## 문제 해결

### 문제: "ConnectionString 복호화 실패 - 다른 사용자 계정에서 암호화되었을 수 있음"

**원인**: 다른 PC나 다른 계정에서 암호화된 값을 사용했습니다.

**해결**: 현재 PC의 현재 사용자 계정에서 다시 암호화하세요.

```cmd
TradingBot.exe --encrypt-connection
```

### 문제: 로그인 시 "ConnectionString 속성이 초기화되지 않았습니다"

**원인**: appsettings.json에 ConnectionString이 비어있거나 복호화에 실패했습니다.

**해결**:
1. appsettings.json 확인
2. `IsEncrypted: true`인데 복호화 실패 시 → 다시 암호화
3. `IsEncrypted: false`인데 평문이 비었으면 → 연결 문자열 입력

---

## 개발 팁

### Visual Studio에서 디버깅

`App.xaml.cs`의 `OnStartup`에 브레이크포인트를 걸고:

```csharp
// 명령줄 인자로 암호화 도구 실행
if (e.Args.Length > 0 && e.Args[0] == "--encrypt-connection")
{
    ConnectionStringEncryptor.RunInteractive();
    this.Shutdown();
    return;
}
```

Visual Studio 프로젝트 설정 → 디버그 → 명령줄 인자: `--encrypt-connection`

---

## 보안 강화 옵션

현재는 DPAPI `CurrentUser` 범위를 사용합니다. 더 높은 보안이 필요하면:

1. **Azure Key Vault** 사용
2. **환경 변수**로 관리
3. **Windows Credential Manager** 사용

필요하면 요청해주세요!
