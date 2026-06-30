/*
 * NodeGuard
 * Copyright (C) 2023  Elenpay
 *
 * This program is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Affero General Public License as published by
 * the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 *
 * This program is distributed in the hope that it will be useful,
 * but WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
 * GNU Affero General Public License for more details.
 *
 * You should have received a copy of the GNU Affero General Public License
 * along with this program.  If not, see http://www.gnu.org/licenses/.
 *
 */

using System.Net.Sockets;

namespace NodeGuard.Tests.E2E;

/// <summary>
/// Gates e2e tests so they RUN when a NodeGuard gRPC server is reachable (so a normal
/// <c>dotnet test</c> / VSCode Test Explorer run auto-executes them whenever a local NodeGuard
/// is up on 50051), and otherwise report as <b>Skipped</b> rather than failing.
///
/// Runs when EITHER:
///   • <c>RUN_E2E_TESTS=1</c> (explicit force — used by CI and the e2e container), OR
///   • the gRPC endpoint (<c>NODEGUARD_GRPC_ENDPOINT</c>, default http://localhost:50051) accepts a TCP connection.
/// </summary>
public sealed class E2EFactAttribute : FactAttribute
{
    public E2EFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("RUN_E2E_TESTS") == "1") return;
        if (NodeGuardReachable()) return;
        Skip = "No NodeGuard gRPC reachable (NODEGUARD_GRPC_ENDPOINT, default localhost:50051) and RUN_E2E_TESTS != 1 — skipping e2e.";
    }

    private static bool NodeGuardReachable()
    {
        try
        {
            var endpoint = Environment.GetEnvironmentVariable("NODEGUARD_GRPC_ENDPOINT") ?? "http://localhost:50051";
            var uri = new Uri(endpoint);
            using var client = new TcpClient();
            return client.ConnectAsync(uri.Host, uri.Port).Wait(TimeSpan.FromMilliseconds(500)) && client.Connected;
        }
        catch
        {
            return false;
        }
    }
}
