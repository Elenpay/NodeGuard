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
using NodeGuard.Data.Models;

namespace NodeGuard.Services;

public class ChannelOpenInitiatorServiceTests
{
    private static readonly DateTimeOffset T0 = new(2026, 9, 18, 12, 0, 0, TimeSpan.Zero);

    private static ChannelOpenInitiatorTunables Tunables(
        int minBursts = 5,
        long minMissedFeeMsat = 2_000_000,
        double bufferMultiplier = 3.0,
        int targetRunwayDays = 2,
        double costToEarnRatio = 0.5,
        bool allowWumbo = false,
        long nodeMin = 500_000,
        long nodeMax = 16_000_000,
        long walletBalance = 50_000_000,
        long remainingBudget = 20_000_000,
        long chainCost = 2_540,
        int maxPlans = 1)
        => new(
            WindowHours: 24,
            BurstGapSeconds: 120,
            MinBursts: minBursts,
            MinMissedFeeMsat: minMissedFeeMsat,
            BufferMultiplier: bufferMultiplier,
            TargetRunwayDays: targetRunwayDays,
            MaxCostToEarnRatio: costToEarnRatio,
            NonWumboMaxSats: 16_777_215,
            AllowWumbo: allowWumbo,
            NodeMinSizeSats: nodeMin,
            NodeMaxSizeSats: nodeMax,
            WalletSpendableSats: walletBalance,
            RemainingBudgetSats: remainingBudget,
            EstimatedChainCostSats: chainCost,
            MaxPlansPerRun: maxPlans);

    private const string PEER = "03ab";

    private static PeerDemandFailure Fail(
        ulong outChan, int secondsFromStart, long amountSats, long ppm = 900, string peer = PEER, long? feeMsat = null)
        => new(peer, outChan, T0.AddSeconds(secondsFromStart), (ulong)(amountSats * 1000), ppm, feeMsat);

    private static PeerDemandContext Context(
        string peer = PEER,
        long usableLocalSats = 120_000,
        long outSats = 5_000_000,
        long inSats = 3_200_000,
        bool cooldown = false,
        long? ppm = 900,
        long baseFeeMsat = 1_000)
        => new(peer, "peerA", usableLocalSats, outSats, inSats,
            ppm is null ? null : new LocalOutboundPolicy(ppm.Value, baseFeeMsat), cooldown);

    private static Dictionary<string, PeerDemandContext> Contexts(params PeerDemandContext[] c)
        => c.ToDictionary(x => x.PeerPubKey, x => x);

    private static ChannelOpenClassification Plan(
        IEnumerable<PeerDemandFailure> failures,
        Dictionary<string, PeerDemandContext> contexts,
        ChannelOpenInitiatorTunables t)
        => ChannelOpenInitiatorService.BuildPlans(failures.ToLookup(x => x.PeerPubKey), contexts, t);

    // ── Bursts ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public void CollapseBursts_IdenticalRetriesWithinGap_CollapseToOnePayment()
    {
        // Same amount three times, seconds apart: one payment retried, not three payments.
        var rows = new[]
        {
            Fail(1, 0, 500_000),
            Fail(1, 2, 500_000),
            Fail(1, 4, 500_000)
        };

        var bursts = ChannelOpenInitiatorService.CollapseBursts(rows, 120, null);

        bursts.Should().ContainSingle();
        bursts[0].PaymentSizeSats.Should().Be(500_000);
    }

    [Fact]
    public void CollapseBursts_DistinctShardsWithinGap_SumIntoOnePayment()
    {
        // MPP: different amounts arriving together are parts of one payment, so they sum.
        var rows = new[]
        {
            Fail(1, 0, 300_000),
            Fail(1, 1, 200_000),
            Fail(1, 2, 400_000)
        };

        var bursts = ChannelOpenInitiatorService.CollapseBursts(rows, 120, null);

        bursts.Should().ContainSingle();
        bursts[0].PaymentSizeSats.Should().Be(900_000);
    }

