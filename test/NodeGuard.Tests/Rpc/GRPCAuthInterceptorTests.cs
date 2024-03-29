
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
        var continuation = new UnaryServerMethod<string, string>(async (request, context) => { return "response"; });
        
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
        var context = TestServerCallContext.Create(new Metadata{{"auth-token", "iamastupidtoken"}});
        var mockedApiTokenRepository = new Mock<IAPITokenRepository>();
        var interceptor = new GRPCAuthInterceptor(mockedApiTokenRepository.Object);
        var continuation = new UnaryServerMethod<string, string>(async (request, context) => { return "response"; });
        
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
        
        var context = TestServerCallContext.Create(new Metadata{{"auth-token", validToken}});
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
        var apiTokenFixture = new APIToken { TokenHash = validToken , IsBlocked = true};
        var mockedApiTokenRepository = new Mock<IAPITokenRepository>();
        //GetBytoken mocked to return a valid token
        mockedApiTokenRepository.Setup(x => x.GetByToken(It.IsAny<string>()))
            .ReturnsAsync(apiTokenFixture);
        
        var context = TestServerCallContext.Create(new Metadata{{"auth-token", validToken}});
        var interceptor = new GRPCAuthInterceptor(mockedApiTokenRepository.Object);
        var continuation = new UnaryServerMethod<string, string>(async (request, context) => { return "response"; });

        // Act 
        var act = () => interceptor.UnaryServerHandler(String.Empty, context, continuation);
        
        // Assert
        await act
            .Should()
            .ThrowAsync<RpcException>()
            .WithMessage("Status(StatusCode=\"Unauthenticated\", Detail=\"Invalid token\")");
    }
}