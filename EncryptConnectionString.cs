using System;
using TradingBot.Shared.Services;

namespace TradingBot
{
    /// <summary>
    /// ConnectionString을 암호화하는 유틸리티 클래스
    /// 사용법: 프로그램 실행 시 --encrypt-connection 인자 전달
    /// </summary>
    public static class ConnectionStringEncryptor
    {
        public static void RunInteractive()
        {
            Console.WriteLine("=== ConnectionString 암호화 도구 (AES256) ===\n");
            Console.WriteLine("예시: Server=localhost;Database=TradingDB;Integrated Security=True;TrustServerCertificate=True;\n");
            Console.Write("암호화할 ConnectionString을 입력하세요: ");
            
            string? input = Console.ReadLine();
            
            if (string.IsNullOrWhiteSpace(input))
            {
                Console.WriteLine("\n오류: 연결 문자열이 비어있습니다.");
                return;
            }

            string encrypted = SecurityService.EncryptString(input);
            
            Console.WriteLine("\n=== 암호화 완료 (AES256) ===");
            Console.WriteLine("\nappsettings.json에 아래 값을 복사하세요:\n");
            Console.WriteLine($"\"EncryptedConnectionString\": \"{encrypted}\"");
            Console.WriteLine("\n또는\n");
            Console.WriteLine("\"ConnectionStrings\": {");
            Console.WriteLine($"  \"DefaultConnection\": \"{encrypted}\",");
            Console.WriteLine($"  \"IsEncrypted\": true");
            Console.WriteLine("}");
            Console.WriteLine("\n주의: AES256 암호화는 모든 PC에서 복호화 가능합니다.");
            Console.WriteLine("보안 강화를 위해 암호화 키를 변경하려면 SecurityService.cs를 수정하세요.");
        }
    }
}
