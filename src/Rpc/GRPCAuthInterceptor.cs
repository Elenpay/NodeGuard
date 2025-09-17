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
