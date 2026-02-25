using System;
using TradingBot.Services;
using Xunit;

namespace TradingBot.Tests
{
    public class SecurityServiceTests
    {
        [Fact]
        public void EncryptString_ReturnsNonEmptyString()
        {
            // Arrange
            string plainText = "MySecretApiKey123";

            // Act
            string encrypted = SecurityService.EncryptString(plainText);

            // Assert
            Assert.NotNull(encrypted);
            Assert.NotEmpty(encrypted);
            Assert.NotEqual(plainText, encrypted);
        }

        [Fact]
        public void DecryptString_ReturnsOriginalPlainText()
        {
            // Arrange
            string plainText = "MySecretApiKey123";
            string encrypted = SecurityService.EncryptString(plainText);

            // Act
            string decrypted = SecurityService.DecryptString(encrypted);

            // Assert
            Assert.Equal(plainText, decrypted);
        }

        [Fact]
        public void EncryptString_WithEmptyString_ReturnsEmpty()
        {
            // Arrange
            string plainText = "";

            // Act
            string encrypted = SecurityService.EncryptString(plainText);

            // Assert
            Assert.Equal("", encrypted);
        }

        [Fact]
        public void DecryptString_WithEmptyString_ReturnsEmpty()
        {
            // Arrange
            string cipherText = "";

            // Act
            string decrypted = SecurityService.DecryptString(cipherText);

            // Assert
            Assert.Equal("", decrypted);
        }

        [Fact]
        public void EncryptDecrypt_WithSpecialCharacters_WorksCorrectly()
        {
            // Arrange
            string plainText = "API_KEY!@#$%^&*(){}[]<>?/|\\";

            // Act
            string encrypted = SecurityService.EncryptString(plainText);
            string decrypted = SecurityService.DecryptString(encrypted);

            // Assert
            Assert.Equal(plainText, decrypted);
        }

        [Fact]
        public void HashPassword_ReturnsNonEmptyHash()
        {
            // Arrange
            string password = "MyPassword123!";

            // Act
            string hash = SecurityService.HashPassword(password);

            // Assert
            Assert.NotNull(hash);
            Assert.NotEmpty(hash);
            Assert.NotEqual(password, hash);
        }

        [Fact]
        public void VerifyPassword_WithCorrectPassword_ReturnsTrue()
        {
            // Arrange
            string password = "MyPassword123!";
            string hash = SecurityService.HashPassword(password);

            // Act
            bool isValid = SecurityService.VerifyPassword(password, hash);

            // Assert
            Assert.True(isValid);
        }

        [Fact]
        public void VerifyPassword_WithIncorrectPassword_ReturnsFalse()
        {
            // Arrange
            string password = "MyPassword123!";
            string wrongPassword = "WrongPassword";
            string hash = SecurityService.HashPassword(password);

            // Act
            bool isValid = SecurityService.VerifyPassword(wrongPassword, hash);

            // Assert
            Assert.False(isValid);
        }
    }
}
