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
using Microsoft.EntityFrameworkCore;
using NodeGuard.Data.Models;
using Xunit.Abstractions;
using Channel = NodeGuard.Data.Models.Channel;

namespace NodeGuard.Tests.E2E;

/// <summary>
/// E2E: the fee engine categorizes Alice→Bob as SINK under push-heavy load, then FLIPS it to SOURCE as the
/// flow reverses. Drives its own traffic in-process via <see cref="LndTestClient"/> — phase 1 Carol→Alice→Bob
/// pushes OUT, phase 2 Bob→Alice→Carol pulls IN — so it doesn't need a traffic sidecar. Order-agnostic:
/// self-provisions its channels and finds the one it drives by scid. The longest/flakiest e2e (two flow
/// phases + gossip + job cadence).
/// </summary>
[Trait("Category", "E2E")]
[Collection("E2E")]
public class FeeEngineFlowE2ETests : RoutingEngineE2EBase
{
    // Flow knobs (former generate-flow.sh defaults). The ~2.4x pull:push ratio flips SINK→SOURCE; each
    // 25k-sat payment clears the 1M-msat floor.
    private static int PushPayments => int.Parse(Env("FLOW_PUSH_PAYMENTS", "6"));
    private static int PullPayments => int.Parse(Env("FLOW_PULL_PAYMENTS", "14"));
    private static long FlowPaymentSats => long.Parse(Env("FLOW_PAYMENT_SATS", "25000"));
    // bob's sending liquidity to reach before phase 2 pulls; topped up by a DIRECT alice→bob payment (not a
    // forward, so it doesn't touch the categorizer's windows).
    private static long BobTopupSats => long.Parse(Env("FLOW_BOB_TOPUP_SATS", "2000000"));
    private const long AliceCarolLocalSats = 8_000_000; // alice's phase-2 exit hop
    private static long FeeMinChannelSizeSats => long.Parse(Env("ROUTING_ENGINE_FEE_MIN_CHANNEL_SIZE_SATS", "15000000"));

    public FeeEngineFlowE2ETests(ITestOutputHelper output) : base(output)
    {
    }

