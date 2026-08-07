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
/// Theory counterpart to <see cref="E2EFactAttribute"/> — same gating (RUN_E2E_TESTS=1 or a
/// reachable NodeGuard gRPC endpoint), but for parameterized [Theory]/[InlineData] tests.
/// </summary>
public sealed class E2ETheoryAttribute : TheoryAttribute
{
    public E2ETheoryAttribute()
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
