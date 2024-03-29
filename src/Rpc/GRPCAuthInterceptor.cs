using Grpc.Core;
using Grpc.Core.Interceptors;
using NodeGuard.Data.Repositories.Interfaces;

namespace NodeGuard.Rpc;

public class GRPCAuthInterceptor : Interceptor
{
    private readonly IAPITokenRepository _apiTokenRepository;
    
    public GRPCAuthInterceptor(IAPITokenRepository apiTokenRepository)
    {
        _apiTokenRepository = apiTokenRepository;
    }
    
public override async Task<TResponse> UnaryServerHandler<TRequest, TResponse>(
    TRequest request, 
    ServerCallContext context, 
    UnaryServerMethod<TRequest, TResponse> continuation)
    {
        var token = context.RequestHeaders.FirstOrDefault(x => x.Key == "auth-token")?.Value;
        if (token == null)
        {
            throw new RpcException(new Status(StatusCode.Unauthenticated, "No token provided"));
        }

        var apiToken = await _apiTokenRepository.GetByToken(token);
        
        if (apiToken?.IsBlocked ?? true)
        {
            throw new RpcException(new Status(StatusCode.Unauthenticated, "Invalid token"));
        }
        
        return await continuation(request, context);
    }
    
}