    [E2EFact, Trait("Speed", "Slow")]
    public async Task FeeEngine_CategorizesSinkThenFlipsToSource_AsFlowReverses()
    {
        var client = CreateClient(out var headers);
        var rpc = CreateBitcoindRpc();

        var nodes = await WaitForNodesAsync(client, headers);
        var aliceNode = nodes.Single(n => n.Name == "alice");
        var bobNode = nodes.Single(n => n.Name == "bob");
        var carolNode = nodes.Single(n => n.Name == "carol");
        _output.WriteLine($"alice={aliceNode.PubKey} bob={bobNode.PubKey} carol={carolNode.PubKey}");

        var alice = LndTestClient.FromEnv("alice", aliceNode.PubKey);
        var bob = LndTestClient.FromEnv("bob", bobNode.PubKey);
        var carol = LndTestClient.FromEnv("carol", carolNode.PubKey);

        try
        {
            await ResetRoutingEngineStateAsync();

            // Enable alice's fee engine — alice is the node whose Alice→Bob channel NodeGuard categorizes.
            await using (var db = CreateDbContext())
            {
                var updated = await db.Nodes
                    .Where(n => n.PubKey == aliceNode.PubKey)
                    .ExecuteUpdateAsync(s => s
                        .SetProperty(n => n.DynamicFeeManagementEnabled, true)
                        .SetProperty(n => n.RoutingEngineDryRun, false));
                updated.Should().Be(1, "alice should be seeded and have its fee engine enabled");
            }

            await using (var db = CreateDbContext())
            {
                var ns = await db.Nodes.AsNoTracking().Select(n => new { n.Id, n.Name }).ToListAsync();
                _output.WriteLine("[diag] nodes: " + string.Join(", ", ns.Select(n => $"{n.Name}=#{n.Id}")));
            }

            // Self-provision the topology FIRST — reuse Alice→Bob if present, else open our own; top up bob;
            // open the Alice→Carol exit hop — so this scenario never depends on another having run first.
            var (aliceBobScid, carolAliceScid, bobAliceScid) =
                await SetUpFlowTopologyAsync(alice, bob, carol, rpc);

            // Find NodeGuard's row for the exact channel we'll drive, by its LND scid (ChanId) — unambiguous
            // even if a second Alice→Bob exists. NodeGuard records externally-opened channels via MonitorChannelsJob.
            var channelId = await PollChannelIdByScidAsync(aliceBobScid);

            await using (var db = CreateDbContext())
            {
                var optedIn = await db.Channels
                    .Where(c => c.Id == channelId)
                    .ExecuteUpdateAsync(s => s.SetProperty(c => c.IsDynamicFeeEnabled, true));
                optedIn.Should().Be(1, "the channel row must exist and be opted in to the fee engine");
            }

            _output.WriteLine($"[flow] PHASE 1 (SINK): {PushPayments} Carol→Alice→Bob payments of {FlowPaymentSats} sats");
            var pushed = 0;
            for (var i = 0; i < PushPayments; i++)
            {
                if (await carol.PayViaScidAsync(bob, carolAliceScid, FlowPaymentSats))
                    _output.WriteLine($"[flow] push {i + 1} OK ({++pushed})");
                else
                    await Task.Delay(TimeSpan.FromSeconds(2));
            }
            pushed.Should().BeGreaterThan(0, "at least one Carol→Alice→Bob push payment must settle to drive SINK");

            var sink = await PollRoutingStateAsync(channelId, aliceNode.PubKey,
                rs => rs is { PeerFlowCategory: PeerFlowCategory.Sink },
                tag: "poll-sink", what: "Alice→Bob categorized as SINK (side 1)", attempts: 60);

            _output.WriteLine($"[sink] netFlow={sink!.NetFlowRatio:0.###} ema={sink.EmaLocalRatio:0.###} target={sink.TargetLocalRatio:0.###} push={sink.PushMsatWindow} pull={sink.PullMsatWindow}");

            // Push-heavy ⇒ positive net-flow, and a SINK's target drifts above 0.5.
            sink.PeerFlowCategory.Should().Be(PeerFlowCategory.Sink);
            sink.NetFlowRatio.Should().BeGreaterThan(0, "outbound-heavy flow on Alice→Bob is a SINK signal");
            sink.TargetLocalRatio.Should().BeGreaterThan(0.5, "a SINK's target ratio drifts upward");

            var sinkFee = await PollFeeAppliedAsync(channelId, aliceNode.PubKey, "ChannelFeeState fee applied (SINK)");
            _output.WriteLine($"[sink-fee] outbound={sinkFee!.LastAppliedOutboundPpm} inbound={sinkFee.LastAppliedInboundPpm}");
            sinkFee.LastAppliedOutboundPpm.Should().NotBeNull("the fee engine should have applied a policy to the SINK channel");

            _output.WriteLine($"[flow] PHASE 2 (SOURCE): {PullPayments} Bob→Alice→Carol payments of {FlowPaymentSats} sats");
            var pulled = 0;
            for (var i = 0; i < PullPayments; i++)
            {
                if (await bob.PayViaScidAsync(carol, bobAliceScid, FlowPaymentSats))
                    _output.WriteLine($"[flow] pull {i + 1} OK ({++pulled})");
                else
                    await Task.Delay(TimeSpan.FromSeconds(2));
            }
            pulled.Should().BeGreaterThan(0, "at least one Bob→Alice→Carol pull payment must settle to drive the flip");

            // Category flips on net-flow crossing, but TargetLocalRatio is a slow EMA — wait for BOTH (a
            // mid-drift ~0.503 would fail the < 0.5 assertion).
            var source = await PollRoutingStateAsync(channelId, aliceNode.PubKey,
                rs => rs is { PeerFlowCategory: PeerFlowCategory.Source } && rs.TargetLocalRatio < 0.5,
                tag: "poll-source", what: "Alice→Bob flipped to SOURCE with target < 0.5 (side 2)", attempts: 90);

            _output.WriteLine($"[source] netFlow={source!.NetFlowRatio:0.###} target={source.TargetLocalRatio:0.###} push={source.PushMsatWindow} pull={source.PullMsatWindow}");

            source.PeerFlowCategory.Should().Be(PeerFlowCategory.Source);
            source.NetFlowRatio.Should().BeLessThan(0, "reversed (inbound-heavy) flow is a SOURCE signal");
            source.TargetLocalRatio.Should().BeLessThan(0.5, "a SOURCE's target drifts downward");
            source.PushMsatWindow.Should().BeGreaterThan(0, "side 1 push flow should still be on record");
            source.PullMsatWindow.Should().BeGreaterThan(0, "side 2 pull flow drove the flip");

            var sourceFee = await PollFeeAppliedAsync(channelId, aliceNode.PubKey, "ChannelFeeState fee applied (SOURCE)");
            _output.WriteLine($"[source-fee] outbound={sourceFee!.LastAppliedOutboundPpm} inbound={sourceFee.LastAppliedInboundPpm}");
            sourceFee.LastAppliedOutboundPpm.Should().NotBeNull("the fee engine should have applied a policy to the categorized channel");
        }
        finally
        {
            await using var db = CreateDbContext();
            await db.Nodes
                .Where(n => n.PubKey == aliceNode.PubKey)
                .ExecuteUpdateAsync(s => s.SetProperty(n => n.DynamicFeeManagementEnabled, false));
        }
    }

