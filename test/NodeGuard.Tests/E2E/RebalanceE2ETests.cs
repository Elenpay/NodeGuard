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

using FluentAssertions;
using Nodeguard;
using Xunit.Abstractions;

namespace NodeGuard.Tests.E2E;

/// <summary>
/// True end-to-end test driven entirely from .NET (no grpcurl/curl): a generated gRPC client drives a
/// LIVE NodeGuard instance through the whole option-B flow — GetNodes → OpenChannel(Alice→Bob) →
/// mine + poll until the channel confirms → RequestRebalance(Alice→Bob→Carol→Alice) → assert success.
/// This exercises gRPC auth, channel opening (wallet → PSBT → internal signing → broadcast), channel
/// sync, and the amountless-invoice rebalance against real LND + Postgres. Shared plumbing lives in
/// <see cref="E2ETestBase"/>.
/// </summary>
[Trait("Category", "E2E")]
public class RebalanceE2ETests : E2ETestBase
{
    public RebalanceE2ETests(ITestOutputHelper output) : base(output)
    {
    }

    [E2EFact]
    public async Task OpenChannelViaGrpc_ThenCircularRebalance_Succeeds()
    {
        var client = CreateClient(out var headers);
        var rpc = CreateBitcoindRpc();

        // Wait for NodeGuard to be up and to have seeded the three nodes.
        var nodes = await WaitForNodesAsync(client, headers);
        var alice = nodes.Single(n => n.Name == "alice");
        var bob = nodes.Single(n => n.Name == "bob");
        var carol = nodes.Single(n => n.Name == "carol");
        _output.WriteLine($"alice={alice.PubKey} bob={bob.PubKey} carol={carol.PubKey}");

        // Open Alice→Bob THROUGH NodeGuard (option B), confirmed and active.
        var channelId = await OpenChannelAndConfirmAsync(client, headers, rpc, alice.PubKey, bob.PubKey);

        // Circular rebalance Alice→Bob→Carol→Alice over the just-opened channel.
        var resp = await client.RequestRebalanceAsync(new RequestRebalanceRequest
        {
            NodePubkey = alice.PubKey,
            SourceChannelId = (int)channelId,
            TargetPubkey = carol.PubKey,
            AmountSats = 500_000,
            MaxFeePct = 0.2,
            MaxAttempts = 1,
        }, headers);

        _output.WriteLine($"rebalance {resp.RebalanceId}: status={resp.Status} feeSats={resp.FeePaidSats} ppm={resp.EffectivePpm}");
        resp.Status.Should().Be(REBALANCE_STATUS.RebalanceSucceeded);
        resp.FeePaidSats.Should().BeGreaterThan(0);
        resp.FeePaidSats.Should().BeLessThanOrEqualTo(1_000); // within 0.2% of 500k
    }
}
