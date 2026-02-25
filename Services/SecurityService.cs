using System;
using System.Security.Cryptography;
using System.Text;

namespace TradingBot.Services
{
    /// <summary>
    /// Windows DPAPI를 사용한 암호화/복호화 및 비밀번호 해싱을 제공합니다.
    /// </summary>
    public static class SecurityService
    {
        // DPAPI를 위한 추가 엔트로피 (보안 강화). 이 값을 변경하면 기존에 암호화된 모든 데이터를 읽을 수 없게 됩니다.
        private static readonly byte[] s_entropy = Encoding.Unicode.GetBytes("CoinFF-TradingBot-DPAPI-Salt");

        /// <summary>
        /// 문자열을 현재 사용자 범위로 암호화합니다.
        /// </summary>
        /// <param name="input">암호화할 문자열</param>
        /// <returns>Base64로 인코딩된 암호화된 문자열</returns>
        public static string EncryptString(string input)
        {
            if (string.IsNullOrEmpty(input))
                return string.Empty;

            byte[] data = Encoding.Unicode.GetBytes(input);
            // 현재 사용자만 접근할 수 있도록 데이터를 보호합니다.
            byte[] encryptedData = ProtectedData.Protect(data, s_entropy, DataProtectionScope.CurrentUser);
            return Convert.ToBase64String(encryptedData);
        }

        /// <summary>
        /// 암호화된 문자열을 복호화합니다.
        /// </summary>
        /// <param name="encryptedInput">Base64로 인코딩된 암호화된 문자열</param>
        /// <returns>복호화된 문자열. 실패 시 빈 문자열을 반환합니다.</returns>
        public static string DecryptString(string encryptedInput)
        {
            if (string.IsNullOrEmpty(encryptedInput))
                return string.Empty;

            try
            {
                byte[] encryptedData = Convert.FromBase64String(encryptedInput);
                // 데이터를 보호 해제합니다.
                byte[] decryptedData = ProtectedData.Unprotect(encryptedData, s_entropy, DataProtectionScope.CurrentUser);
                return Encoding.Unicode.GetString(decryptedData);
            }
            catch (CryptographicException)
            {
                // 다른 사용자나 다른 컴퓨터에서 암호화된 데이터를 복호화하려고 할 때 발생합니다.
                return string.Empty;
            }
            catch (FormatException)
            {
                // Base64 형식이 아닐 때 발생합니다.
                return string.Empty;
            }
        }

        /// <summary>
        /// 비밀번호를 SHA256으로 해싱합니다.
        /// </summary>
        public static string HashPassword(string password)
        {
            if (string.IsNullOrEmpty(password)) return string.Empty;

            using var sha256 = SHA256.Create();
            var hashedBytes = sha256.ComputeHash(Encoding.UTF8.GetBytes(password));
            return BitConverter.ToString(hashedBytes).Replace("-", "").ToLowerInvariant();
        }

        /// <summary>
        /// 비밀번호 해시를 비교하여 유효성을 확인합니다.
        /// </summary>
        public static bool VerifyPassword(string password, string expectedHash)
        {
            if (string.IsNullOrEmpty(password) || string.IsNullOrEmpty(expectedHash))
                return false;

            string actualHash = HashPassword(password);
            return string.Equals(actualHash, expectedHash, StringComparison.OrdinalIgnoreCase);
        }
    }
}