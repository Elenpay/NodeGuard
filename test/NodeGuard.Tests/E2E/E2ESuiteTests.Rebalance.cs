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
using Xunit.Sdk;

namespace NodeGuard.Tests.E2E;

/// <summary>
/// The full container e2e suite (LIVE NodeGuard + real LND + Postgres) in one ordered <c>dotnet test</c>
/// pass; shared plumbing in <see cref="E2ETestBase"/>, order pinned by <see cref="PriorityOrderer"/>. The
/// three scenarios share one Alice→Bob channel, so they're one <c>partial</c> class split by scenario:
/// (1) open + rebalance (here), (2) fee-engine smoke, (3) fee-engine flow. (3) runs last — it reuses (1)'s
/// channel and its traffic would starve (1)'s route. The stack enables the routing engine (real LND fee
/// writes) on a seconds cadence with lowered categorization gates so a channel can flip within one run.
///
/// Extra env: NODEGUARD_DB_CONNECTIONSTRING; ALICE_/BOB_/CAROL_HOST + _MACAROON (scenario 3, extract-env.sh).
/// </summary>
[Trait("Category", "E2E")]
[TestCaseOrderer(PriorityOrderer.TypeName, PriorityOrderer.AssemblyName)]
public partial class E2ESuiteTests : E2ETestBase
{
    public E2ESuiteTests(ITestOutputHelper output) : base(output)
    {
    }

    // (1) Open a channel via gRPC, then circular-rebalance over it.
    [E2EFact, TestPriority(1)]
    public async Task OpenChannelViaGrpc_ThenCircularRebalance_Succeeds()
    {
        var client = CreateClient(out var headers);
        var rpc = CreateBitcoindRpc();

        var nodes = await WaitForNodesAsync(client, headers);
        var alice = nodes.Single(n => n.Name == "alice");
        var bob = nodes.Single(n => n.Name == "bob");
        var carol = nodes.Single(n => n.Name == "carol");
        _output.WriteLine($"alice={alice.PubKey} bob={bob.PubKey} carol={carol.PubKey}");

        // Open Alice→Bob through NodeGuard (option B — also covers channel opening), confirmed and active.
        var channelId = await OpenChannelAndConfirmAsync(client, headers, rpc, alice.PubKey, bob.PubKey);

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