    [Fact]
    public void CollapseBursts_BeyondGap_StartsANewPayment()
    {
        var rows = new[]
        {
            Fail(1, 0, 500_000),
            Fail(1, 500, 500_000)
        };

        var bursts = ChannelOpenInitiatorService.CollapseBursts(rows, 120, null);

        bursts.Should().HaveCount(2);
    }

    [Fact]
    public void CollapseBursts_SmallPayment_StillAccruesFee()
    {
        // Guards the msat/ppm ordering: dividing before multiplying truncates small payments to zero.
        var rows = new[] { Fail(1, 0, 1_000, ppm: 900) };

        var bursts = ChannelOpenInitiatorService.CollapseBursts(rows, 120, null);

        bursts[0].MissedFeeMsat.Should().Be(900);
    }

    [Fact]
    public void CollapseBursts_NoPpmOnEvent_FallsBackToChannelPolicy()
    {
        var rows = new[] { new PeerDemandFailure(PEER, 1, T0, 1_000_000_000, null, null) };

        var bursts = ChannelOpenInitiatorService.CollapseBursts(rows, 120, fallbackPpm: 500);

        bursts[0].MissedFeeMsat.Should().Be(500_000);
    }

    [Fact]
    public void CollapseBursts_OfferedFeeOnEvent_WinsOverPpmEstimate()
    {
        // 1M sats at 900 ppm would estimate 900,000 msat; the sender actually offered base fee on top.
        var rows = new[] { Fail(1, 0, 1_000_000, ppm: 900, feeMsat: 901_000) };

        var bursts = ChannelOpenInitiatorService.CollapseBursts(rows, 120, null);

        bursts[0].MissedFeeMsat.Should().Be(901_000);
    }

    [Fact]
    public void CollapseBursts_OfferedFee_CountedOncePerDistinctAmount()
    {
        // Identical retries carry the same fee and must not multiply it; a distinct shard adds its own.
        var rows = new[]
        {
            Fail(1, 0, 500_000, feeMsat: 451_000),
            Fail(1, 2, 500_000, feeMsat: 451_000),
            Fail(1, 4, 300_000, feeMsat: 271_000)
        };

        var bursts = ChannelOpenInitiatorService.CollapseBursts(rows, 120, null);

        bursts.Should().ContainSingle();
        bursts[0].MissedFeeMsat.Should().Be(722_000);
    }

    [Fact]
    public void CollapseBursts_MixedOfferedAndMissingFee_FallsBackPerAmount()
    {
        var rows = new[]
        {
            Fail(1, 0, 500_000, ppm: 900, feeMsat: 451_000),
            Fail(1, 1, 200_000, ppm: 900)
        };

        var bursts = ChannelOpenInitiatorService.CollapseBursts(rows, 120, null);

        bursts[0].MissedFeeMsat.Should().Be(451_000 + 180_000);
    }

    // ── Gate ───────────────────────────────────────────────────────────────────────────

    [Fact]
    public void BuildPlans_OneFatPaymentRetried_IsRejected()
    {
        // High missed fee but a single burst: a payment problem, not a route problem.
        var rows = Enumerable.Range(0, 20).Select(i => Fail(1, i, 5_000_000)).ToList();

        var result = Plan(rows, Contexts(Context()), Tunables());

        result.Plans.Should().BeEmpty();
        result.Rejected.Should().ContainSingle().Which.Reason.Should().Contain("burst");
    }

    [Fact]
    public void BuildPlans_ManyCheapPayments_IsRejected()
    {
        var rows = Enumerable.Range(0, 8)
            .Select(i => Fail(1, i * 300, 1_000, ppm: 1))
            .ToList();

        var result = Plan(rows, Contexts(Context()), Tunables());

        result.Plans.Should().BeEmpty();
        result.Rejected.Should().ContainSingle().Which.Reason.Should().Contain("Missed fee");
    }

