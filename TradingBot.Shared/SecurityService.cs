using System;
using System.Security.Cryptography;
using System.Text;

namespace TradingBot.Shared.Services
{
    /// <summary>
    /// AES256 암호화/복호화 및 비밀번호 해싱을 제공합니다.
    /// </summary>
    public static class SecurityService
    {
        // AES256 암호화 키 (32바이트 = 256비트)
        // 주의: 실제 배포 시에는 환경변수나 안전한 방법으로 관리하세요!
        // 이 키를 변경하면 기존에 암호화된 모든 데이터를 읽을 수 없게 됩니다.
        private static readonly byte[] s_aesKey = new byte[32]
        {
            0x43, 0x6F, 0x69, 0x6E, 0x46, 0x46, 0x2D, 0x54, // "CoinFF-T"
            0x72, 0x61, 0x64, 0x69, 0x6E, 0x67, 0x42, 0x6F, // "radingBo"
            0x74, 0x2D, 0x41, 0x45, 0x53, 0x32, 0x35, 0x36, // "t-AES256"
            0x2D, 0x4B, 0x65, 0x79, 0x2D, 0x33, 0x32, 0x42  // "-Key-32B"
        };

        /// <summary>
        /// 문자열을 AES256으로 암호화합니다. 어떤 PC에서든 같은 키로 복호화 가능합니다.
        /// </summary>
        /// <param name="input">암호화할 문자열</param>
        /// <returns>Base64로 인코딩된 암호화된 문자열 (IV + 암호화된 데이터)</returns>
        public static string EncryptString(string input)
        {
            if (string.IsNullOrEmpty(input))
                return string.Empty;

            try
            {
                using var aes = Aes.Create();
                aes.Key = s_aesKey;
                aes.GenerateIV(); // 랜덤 IV 생성

                using var encryptor = aes.CreateEncryptor(aes.Key, aes.IV);
                byte[] inputBytes = Encoding.UTF8.GetBytes(input);
                byte[] encryptedBytes = encryptor.TransformFinalBlock(inputBytes, 0, inputBytes.Length);

                // IV + 암호화된 데이터를 결합
                byte[] result = new byte[aes.IV.Length + encryptedBytes.Length];
                Buffer.BlockCopy(aes.IV, 0, result, 0, aes.IV.Length);
                Buffer.BlockCopy(encryptedBytes, 0, result, aes.IV.Length, encryptedBytes.Length);

                return Convert.ToBase64String(result);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[SecurityService] 암호화 오류: {ex.Message}");
                return string.Empty;
            }
        }

        /// <summary>
        /// AES256으로 암호화된 문자열을 복호화합니다.
        /// </summary>
        /// <param name="encryptedInput">Base64로 인코딩된 암호화된 문자열 (IV + 암호화된 데이터)</param>
        /// <returns>복호화된 문자열. 실패 시 빈 문자열을 반환합니다.</returns>
        public static string DecryptString(string encryptedInput)
        {
            if (string.IsNullOrEmpty(encryptedInput))
                return string.Empty;

            try
            {
                byte[] fullCipher = Convert.FromBase64String(encryptedInput);

                using var aes = Aes.Create();
                aes.Key = s_aesKey;

                // IV 추출 (처음 16바이트)
                byte[] iv = new byte[aes.IV.Length];
                byte[] cipher = new byte[fullCipher.Length - iv.Length];

                Buffer.BlockCopy(fullCipher, 0, iv, 0, iv.Length);
                Buffer.BlockCopy(fullCipher, iv.Length, cipher, 0, cipher.Length);

                aes.IV = iv;

                using var decryptor = aes.CreateDecryptor(aes.Key, aes.IV);
                byte[] decryptedBytes = decryptor.TransformFinalBlock(cipher, 0, cipher.Length);

                return Encoding.UTF8.GetString(decryptedBytes);
            }
            catch (CryptographicException ex)
            {
                System.Diagnostics.Debug.WriteLine($"[SecurityService] 복호화 오류: {ex.Message}");
                return string.Empty;
            }
            catch (FormatException ex)
            {
                System.Diagnostics.Debug.WriteLine($"[SecurityService] Base64 형식 오류: {ex.Message}");
                return string.Empty;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[SecurityService] 복호화 중 예외: {ex.Message}");
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
