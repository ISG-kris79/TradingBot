using System;
using TradingBot.Services;
using TradingBot.Shared.Services;

namespace TradingBot
{
    /// <summary>
    /// 복호화 테스트 도구 - 암호화된 값이 현재 계정에서 복호화 가능한지 확인
    /// 사용법: TradingBot.exe --test-decrypt
    /// </summary>
    public static class DecryptionTester
    {
        public static void RunTest()
        {
            var logPath = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "decrypt_test.log");
            using var writer = new System.IO.StreamWriter(logPath, false);
            var originalOut = Console.Out;
            Console.SetOut(writer);
            
            Console.WriteLine("=== ConnectionString 복호화 테스트 (AES256) ===\n");
            
            // appsettings.json 읽기
            string settingsPath = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "appsettings.json");
            
            if (!System.IO.File.Exists(settingsPath))
            {
                Console.WriteLine($"오류: appsettings.json 파일을 찾을 수 없습니다.");
                Console.WriteLine($"경로: {settingsPath}");
                return;
            }
            
            Console.WriteLine($"설정 파일: {settingsPath}\n");
            
            try
            {
                string json = System.IO.File.ReadAllText(settingsPath);
                var config = System.Text.Json.JsonSerializer.Deserialize<System.Text.Json.JsonDocument>(json);
                
                var connStrings = config?.RootElement.GetProperty("ConnectionStrings");
                string encryptedValue = connStrings?.GetProperty("DefaultConnection").GetString() ?? "";
                bool isEncrypted = connStrings?.GetProperty("IsEncrypted").GetBoolean() ?? false;
                
                Console.WriteLine($"IsEncrypted: {isEncrypted}");
                Console.WriteLine($"암호화된 값 길이: {encryptedValue.Length} 문자\n");
                
                if (!isEncrypted)
                {
                    Console.WriteLine("평문으로 설정되어 있습니다. 복호화 불필요.");
                    Console.WriteLine($"\n연결 문자열: {encryptedValue}");
                    return;
                }
                
                if (string.IsNullOrEmpty(encryptedValue))
                {
                    Console.WriteLine("오류: DefaultConnection이 비어있습니다.");
                    return;
                }
                
                Console.WriteLine("복호화 시도 중...\n");
                
                string decrypted = SecurityService.DecryptString(encryptedValue);
                
                if (string.IsNullOrEmpty(decrypted))
                {
                    Console.ForegroundColor = ConsoleColor.Red;
                    Console.WriteLine("❌ 복호화 실패!");
                    Console.ResetColor();
                    Console.WriteLine("\n가능한 원인:");
                    Console.WriteLine("1. 잘못된 암호화 형식");
                    Console.WriteLine("2. 다른 암호화 키로 암호화됨 (SecurityService.cs의 s_aesKey)");
                    Console.WriteLine("3. 데이터 손상");
                    Console.WriteLine("\n해결 방법:");
                    Console.WriteLine("- appsettings.json에서 IsEncrypted를 false로 변경하고 평문 사용");
                    Console.WriteLine("- 또는 다시 암호화: TradingBot.exe --encrypt-connection");
                    Console.WriteLine("- AES256 키 확인: SecurityService.cs의 s_aesKey 설정 확인");
                }
                else
                {
                    Console.ForegroundColor = ConsoleColor.Green;
                    Console.WriteLine("✅ 복호화 성공!");
                    Console.ResetColor();
                    Console.WriteLine($"\n복호화된 연결 문자열:\n{decrypted}");
                }
            }
            catch (Exception ex)
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine($"오류 발생: {ex.Message}");
                Console.ResetColor();
                Console.WriteLine($"\n상세:\n{ex}");
            }
            
            Console.WriteLine("\n\n현재 사용자 정보:");
            Console.WriteLine($"사용자 이름: {Environment.UserName}");
            Console.WriteLine($"도메인: {Environment.UserDomainName}");
            Console.WriteLine($"컴퓨터 이름: {Environment.MachineName}");
            
            Console.SetOut(originalOut);
            writer.Close();
            
            System.Windows.MessageBox.Show($"테스트 완료! 결과는 다음 파일에 저장되었습니다:\n\n{logPath}", 
                "복호화 테스트", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Information);
        }
    }
}