    [Fact]
    public void BuildPlans_PeerCanAlreadyCoverTheRefusedPayment_IsRejectedDespiteHistory()
    {
        // Refused payments are 500k; 2M usable would have served them, so capacity was never the problem.
        var rows = SufficientDemand();

        var result = Plan(rows, Contexts(Context(usableLocalSats: 2_000_000)), Tunables());

        result.Plans.Should().BeEmpty();
        result.Rejected.Should().ContainSingle().Which.Reason.Should().Contain("not currently constrained");
    }

    [Fact]
    public void BuildPlans_PeerOnCooldown_IsRejected()
    {
        var result = Plan(SufficientDemand(), Contexts(Context(cooldown: true)), Tunables());

        result.Plans.Should().BeEmpty();
        result.Rejected.Should().ContainSingle().Which.Reason.Should().Contain("cooldown");
    }

    [Fact]
    public void BuildPlans_NoContextForPeer_IsRejected()
    {
        var result = Plan(SufficientDemand(), new Dictionary<string, PeerDemandContext>(), Tunables());

        result.Plans.Should().BeEmpty();
        result.Rejected.Should().ContainSingle().Which.Reason.Should().Contain("No live context");
    }

    [Fact]
    public void BuildPlans_DemandSpreadAcrossAPeersChannels_CountsAsOneSignal()
    {
        // Non-strict forwarding reports whichever channel LND tried last, so the same 7 payments land
        // on 4 different scids. Grouped per channel this is 2/2/2/1 and fails MinBursts on every one.
        var rows = SufficientDemand();
        rows.Select(r => r.OutgoingChannelId).Distinct().Should().HaveCount(4);

        var result = Plan(rows, Contexts(Context()), Tunables());

        result.Plans.Should().ContainSingle();
        result.Plans[0].Bursts.Should().Be(7);
        result.Plans[0].ChannelsInvolved.Should().Be(4);
    }

    [Fact]
    public void BuildPlans_DemandMet_ProducesAPlanCarryingItsEvidence()
    {
        var result = Plan(SufficientDemand(), Contexts(Context()), Tunables());

        result.Plans.Should().ContainSingle();
        var plan = result.Plans[0];
        plan.PeerPubKey.Should().Be(PEER);
        plan.Bursts.Should().Be(7);
        plan.SuggestedCapacitySats.Should().BeGreaterThan(0);
        plan.LastEvidenceAt.Should().Be(T0.AddSeconds(6 * 300));
    }

    [Fact]
    public void BuildPlans_RanksByMissedFeeAndTruncatesToTheRunCap()
    {
        var rows = SufficientDemand(peer: "peerLow", amountSats: 500_000)
            .Concat(SufficientDemand(peer: "peerHigh", amountSats: 900_000))
            .ToList();

        var result = Plan(rows, Contexts(Context("peerLow"), Context("peerHigh")), Tunables(maxPlans: 1));

        result.Plans.Should().ContainSingle();
        result.Plans[0].PeerPubKey.Should().Be("peerHigh");
    }

    // ── Sizing ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Size_RefillsFasterThanItDrains_UsesTheBufferRegime()
    {
        // In 9M >= Out 5M + Missed 3M: a timing problem, so size to the largest turned-away payment.
        // 1.4M x 3 = 4.2M, inflated by the 1% reserve. Large enough that one drain clears the
        // profitability gate, which is a separate concern from the regime this asserts.
        var (capacity, regime, _, error) = ChannelOpenInitiatorService.Size(
            outSats: 5_000_000, inSats: 9_000_000, missedSats: 3_000_000,
            maxBurstPaymentSats: 1_400_000, earnPpm: 1500, Tunables());

        error.Should().BeNull();
        regime.Should().Be(ChannelOpenSizingRegime.Buffer);
        capacity.Should().Be(4_242_424);
    }

