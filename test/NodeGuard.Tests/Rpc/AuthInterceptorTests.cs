
using Grpc.Core;
using NodeGuard.Data.Models;
using NodeGuard.Data.Repositories.Interfaces;

namespace NodeGuard.Rpc;

public class AuthInterceptorTests
{
    [Fact]
    public async Task AuthInterceptor_NoTokenProvided()
    {
        var context = TestServerCallContext.Create();
        var mockedApiTokenRepository = new Mock<IAPITokenRepository>();
        var interceptor = new ServerAuthInterceptor(mockedApiTokenRepository.Object);
        var continuation = new UnaryServerMethod<string, string>(async (request, context) => { return "response"; });

        var expectedMessage = "No token provided";
        
        var exception = await Assert.ThrowsAsync<RpcException>(() => interceptor.UnaryServerHandler<string, string>(String.Empty, context, continuation));
        Assert.Equal(expectedMessage, exception.Status.Detail);
    }
    
    [Fact]
    public async Task AuthInterceptor_NonExistingToken()
    {
        var context = TestServerCallContext.Create(new Metadata{{"auth-token", "iamastupidtoken"}});
        var mockedApiTokenRepository = new Mock<IAPITokenRepository>();
        var interceptor = new ServerAuthInterceptor(mockedApiTokenRepository.Object);
        var continuation = new UnaryServerMethod<string, string>(async (request, context) => { return "response"; });

        var expectedMessage = "Invalid token";
        
        var exception = await Assert.ThrowsAsync<RpcException>(() => interceptor.UnaryServerHandler<string, string>(String.Empty, context, continuation));
        Assert.Equal(expectedMessage, exception.Status.Detail);
    }
    
    [Fact]
    public async Task AuthInterceptor_ExistingToken()
    {
        var validToken = "iamavalidtoken";
        var apiTokenFixture = new APIToken { TokenHash = validToken };
        var mockedApiTokenRepository = new Mock<IAPITokenRepository>();
        //GetBytoken mocked to return a valid token
        mockedApiTokenRepository.Setup(x => x.GetByToken(It.IsAny<string>()))
            .ReturnsAsync(apiTokenFixture);
        
        var context = TestServerCallContext.Create(new Metadata{{"auth-token", validToken}});
        var interceptor = new ServerAuthInterceptor(mockedApiTokenRepository.Object);
        var mockContinuation = new Mock<UnaryServerMethod<string, string>>();
        mockContinuation.Setup(x => x.Invoke(
                It.IsAny<string>(), 
                It.IsAny<TestServerCallContext>())
            )
            .ReturnsAsync("response");
        
        // In order to test if the request is continued, which would mean that the token is valid,
        // we mock the continuation function and we verify that it is called once.

        await interceptor.UnaryServerHandler<string, string>(String.Empty, context, mockContinuation.Object);
        mockContinuation.Verify(
            c => c(
                It.IsAny<string>(),
                It.IsAny<TestServerCallContext>()),
            Times.Once);
    }
}