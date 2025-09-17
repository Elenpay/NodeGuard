// NodeGuard
// Copyright (C) 2025  Elenpay
//
// This program is free software: you can redistribute it and/or modify
// it under the terms of the GNU Affero General Public License as published by
// the Free Software Foundation, either version 3 of the License, or
// (at your option) any later version.
//
// This program is distributed in the hope that it will be useful,
// but WITHOUT ANY WARRANTY, without even the implied warranty of
// MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
// GNU Affero General Public License for more details.
//
// You should have received a copy of the GNU Affero General Public License
// along with this program.  If not, see http://www.gnu.org/licenses/.

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
