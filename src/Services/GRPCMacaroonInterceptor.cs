using Grpc.Core;
using Grpc.Core.Interceptors;
using NuGet.Packaging;

namespace NodeGuard.Services;

public class GRPCMacaroonInterceptor : Interceptor
{
    private readonly string _macaroon;

    public GRPCMacaroonInterceptor(string macaroon)
    {
        _macaroon = macaroon;
    }

    public override AsyncUnaryCall<TResponse> AsyncUnaryCall<TRequest, TResponse>(
        TRequest request,
        ClientInterceptorContext<TRequest, TResponse> context,
        AsyncUnaryCallContinuation<TRequest, TResponse> continuation)
    {
        var headers = new Metadata();
        if (context.Options.Headers != null)
        {
            headers.AddRange(context.Options.Headers);
        }
        headers.Add("macaroon", _macaroon);

        var newOptions = context.Options.WithHeaders(headers);
        var newContext = new ClientInterceptorContext<TRequest, TResponse>(
            context.Method, context.Host, newOptions);

        return continuation(request, newContext);
    }
}