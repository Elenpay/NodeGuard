using System.Security.Cryptography;
using FluentAssertions;
using NodeGuard.Data.Models;

namespace NodeGuard.Tests;

public class APITokenTests
{
    [Fact]
    void GenerateTokenHash_TokenGenerated()
    {
        // Arrange
        var token = new APIToken();

        // Act
        var password = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        token.GenerateTokenHash(password, Constants.API_TOKEN_SALT);

        // Assert
        token.TokenHash.Should().NotBeNullOrEmpty();
    }
}