    // Ensures the channels the flow needs (order-agnostic): reuse Alice→Bob if present, else open our own
    // with push; top up bob's side; open the Alice→Carol exit hop; settle gossip. Returns the forced
    // first-hop scids for the two phases.
    private async Task<(ulong aliceBobScid, ulong carolAliceScid, ulong bobAliceScid)> SetUpFlowTopologyAsync(
        LndTestClient alice, LndTestClient bob, LndTestClient carol, NBitcoin.RPC.RPCClient rpc)
    {
        // Reuse an Alice→Bob channel only if it clears the fee-engine min channel size — the optimizer skips
        // smaller ones (e.g. the 5M channel the HTLC-reconnect scenario opens), so a smaller reused channel
        // would categorize but never get a fee. Pin the largest that qualifies, else open a fresh 16M one.
        var aliceBobScid = await alice.ScidToAsync(bob.PubKey, minCapacitySats: FeeMinChannelSizeSats);
        if (aliceBobScid is null)
        {
            _output.WriteLine($"[flow] no Alice→Bob >= {FeeMinChannelSizeSats} sat — opening one with push");
            await alice.ConnectAsync(bob.PubKey, $"{bob.Name}:9735");
            await alice.OpenChannelAsync(bob.PubKey, localSats: 16_000_000, pushSats: 8_000_000);
            aliceBobScid = await MineUntilScidAsync(
                rpc, () => alice.ScidToAsync(bob.PubKey, FeeMinChannelSizeSats), "Alice→Bob (>= fee min)");
        }
        _output.WriteLine($"[flow] Alice→Bob scid={aliceBobScid} (>= {FeeMinChannelSizeSats} sat)");

        // Top up bob via a DIRECT alice→bob payment (not a forward, so it doesn't touch the categorizer's
        // windows) until bob has the sending liquidity test needs.
        var bobLocal = await alice.RemoteBalanceOnScidAsync(bob.PubKey, aliceBobScid.Value);
        _output.WriteLine($"[flow] bob local on Alice→Bob = {bobLocal} sat (target {BobTopupSats})");
        for (var i = 0; i < 8 && bobLocal < BobTopupSats; i++)
        {
            if (!await alice.PayViaScidAsync(bob, aliceBobScid.Value, 1_000_000))
                await Task.Delay(TimeSpan.FromSeconds(2));
            bobLocal = await alice.RemoteBalanceOnScidAsync(bob.PubKey, aliceBobScid.Value);
        }
        _output.WriteLine($"[flow] bob local on Alice→Bob = {bobLocal} sat");

        // Alice→Carol: alice's own outbound exit hop for phase 2 (setup's Carol→Alice is carol-owned).
        var aliceToCarol = await alice.LocalBalanceToAsync(carol.PubKey);
        if (aliceToCarol < FlowPaymentSats)
        {
            _output.WriteLine("[flow] opening Alice→Carol (alice outbound exit hop)");
            await alice.ConnectAsync(carol.PubKey, $"{carol.Name}:9735");
            await alice.OpenChannelAsync(carol.PubKey, localSats: AliceCarolLocalSats, pushSats: 0);
            await MineUntilScidAsync(rpc, () => alice.ScidToAsync(carol.PubKey), "Alice→Carol");
            aliceToCarol = await alice.LocalBalanceToAsync(carol.PubKey);
        }
        _output.WriteLine($"[flow] alice outbound to carol = {aliceToCarol} sat");

        // Forced first hops as each SENDER sees them.
        var carolAliceScid = await carol.ScidToAsync(alice.PubKey);
        carolAliceScid.Should().NotBeNull("Carol→Alice scid is needed to force the phase-1 route");
        var bobAliceScid = aliceBobScid.Value;
        _output.WriteLine($"[flow] Carol→Alice scid={carolAliceScid} Bob→Alice scid={bobAliceScid}");

        await Task.Delay(TimeSpan.FromSeconds(15)); // gossip settle so senders can build the two-hop routes
        return (aliceBobScid.Value, carolAliceScid!.Value, bobAliceScid);
    }

    // Mines until readScid returns a confirmed scid (channel active), or throws.
    private async Task<ulong> MineUntilScidAsync(NBitcoin.RPC.RPCClient rpc, Func<Task<ulong?>> readScid, string what)
    {
        await MineAsync(rpc, 6);
        for (var i = 0; i < 60; i++)
        {
            var scid = await readScid();
            if (scid is not null) return scid.Value;
            await MineAsync(rpc, 1);
            await Task.Delay(TimeSpan.FromSeconds(3));
        }
        throw new InvalidOperationException($"{what} never got a confirmed scid after mining");
    }
}
