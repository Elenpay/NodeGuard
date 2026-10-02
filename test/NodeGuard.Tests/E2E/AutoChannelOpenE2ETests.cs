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
using Lnrpc;
using Microsoft.EntityFrameworkCore;
using NodeGuard.Data.Models;
using NodeGuard.Helpers;
using Xunit.Abstractions;

namespace NodeGuard.Tests.E2E;

/// <summary>
/// E2E: demand-driven channel opening, from real refused forwards to a confirmed channel. Drains bob's
/// outbound to carol, has alice route payments alice→bob→carol so bob's link refuses them with
/// INSUFFICIENT_BALANCE, then switches bob to <see cref="AutoChannelOpenMode.Auto"/> on the hot wallet and
/// asserts on what <c>AutoChannelOpenJob</c> decided: the recommendation's evidence and sizing, the request
/// it promoted, and the new Bob→Carol channel opening at the price bob already charges carol.
/// Order-agnostic and self-resetting; serial with the other e2e classes via <c>[Collection("E2E")]</c>.
/// </summary>
[Trait("Category", "E2E")]
[Collection("E2E")]
public class AutoChannelOpenE2ETests : RoutingEngineE2EBase
{
    // Every refused payment is this size, so MaxBurstPaymentSats and MissedSats are exact
    private const long RefusedPaymentSats = 1_000_000;
    // AUTO_CHANNEL_OPEN_MIN_BURSTS: the fewest that still reads as repeated demand
    private const int Bursts = 2;

    // Identical retries inside a burst: the planner must collapse them, so FailedAttempts != Bursts
    private const int AttemptsPerBurst = 2;

    // Per channel, well under one refused payment, so bob can't serve any of them on any channel
    private const long ResidualUsableSats = 200_000;
    // Larger than any bob→carol balance, so the drain is one payment unless it has to halve
    private const long DrainChunkSats = 50_000_000;
    private const long MinDrainPaymentSats = 10_000;

    // Mirrors ChannelOpenInitiatorService.ReserveFraction
    private const double ReserveFraction = 0.01;

    // Neutralises the profitability gate: its chain cost comes from the mainnet mempool feed, not regtest
    private const double CostToEarnRatio = 1_000;
    private const long BudgetSats = 50_000_000;

    private const int JobWaitAttempts = 150; // 5 min of 2s polls; the job runs every ROUTING_ENGINE_JOB_INTERVAL_SECONDS

    public AutoChannelOpenE2ETests(ITestOutputHelper output) : base(output)
    {
    }

    [E2EFact, Trait("Speed", "Slow")]
    public async Task AutoChannelOpen_RefusedForwardsToDrainedPeer_OpensSecondChannelAtInheritedFee()
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
        var walletId = int.Parse(Env("E2E_HOT_WALLET_ID", "3"));

        int bobNodeId, carolNodeId;
        bool bobDynamicFees;
        await using (var db = CreateDbContext())
        {
            var bobRow = await db.Nodes.AsNoTracking().SingleAsync(n => n.PubKey == bobNode.PubKey);
            bobNodeId = bobRow.Id;
            bobDynamicFees = bobRow.DynamicFeeManagementEnabled;
            carolNodeId = await db.Nodes.AsNoTracking().Where(n => n.PubKey == carolNode.PubKey)
                .Select(n => n.Id).SingleAsync();
        }

