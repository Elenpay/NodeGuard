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

using FluentAssertions;
using Grpc.Core;
using NodeGuard.Data.Models;
using NodeGuard.Data.Repositories.Interfaces;

namespace NodeGuard.Rpc;

public class AuthInterceptorTests
{
    [Fact]
    public async Task AuthInterceptor_NoTokenProvided()
    {
        // Arrange
        var context = TestServerCallContext.Create();
        var mockedApiTokenRepository = new Mock<IAPITokenRepository>();
        var interceptor = new GRPCAuthInterceptor(mockedApiTokenRepository.Object);
        var continuation = new UnaryServerMethod<string, string>((request, context) => { return Task.FromResult("response"); });

        // Act & Assert
        var act = () => interceptor.UnaryServerHandler<string, string>(String.Empty, context, continuation);

        // Assert
        await act
            .Should()
            .ThrowAsync<RpcException>()
            .WithMessage($"Status(StatusCode=\"Unauthenticated\", Detail=\"No token provided\")");
    }

    [Fact]
    public async Task AuthInterceptor_NonExistingToken()
    {
        // Arrange
        var context = TestServerCallContext.Create(new Metadata { { "auth-token", "iamastupidtoken" } });
        var mockedApiTokenRepository = new Mock<IAPITokenRepository>();
        var interceptor = new GRPCAuthInterceptor(mockedApiTokenRepository.Object);
        var continuation = new UnaryServerMethod<string, string>((request, context) => { return Task.FromResult("response"); });

        // Act
        var act = () => interceptor.UnaryServerHandler<string, string>(String.Empty, context, continuation);

        // Assert
        await act
            .Should()
            .ThrowAsync<RpcException>()
            .WithMessage("Status(StatusCode=\"Unauthenticated\", Detail=\"Invalid token\")");

    }

    [Fact]
    public async Task AuthInterceptor_ExistingTokenValid()
    {
        // Arrange
        var validToken = "iamavalidtoken";
        var apiTokenFixture = new APIToken { TokenHash = validToken };
        var mockedApiTokenRepository = new Mock<IAPITokenRepository>();
        //GetBytoken mocked to return a valid token
        mockedApiTokenRepository.Setup(x => x.GetByToken(It.IsAny<string>()))
            .ReturnsAsync(apiTokenFixture);

        var context = TestServerCallContext.Create(new Metadata { { "auth-token", validToken } });
        var interceptor = new GRPCAuthInterceptor(mockedApiTokenRepository.Object);
        var mockContinuation = new Mock<UnaryServerMethod<string, string>>();
        mockContinuation.Setup(x => x.Invoke(
                It.IsAny<string>(),
                It.IsAny<TestServerCallContext>())
            )
            .ReturnsAsync("response");

        // In order to test if the request is continued, which would mean that the token is valid,
        // we mock the continuation function and we verify that it is called once.

        // Act
        await interceptor.UnaryServerHandler<string, string>(String.Empty, context, mockContinuation.Object);

        // Assert
        mockContinuation.Verify(
            c => c(
                It.IsAny<string>(),
                It.IsAny<TestServerCallContext>()),
            Times.Once);
    }

    [Fact]
    public async Task AuthInterceptor_ExistingTokenInvalid()
    {
        // Arrange
        var validToken = "iamaninvalidtoken";
        var apiTokenFixture = new APIToken { TokenHash = validToken, IsBlocked = true };
        var mockedApiTokenRepository = new Mock<IAPITokenRepository>();
        //GetBytoken mocked to return a valid token
        mockedApiTokenRepository.Setup(x => x.GetByToken(It.IsAny<string>()))
            .ReturnsAsync(apiTokenFixture);

        var context = TestServerCallContext.Create(new Metadata { { "auth-token", validToken } });
        var interceptor = new GRPCAuthInterceptor(mockedApiTokenRepository.Object);
        var continuation = new UnaryServerMethod<string, string>((request, context) => { return Task.FromResult("response"); });

        // Act 
        var act = () => interceptor.UnaryServerHandler(String.Empty, context, continuation);

        // Assert
        await act
            .Should()
            .ThrowAsync<RpcException>()
            .WithMessage("Status(StatusCode=\"Unauthenticated\", Detail=\"Invalid token\")");
    }
}
