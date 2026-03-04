using System;
using System.Security.Cryptography;
using System.Text;

namespace TradingBot.Shared.Services
{
    public static class SecurityService
    {
        private static readonly byte[] s_aesKey = new byte[32]
        {
            0x43, 0x6F, 0x69, 0x6E, 0x46, 0x46, 0x2D, 0x54,
            0x72, 0x61, 0x64, 0x69, 0x6E, 0x67, 0x42, 0x6F,
            0x74, 0x2D, 0x41, 0x45, 0x53, 0x32, 0x35, 0x36,
            0x2D, 0x4B, 0x65, 0x79, 0x2D, 0x33, 0x32, 0x42
        };

        public static string EncryptString(string input)
        {
            if (string.IsNullOrEmpty(input)) return string.Empty;

            try
            {
                using var aes = Aes.Create();
                aes.Key = s_aesKey;
                aes.GenerateIV();

                using var encryptor = aes.CreateEncryptor(aes.Key, aes.IV);
                byte[] inputBytes = Encoding.UTF8.GetBytes(input);
                byte[] encryptedBytes = encryptor.TransformFinalBlock(inputBytes, 0, inputBytes.Length);

                byte[] result = new byte[aes.IV.Length + encryptedBytes.Length];
                Buffer.BlockCopy(aes.IV, 0, result, 0, aes.IV.Length);
                Buffer.BlockCopy(encryptedBytes, 0, result, aes.IV.Length, encryptedBytes.Length);

                return Convert.ToBase64String(result);
            }
            catch
            {
                return string.Empty;
            }
        }

        public static string DecryptString(string encryptedInput)
        {
            if (string.IsNullOrEmpty(encryptedInput)) return string.Empty;

            try
            {
                byte[] fullCipher = Convert.FromBase64String(encryptedInput);

                using var aes = Aes.Create();
                aes.Key = s_aesKey;

                byte[] iv = new byte[aes.IV.Length];
                byte[] cipher = new byte[fullCipher.Length - iv.Length];

                Buffer.BlockCopy(fullCipher, 0, iv, 0, iv.Length);
                Buffer.BlockCopy(fullCipher, iv.Length, cipher, 0, cipher.Length);

                aes.IV = iv;

                using var decryptor = aes.CreateDecryptor(aes.Key, aes.IV);
                byte[] decryptedBytes = decryptor.TransformFinalBlock(cipher, 0, cipher.Length);

                return Encoding.UTF8.GetString(decryptedBytes);
            }
            catch
            {
                return string.Empty;
            }
        }

        public static string HashPassword(string password)
        {
            if (string.IsNullOrEmpty(password)) return string.Empty;

            using var sha256 = SHA256.Create();
            var hashedBytes = sha256.ComputeHash(Encoding.UTF8.GetBytes(password));
            return BitConverter.ToString(hashedBytes).Replace("-", "").ToLowerInvariant();
        }

        public static bool VerifyPassword(string password, string expectedHash)
        {
            if (string.IsNullOrEmpty(password) || string.IsNullOrEmpty(expectedHash)) return false;
            return string.Equals(HashPassword(password), expectedHash, StringComparison.OrdinalIgnoreCase);
        }
    }
}