    [Fact]
    public void Size_DrainsFasterThanItRefills_BuysRunway()
    {
        // drain 8M - refill 3.2M = 4.8M/day; 2 days of runway = 9.6M, above the 2.7M buffer floor.
        // Inflated by the 1% reserve.
        var (capacity, regime, clamp, error) = ChannelOpenInitiatorService.Size(
            outSats: 5_000_000, inSats: 3_200_000, missedSats: 3_000_000,
            maxBurstPaymentSats: 900_000, earnPpm: 1500, Tunables());

        error.Should().BeNull();
        regime.Should().Be(ChannelOpenSizingRegime.Draining);
        clamp.Should().Be(ChannelOpenBindingClamp.None);
        capacity.Should().Be(9_696_969);
    }

    [Fact]
    public void Size_NetDrainTooSmallToBeatTheFloor_IsSizedAndLabelledByTheFloor()
    {
        // Two days of a 1k/day drain is 2k, far under the 4.2M floor. The floor decides the size, so
        // it also decides the label: calling this Draining would misread the number beside it.
        var (capacity, regime, _, error) = ChannelOpenInitiatorService.Size(
            outSats: 1_000, inSats: 0, missedSats: 0,
            maxBurstPaymentSats: 1_400_000, earnPpm: 1500, Tunables());

        error.Should().BeNull();
        regime.Should().Be(ChannelOpenSizingRegime.Buffer);
        capacity.Should().Be(4_242_424);
    }

    [Fact]
    public void Size_AboveTheNonWumboCeiling_IsClamped()
    {
        var (capacity, _, clamp, error) = ChannelOpenInitiatorService.Size(
            outSats: 200_000_000, inSats: 0, missedSats: 0,
            maxBurstPaymentSats: 1_000, earnPpm: 1500, Tunables(nodeMax: long.MaxValue, walletBalance: long.MaxValue, remainingBudget: long.MaxValue));

        error.Should().BeNull();
        clamp.Should().Be(ChannelOpenBindingClamp.WumboCeiling);
        capacity.Should().Be(16_777_215);
    }

    [Fact]
    public void Size_BudgetLowerThanEverythingElse_BindsAndIsReported()
    {
        var (capacity, _, clamp, error) = ChannelOpenInitiatorService.Size(
            outSats: 5_000_000, inSats: 3_200_000, missedSats: 3_000_000,
            maxBurstPaymentSats: 900_000, earnPpm: 1500, Tunables(remainingBudget: 4_000_000));

        error.Should().BeNull();
        clamp.Should().Be(ChannelOpenBindingClamp.Budget);
        capacity.Should().Be(4_000_000);
    }

    [Fact]
    public void Size_OneDrainEarnsLessThanTheRatioDemands_ProducesNoPlan()
    {
        // 300k of capacity at 1500 ppm earns 450 sats a drain, nowhere near twice a 50k chain cost.
        var (_, _, _, error) = ChannelOpenInitiatorService.Size(
            outSats: 0, inSats: 0, missedSats: 0,
            maxBurstPaymentSats: 100_000, earnPpm: 1500, Tunables(chainCost: 50_000, nodeMin: 0));

        error.Should().Contain("cost-to-earn");
    }

    [Fact]
    public void Size_OneDrainCoversTheCostTwiceOver_IsAccepted()
    {
        // 4M sats at 1500 ppm earns ~5,940 a drain against 3k of chain cost, a ratio of ~0.50.
        var (capacity, _, _, error) = ChannelOpenInitiatorService.Size(
            outSats: 0, inSats: 0, missedSats: 0,
            maxBurstPaymentSats: 1_400_000, earnPpm: 1500, Tunables(chainCost: 3_000, nodeMin: 0, nodeMax: long.MaxValue));

        error.Should().BeNull();
        capacity.Should().BeGreaterThan(4_000_000);
    }

