using Grpc.Core;
using Grpc.Core.Interceptors;
using NodeGuard.Data.Repositories.Interfaces;
using NodeGuard.Helpers;

namespace NodeGuard.Rpc;

/// <summary>
/// Requires a valid, unblocked API token in the auth-token header on every call: unary and streaming alike
/// </summary>
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
        await AuthenticateAsync(context);
        return await continuation(request, context);
    }

    public override async Task<TResponse> ClientStreamingServerHandler<TRequest, TResponse>(
        IAsyncStreamReader<TRequest> requestStream,
        ServerCallContext context,
        ClientStreamingServerMethod<TRequest, TResponse> continuation)
    {
        await AuthenticateAsync(context);
        return await continuation(requestStream, context);
    }

    public override async Task ServerStreamingServerHandler<TRequest, TResponse>(
        TRequest request,
        IServerStreamWriter<TResponse> responseStream,
        ServerCallContext context,
        ServerStreamingServerMethod<TRequest, TResponse> continuation)
    {
        await AuthenticateAsync(context);
        await continuation(request, responseStream, context);
    }

    public override async Task DuplexStreamingServerHandler<TRequest, TResponse>(
        IAsyncStreamReader<TRequest> requestStream,
        IServerStreamWriter<TResponse> responseStream,
        ServerCallContext context,
        DuplexStreamingServerMethod<TRequest, TResponse> continuation)
    {
        await AuthenticateAsync(context);
        await continuation(requestStream, responseStream, context);
    }

    /// <summary>
    /// The id and name of the API token that authenticated the call, or null outside an authenticated call
    /// </summary>
    public static AuthenticatedApiToken? GetApiToken(ServerCallContext context) =>
        context.UserState.TryGetValue(AuthenticatedApiToken.HttpContextItemKey, out var token)
            ? token as AuthenticatedApiToken
            : null;

    private async Task AuthenticateAsync(ServerCallContext context)
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

        // Only the id and name go down the call, never the token row. Under ASP.NET Core, UserState is
        // HttpContext.Items, so AuditService finds the token under the same key
        context.UserState[AuthenticatedApiToken.HttpContextItemKey] = new AuthenticatedApiToken(apiToken.Id, apiToken.Name);
    }
}