        try
        {
            await ResetAutoChannelOpenAsync(bobNodeId, dynamicFees: false);

            var aliceToBobScid = await ResolveAliceToBobAsync(client, headers, rpc, alice, bobNode.PubKey);
            _output.WriteLine($"[setup] alice→bob first hop scid {aliceToBobScid}");

            await DrainUsableLocalAsync(bob, carol);

            // After the drain, so bob's own settled sends to carol can't read as settled outbound demand
            await Task.Delay(TimeSpan.FromSeconds(3));
            await ResetRoutingEngineStateAsync();

            // The planner prices by the dearest of bob's carol channels, off FeeReport
            var carolScids = (await bob.ChannelsToAsync(carolNode.PubKey)).Select(c => c.ChanId).ToHashSet();
            var policies = new List<ChannelFeeReport>();
            foreach (var scid in carolScids)
            {
                var policy = await bob.OutboundPolicyAsync(scid);
                if (policy != null) policies.Add(policy);
            }
            var dearest = policies.MaxBy(p => p.FeePerMil);
            dearest.Should().NotBeNull("bob must report an outbound policy toward carol, or nothing can be priced");
            dearest!.FeePerMil.Should().BePositive("a zero-ppm peer earns nothing and is never planned");
            _output.WriteLine($"[setup] bob's dearest carol policy: {dearest.FeePerMil} ppm / {dearest.BaseFeeMsat} msat on scid {dearest.ChanId}");

            await SendRefusedBurstsAsync(alice, bob, carol, aliceToBobScid);

            var failures = await PollAsync(
                async () =>
                {
                    await using var db = CreateDbContext();
                    return await db.ForwardingHtlcEvents.AsNoTracking()
                        .Where(e => e.ManagedNodePubKey == bobNode.PubKey
                                    && e.EventType == HtlcEventType.Forward
                                    && e.EventCase == HtlcEventCase.LinkFailEvent
                                    && e.FailureDetail == (int)Routerrpc.FailureDetail.InsufficientBalance)
                        .ToListAsync();
                },
                rows => rows.Count >= Bursts * AttemptsPerBurst,
                attempts: 20, delay: TimeSpan.FromSeconds(3),
                what: "NodeHtlcSubscribeJob recorded bob's refused forwards");
            failures.Should().HaveCount(Bursts * AttemptsPerBurst, "every refused attempt is one LinkFailEvent row");
            failures.Should().OnlyContain(e => e.OutgoingAmountMsat == (ulong)RefusedPaymentSats * 1_000);
            failures.Should().OnlyContain(e => carolScids.Contains(e.OutgoingChannelId),
                "the refusing link is one of bob's channels to carol");

            var eventPpm = failures.Max(e => e.RoutingFeePpm) ?? dearest.FeePerMil;
            var expectedMissedFeeMsat = Bursts * (RefusedPaymentSats * 1_000 * eventPpm / 1_000_000);
            expectedMissedFeeMsat.Should().BeGreaterThanOrEqualTo(Constants.AUTO_CHANNEL_OPEN_MIN_MISSED_FEE_MSAT,
                "the shaped demand must clear the fee gate, or there is nothing for the job to plan");

            // Earlier scenarios leave unconfirmed change in the hot wallet; with no spendable coin the first
            // promotion fails and a later cycle re-promotes the same row onto a new request
            await MineAsync(rpc, 6);

            // Only now, so no run can see a partial burst set and plan off it
            await using (var db = CreateDbContext())
            {
                var updated = await db.Nodes
                    .Where(n => n.Id == bobNodeId)
                    .ExecuteUpdateAsync(s => s
                        .SetProperty(n => n.AutoChannelOpenEnabled, true)
                        .SetProperty(n => n.AutoChannelOpenMode, AutoChannelOpenMode.Auto)
                        .SetProperty(n => n.AutoChannelOpenWalletId, (int?)walletId)
                        .SetProperty(n => n.AutoChannelOpenBudgetSats, (long?)BudgetSats)
                        // Null start ⇒ a fresh period, so opens from other scenarios don't count against it
                        .SetProperty(n => n.AutoChannelOpenBudgetStartDatetime, (DateTimeOffset?)null)
                        .SetProperty(n => n.AutoChannelOpenBudgetRefreshInterval, (TimeSpan?)null)
                        .SetProperty(n => n.AutoChannelOpenMinSizeSats, (long?)null)
                        .SetProperty(n => n.AutoChannelOpenMaxSizeSats, (long?)null)
                        .SetProperty(n => n.MaxChannelOpenCostToEarnRatio, (double?)CostToEarnRatio)
                        .SetProperty(n => n.RoutingEngineDryRun, false));
                updated.Should().Be(1, "bob should be seeded and have automatic channel opening switched on");
            }

            ChannelOpenRecommendation? recommendation = null;
            ChannelOperationRequestStatus? requestStatus = null;
            for (var attempt = 0; attempt < JobWaitAttempts; attempt++)
            {
                await using (var db = CreateDbContext())
                {
                    recommendation = await db.ChannelOpenRecommendations.AsNoTracking()
                        .FirstOrDefaultAsync(r => r.NodeId == bobNodeId && r.PeerPubKey == carolNode.PubKey);
                    requestStatus = recommendation?.ChannelOperationRequestId is { } id
                        ? await db.ChannelOperationRequests.AsNoTracking().Where(r => r.Id == id)
                            .Select(r => (ChannelOperationRequestStatus?)r.Status).SingleOrDefaultAsync()
                        : null;
                }

                if (recommendation != null)
                    _output.WriteLine(
                        $"[recommendation] #{recommendation.Id} status={recommendation.Status} bursts={recommendation.Bursts} capacity={recommendation.SuggestedCapacitySats} request={recommendation.ChannelOperationRequestId} requestStatus={requestStatus}");

                // A request that failed before dispatch gets replaced on a later cycle, so wait for a live one
                if (recommendation is { Status: ChannelOpenRecommendationStatus.Promoted }
                    && requestStatus is not ChannelOperationRequestStatus.Failed) break;

                await Task.Delay(TimeSpan.FromSeconds(2));
            }

            recommendation.Should().NotBeNull("AutoChannelOpenJob should have recommended a channel to the drained peer");
            recommendation!.Status.Should().Be(ChannelOpenRecommendationStatus.Promoted, "Auto mode promotes what it recommends");
            requestStatus.Should().NotBe(ChannelOperationRequestStatus.Failed,
                "the promoted request must reach dispatch — check NodeGuard's log for the promotion failure reason");

            // Evidence: each payment tried twice, all refused
            recommendation.Bursts.Should().Be(Bursts);
            recommendation.FailedAttempts.Should().Be(Bursts * AttemptsPerBurst);
            recommendation.MissedSats.Should().Be(Bursts * RefusedPaymentSats, "identical retries collapse into one payment");
            recommendation.MaxBurstPaymentSats.Should().Be(RefusedPaymentSats);
            recommendation.MissedFeeMsat.Should().Be(expectedMissedFeeMsat);
            carolScids.Should().Contain(recommendation.OutgoingChannelId);
            recommendation.InheritedFeeRatePpm.Should().Be(dearest.FeePerMil, "the channel opens at the most bob already charges carol");
            recommendation.InheritedBaseFeeMsat.Should().Be(dearest.BaseFeeMsat, "base fee comes off the same channel as the rate");

            var (expectedCapacity, expectedRegime) = ExpectedSize(recommendation);
            _output.WriteLine($"[sizing] out={recommendation.OutSats} in={recommendation.InSats} missed={recommendation.MissedSats} → {expectedCapacity} sats ({expectedRegime})");
            recommendation.BindingClamp.Should().Be(ChannelOpenBindingClamp.None,
                $"nothing should bind a {expectedCapacity} sat channel — a WalletBalance clamp means hot wallet #{walletId} is short");
            recommendation.Regime.Should().Be(expectedRegime);
            recommendation.SuggestedCapacitySats.Should().Be(expectedCapacity);

            recommendation.ChannelOperationRequestId.Should().NotBeNull("promotion links the request it created");
            var requestId = recommendation.ChannelOperationRequestId!.Value;

            await using (var db = CreateDbContext())
            {
                var audit = await db.AuditLogs.AsNoTracking()
                    .Where(a => a.ActionType == AuditActionType.Create
                                && a.ObjectAffected == AuditObjectType.ChannelOpenRecommendation
                                && a.ObjectId == recommendation.Id.ToString())
                    .OrderByDescending(a => a.Id)
                    .FirstOrDefaultAsync();
                audit.Should().NotBeNull("the job audits every recommendation it records");
                audit!.Details.Should().Contain(carolNode.PubKey);

                var request = await db.ChannelOperationRequests.AsNoTracking().SingleAsync(r => r.Id == requestId);
                request.RequestType.Should().Be(OperationRequestType.Open);
                request.SourceNodeId.Should().Be(bobNodeId);
                request.DestNodeId.Should().Be(carolNodeId);
                request.WalletId.Should().Be(walletId);
                request.SatsAmount.Should().Be(recommendation.SuggestedCapacitySats, "Auto promotes at the suggested capacity");
                request.UserId.Should().BeNull("the job promoted it, not an operator");
                request.InitialChannelFeeRatePpm.Should().Be(recommendation.InheritedFeeRatePpm);
                request.InitialChannelBaseFeeMsat.Should().Be(recommendation.InheritedBaseFeeMsat);
            }

            // A hot wallet needs no signatures, so promotion dispatched ChannelOpenJob already
            var channelId = (int)await MineUntilChannelOpenedAsync(client, headers, rpc, requestId);

            ulong newScid;
            await using (var db = CreateDbContext())
            {
                newScid = await db.Channels.AsNoTracking().Where(c => c.Id == channelId).Select(c => c.ChanId).SingleAsync();
            }
            carolScids.Should().NotContain(newScid, "the open buys a second channel, not one we already had");

            var opened = await PollAsync(
                async () => (await bob.ChannelsToAsync(carolNode.PubKey)).FirstOrDefault(c => c.ChanId == newScid),
                c => c is { Active: true },
                attempts: 20, delay: TimeSpan.FromSeconds(3),
                what: $"bob's new channel {newScid} to carol is active");
            opened!.Capacity.Should().Be(recommendation.SuggestedCapacitySats);
            opened.Initiator.Should().BeTrue("bob funded it");

            var newPolicy = await bob.OutboundPolicyAsync(newScid);
            newPolicy.Should().NotBeNull();
            newPolicy!.FeePerMil.Should().Be(recommendation.InheritedFeeRatePpm!.Value, "the new channel opens at the inherited rate");
            newPolicy.BaseFeeMsat.Should().Be(recommendation.InheritedBaseFeeMsat!.Value);
            _output.WriteLine($"[result] channel #{channelId} scid {newScid}: {opened.Capacity} sats at {newPolicy.FeePerMil} ppm / {newPolicy.BaseFeeMsat} msat");
        }
        finally
        {
            await ResetAutoChannelOpenAsync(bobNodeId, dynamicFees: bobDynamicFees);
        }
    }

    /// <summary>
    /// Switches bob's channel opener off and clears his recommendations — on the way in so an earlier run's
    /// Promoted row can't put carol on cooldown, and on the way out so nothing opens under the next scenario.
    /// The fee engine stays off during the run so bob's price toward carol can't move under the assertions.
    /// </summary>
    private static async Task ResetAutoChannelOpenAsync(int nodeId, bool dynamicFees)
    {
        await using var db = CreateDbContext();
        await db.Nodes
            .Where(n => n.Id == nodeId)
            .ExecuteUpdateAsync(s => s
                .SetProperty(n => n.AutoChannelOpenEnabled, false)
                .SetProperty(n => n.AutoChannelOpenMode, AutoChannelOpenMode.Recommendation)
                .SetProperty(n => n.DynamicFeeManagementEnabled, dynamicFees));
        await db.ChannelOpenRecommendations.Where(r => r.NodeId == nodeId).ExecuteDeleteAsync();
    }

    /// <summary>
    /// Alice's first hop must carry every refused payment plus bob's fee, so the refusal lands on bob's
    /// outgoing link and never on alice's. Opens one through NodeGuard if none qualifies.
    /// </summary>
    private async Task<ulong> ResolveAliceToBobAsync(
        Nodeguard.NodeGuardService.NodeGuardServiceClient client, Grpc.Core.Metadata headers,
        NBitcoin.RPC.RPCClient rpc, LndTestClient alice, string bobPubKey)
    {
        var minLocal = (long)(RefusedPaymentSats * 1.05);
        var existing = (await alice.ChannelsToAsync(bobPubKey))
            .Where(c => c.Active && c.ChanId != 0 && Usable(c) >= minLocal)
            .MaxBy(c => c.LocalBalance);
        if (existing != null) return existing.ChanId;

        _output.WriteLine($"[setup] no Alice→Bob with {minLocal} sats usable — opening one through NodeGuard");
        var openedId = (int)await OpenChannelAndConfirmAsync(client, headers, rpc, alice.PubKey, bobPubKey);
        await using var db = CreateDbContext();
        return await db.Channels.AsNoTracking().Where(c => c.Id == openedId).Select(c => c.ChanId).SingleAsync();
    }

    /// <summary>
    /// Pays carol directly from bob until EVERY channel between them is down to
    /// <see cref="ResidualUsableSats"/> usable. Non-strict forwarding tries all of them, so one with
    /// balance left would serve the payment and nothing would be refused.
    /// </summary>
    private async Task DrainUsableLocalAsync(LndTestClient from, LndTestClient to, int maxPayments = 60)
    {
        for (var i = 0; i < maxPayments; i++)
        {
            var channels = (await from.ChannelsToAsync(to.PubKey)).Where(c => c.Active && c.ChanId != 0).ToList();
            channels.Should().NotBeEmpty($"{from.Name} needs a channel to {to.Name} for its link to refuse anything");

            var fullest = channels.MaxBy(Usable)!;
            _output.WriteLine($"[drain] {from.Name}→{to.Name}: {channels.Count} channel(s), fullest scid {fullest.ChanId} usable {Usable(fullest)}");
            if (Usable(fullest) <= ResidualUsableSats) return;

            var amount = Math.Min(Usable(fullest) - ResidualUsableSats / 2, DrainChunkSats);
            while (amount >= MinDrainPaymentSats && !await from.PayViaScidAsync(to, fullest.ChanId, amount))
            {
                // Commitment fee and anchor reserve make the exact headroom fuzzy, so back off and retry
                _output.WriteLine($"[drain] {amount} sat payment over scid {fullest.ChanId} failed — halving");
                amount /= 2;
            }
        }

        throw new InvalidOperationException(
            $"{from.Name}→{to.Name} never drained to {ResidualUsableSats} usable sats within {maxPayments} payments");
    }

    /// <summary>
    /// Bursts of identical attempts over alice→bob→carol, each refused at bob's link. Attempts inside a
    /// burst go back to back, well under the burst gap; bursts sit a margin over it.
    /// </summary>
    private async Task SendRefusedBurstsAsync(LndTestClient alice, LndTestClient bob, LndTestClient carol, ulong firstHopScid)
    {
        var interBurstDelay = TimeSpan.FromSeconds(Constants.AUTO_CHANNEL_OPEN_BURST_GAP_SECONDS + 2);
        for (var burst = 0; burst < Bursts; burst++)
        {
            if (burst > 0) await Task.Delay(interBurstDelay);

            for (var attempt = 0; attempt < AttemptsPerBurst; attempt++)
            {
                var htlc = await alice.SendToRouteAsync(carol, firstHopScid, [bob.PubKey, carol.PubKey], RefusedPaymentSats);
                _output.WriteLine($"[refuse] burst {burst} attempt {attempt}: status={htlc.Status} code={htlc.Failure?.Code} source={htlc.Failure?.FailureSourceIndex}");

                htlc.Status.Should().Be(HTLCAttempt.Types.HTLCStatus.Failed, "bob has no outbound to carol left");
                htlc.Failure.Should().NotBeNull();
                htlc.Failure!.Code.Should().Be(Failure.Types.FailureCode.TemporaryChannelFailure,
                    "an insufficient-balance refusal — FEE_INSUFFICIENT here means alice's gossip view of bob's policy is stale");
                htlc.Failure.FailureSourceIndex.Should().Be(1u, "the refusal comes from bob, the first hop");
            }
        }
    }

    /// <summary>
    /// The sizing rule from docs/auto-channel-open.md §5, evaluated on the row's own evidence. Clamps are
    /// left out on purpose; the test asserts none of them bound.
    /// </summary>
    private static (long Capacity, ChannelOpenSizingRegime Regime) ExpectedSize(ChannelOpenRecommendation r)
    {
        var windowDays = Math.Max(Constants.AUTO_CHANNEL_OPEN_WINDOW_HOURS / 24.0, 1.0 / 24.0);
        var netDrainPerDay = Math.Max(0, (r.OutSats + r.MissedSats) / windowDays - r.InSats / windowDays);
        var runway = (long)(netDrainPerDay * Constants.AUTO_CHANNEL_OPEN_TARGET_RUNWAY_DAYS);
        var floor = (long)(r.MaxBurstPaymentSats * Constants.AUTO_CHANNEL_OPEN_BUFFER_MULTIPLIER);

        var regime = runway > floor ? ChannelOpenSizingRegime.Draining : ChannelOpenSizingRegime.Buffer;
        return ((long)(Math.Max(runway, floor) / (1 - ReserveFraction)), regime);
    }

    // Same quantity as the job's LocalChanReserveSat, off the non-deprecated field
    private static long Usable(Lnrpc.Channel c)
        => Math.Max(0, c.LocalBalance - (long)(c.LocalConstraints?.ChanReserveSat ?? 0));
}