    [Fact]
    public void Size_CheaperOutboundRate_NeedsABiggerChannelForTheSameRatio()
    {
        // The gate is about earnings, so halving the rate doubles the size the same margin demands.
        var dear = ChannelOpenInitiatorService.Size(0, 0, 0, 1_400_000, 1500,
            Tunables(chainCost: 3_000, nodeMin: 0, nodeMax: long.MaxValue));
        var cheap = ChannelOpenInitiatorService.Size(0, 0, 0, 1_400_000, 300,
            Tunables(chainCost: 3_000, nodeMin: 0, nodeMax: long.MaxValue));

        dear.Error.Should().BeNull();
        cheap.Error.Should().Contain("cost-to-earn");
    }

    [Fact]
    public void Size_ChannelThatWouldChargeNothing_ProducesNoPlan()
    {
        // A zero-ppm channel earns nothing however large it is, so no on-chain cost is recoverable.
        var (_, _, _, error) = ChannelOpenInitiatorService.Size(
            outSats: 0, inSats: 0, missedSats: 0,
            maxBurstPaymentSats: 1_400_000, earnPpm: 0, Tunables(chainCost: 3_000, nodeMin: 0));

        error.Should().Contain("earns nothing");
    }

    [Fact]
    public void Size_BelowTheNodeMinimum_ProducesNoPlan()
    {
        var (_, _, _, error) = ChannelOpenInitiatorService.Size(
            outSats: 0, inSats: 0, missedSats: 0,
            maxBurstPaymentSats: 100_000, earnPpm: 1500, Tunables(nodeMin: 5_000_000, chainCost: 0));

        error.Should().Contain("node minimum");
    }

    [Fact]
    public void BuildPlans_PeerPricedAboveTheDefault_IsValuedAtItsOwnRate()
    {
        // A sink at 3000 ppm earns 31,799 sats over a drain against 12k of chain cost — a ratio of
        // 0.38. Valued at the 1500 global default the same plan would be rejected.
        var result = ChannelOpenInitiatorService.BuildPlans(
            SufficientDemand().ToLookup(x => x.PeerPubKey),
            new Dictionary<string, PeerDemandContext> { [PEER] = Context(ppm: 3_000) },
            Tunables(chainCost: 12_000));

        result.Rejected.Should().BeEmpty();
        result.Plans.Should().HaveCount(1);
        result.Plans[0].SuggestedCapacitySats.Should().Be(10_707_070);
    }

    [Fact]
    public void BuildPlans_CarriesThePolicyItPricedOnto_TheRow()
    {
        // Promotion opens off these two fields, so the rate the gate used and the rate the channel
        // charges cannot drift apart — and the base fee travels with it.
        var result = ChannelOpenInitiatorService.BuildPlans(
            SufficientDemand().ToLookup(x => x.PeerPubKey),
            new Dictionary<string, PeerDemandContext> { [PEER] = Context(ppm: 2_850, baseFeeMsat: 1_400) },
            Tunables());

        result.Plans.Should().ContainSingle();
        result.Plans[0].InheritedFeeRatePpm.Should().Be(2_850);
        result.Plans[0].InheritedBaseFeeMsat.Should().Be(1_400);
    }

    [Fact]
    public void BuildPlans_PeerWithNoRateReported_ProducesNoPlan()
    {
        // Nothing to price the capacity at and nothing to open it at, so the peer never reaches the
        // panel rather than arriving as a row that cannot be promoted.
        var result = ChannelOpenInitiatorService.BuildPlans(
            SufficientDemand().ToLookup(x => x.PeerPubKey),
            new Dictionary<string, PeerDemandContext> { [PEER] = Context(ppm: null) },
            Tunables());

        result.Plans.Should().BeEmpty();
        result.Rejected.Should().ContainSingle()
            .Which.Reason.Should().Contain("no outbound policy");
    }

    /// <summary>
    /// Seven bursts five minutes apart — clears every gate by default.
    /// </summary>
    private static List<PeerDemandFailure> SufficientDemand(string peer = PEER, long amountSats = 500_000)
        => Enumerable.Range(0, 7)
            .Select(i => Fail((ulong)(1 + i % 4), i * 300, amountSats, peer: peer))
            .ToList();
}
