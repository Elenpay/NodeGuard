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
