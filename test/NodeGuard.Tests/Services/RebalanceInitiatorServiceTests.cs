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

namespace NodeGuard.Services;

public class RebalanceInitiatorServiceTests
{
    private static readonly RebalanceInitiatorTunables Tunables = new(
        RebalanceTrigger: 0.15,
        MinAmountSats: 10_000,
        MaxAmountSats: 5_000_000,
        CostToEarnRatio: 0.5,
        MaxInitiations: 5);

    private static ChannelSignal Chan(
        int id, string peer, long local, long remote,
        double ema, double target, bool active = true, bool optIn = true)
        => new(id, (ulong)id, peer, local + remote, local, remote, ema, target, active, optIn);

    // ── Classify: sources ───────────────────────────────────────────────────────────────

    [Fact]
    public void Classify_TooLocalOptedIn_BecomesSource_ExcessSizedOnLiveBalance()
    {
        // d = 0.80 - 0.50 = 0.30 > 0.15; targetLocal = 0.5 * 1_000_000 = 500_000; excess = 300_000.
        var channels = new[] { Chan(1, "peerA", local: 800_000, remote: 200_000, ema: 0.80, target: 0.50) };

        var result = RebalanceInitiatorService.Classify(channels, Tunables);

        result.Sources.Should().ContainSingle();
        result.Sources[0].ChannelId.Should().Be(1);
        result.Sources[0].ExcessSats.Should().Be(300_000);
    }

    [Fact]
    public void Classify_TooLocalButNotOptedIn_IsNotSource()
    {
        var channels = new[] { Chan(1, "peerA", 800_000, 200_000, 0.80, 0.50, optIn: false) };

        RebalanceInitiatorService.Classify(channels, Tunables).Sources.Should().BeEmpty();
    }

    [Fact]
    public void Classify_AboveTargetButWithinTrigger_IsNotSource()
    {
        // d = 0.60 - 0.50 = 0.10 <= 0.15 → not "too local" enough to drain.
        var channels = new[] { Chan(1, "peerA", 600_000, 400_000, 0.60, 0.50) };

        RebalanceInitiatorService.Classify(channels, Tunables).Sources.Should().BeEmpty();
    }

    [Fact]
    public void Classify_InactiveChannel_IsNeitherSourceNorDestination()
    {
        var channels = new[] { Chan(1, "peerA", 900_000, 100_000, 0.90, 0.50, active: false) };

        var result = RebalanceInitiatorService.Classify(channels, Tunables);

        result.Sources.Should().BeEmpty();
        result.Destinations.Should().BeEmpty();
    }

    // ── Classify: destinations (peer aggregate) ─────────────────────────────────────────

    [Fact]
    public void Classify_TooRemotePeer_BecomesDestination_WithDeficit()
    {
        // aggEma 0.20 vs target 0.50 → d = -0.30 < -0.15; deficit = 500_000 - 200_000 = 300_000.
        var channels = new[] { Chan(1, "peerA", local: 200_000, remote: 800_000, ema: 0.20, target: 0.50) };

        var result = RebalanceInitiatorService.Classify(channels, Tunables);

        result.Destinations.Should().ContainSingle();
        result.Destinations[0].PeerPubKey.Should().Be("peerA");
        result.Destinations[0].DeficitSats.Should().Be(300_000);
        result.Destinations[0].Members.Should().ContainSingle();
    }

    [Fact]
    public void Classify_AggregatesMultipleChannelsToSamePeer()
    {
        // Chan A balanced (0.50), Chan B depleted (0.10); both base 1_000_000, target 0.50.
        // aggEma = (0.50 + 0.10)/2 = 0.30 → d = -0.20 < -0.15.
        // deficit = target 1_000_000 - peerLocal 600_000 = 400_000.
        var channels = new[]
        {
            Chan(1, "peerA", 500_000, 500_000, 0.50, 0.50),
            Chan(2, "peerA", 100_000, 900_000, 0.10, 0.50),
        };

        var result = RebalanceInitiatorService.Classify(channels, Tunables);

        result.Destinations.Should().ContainSingle();
        result.Destinations[0].DeficitSats.Should().Be(400_000);
        result.Destinations[0].Members.Should().HaveCount(2);
    }

    [Fact]
    public void Classify_PeerAggregateWithinTrigger_IsNotDestination()
    {
        // aggEma 0.42 vs target 0.50 → d = -0.08, inside the 0.15 trigger.
        var channels = new[] { Chan(1, "peerA", 420_000, 580_000, 0.42, 0.50) };

        RebalanceInitiatorService.Classify(channels, Tunables).Destinations.Should().BeEmpty();
    }

    // ── BuildPlans ──────────────────────────────────────────────────────────────────────

    private static RebalanceClassification Classify(params ChannelSignal[] channels)
        => RebalanceInitiatorService.Classify(channels, Tunables);

    [Fact]
    public void BuildPlans_TakesTheFirstAvailableSource_RegardlessOfEarnRate()
    {
        // Two drainable sources on different peers; the FIRST one is the dear one (2000ppm vs 50ppm).
        // Pairing is first-fit in classification order, so chan 1 is drained anyway — there is no
        // cheapest-source preference. Listing it first is what makes this assertion meaningful.
        var classification = Classify(
            Chan(1, "dear", 800_000, 200_000, 0.80, 0.50),
            Chan(2, "cheap", 800_000, 200_000, 0.80, 0.50),
            Chan(3, "dest", 200_000, 800_000, 0.20, 0.50));
        var earn = new Dictionary<int, long> { [1] = 2000, [2] = 50, [3] = 2500 };

        var plans = RebalanceInitiatorService.BuildPlans(classification, earn, Tunables);

        plans.Should().ContainSingle();
        plans[0].SourceChannelId.Should().Be(1);
        plans[0].DestinationPeerPubKey.Should().Be("dest");
    }

    [Fact]
    public void BuildPlans_ProfitGate_SetsMaxFeePctFromDestEarnRate()
    {
        var classification = Classify(
            Chan(1, "cheap", 800_000, 200_000, 0.80, 0.50),
            Chan(3, "dest", 200_000, 800_000, 0.20, 0.50));
        var earn = new Dictionary<int, long> { [1] = 50, [3] = 2500 };

        var plans = RebalanceInitiatorService.BuildPlans(classification, earn, Tunables);

        // maxCostPpm = 0.5 * 2500 = 1250 → MaxFeePct = 1250 / 10_000 = 0.125.
        plans.Should().ContainSingle();
        plans[0].MaxFeePct.Should().BeApproximately(0.125, 1e-9);
    }

    [Fact]
    public void BuildPlans_ZeroEarnDestination_IsSkipped()
    {
        var classification = Classify(
            Chan(1, "cheap", 800_000, 200_000, 0.80, 0.50),
            Chan(3, "dest", 200_000, 800_000, 0.20, 0.50));
        var earn = new Dictionary<int, long> { [1] = 50, [3] = 0 }; // dest earns nothing

        RebalanceInitiatorService.BuildPlans(classification, earn, Tunables).Should().BeEmpty();
    }

    [Fact]
    public void BuildPlans_DestinationWithoutKnownEarnRate_IsSkipped()
    {
        var classification = Classify(
            Chan(1, "cheap", 800_000, 200_000, 0.80, 0.50),
            Chan(3, "dest", 200_000, 800_000, 0.20, 0.50));
        var earn = new Dictionary<int, long> { [1] = 50 }; // no entry for the dest channel

        RebalanceInitiatorService.BuildPlans(classification, earn, Tunables).Should().BeEmpty();
    }

    [Fact]
    public void BuildPlans_AvoidsSamePeerPairing()
    {
        // Peer "A" is BOTH a too-local source (chan 1) and — thanks to a deeply depleted sibling
        // (chan 2) — a too-remote destination in aggregate, and it is classified before peer "B".
        // The only source is on peer "A", so A can't be refilled from itself; B is refilled instead.
        var classification = Classify(
            Chan(1, "A", 800_000, 200_000, 0.80, 0.50),        // source, peer A, excess 300_000
            Chan(2, "A", 50_000, 2_950_000, 0.02, 0.50),       // pulls peer A aggregate too-remote
            Chan(3, "B", 200_000, 800_000, 0.20, 0.50));       // destination, peer B
        var earn = new Dictionary<int, long> { [1] = 4000, [2] = 4000, [3] = 2500 };

        var plans = RebalanceInitiatorService.BuildPlans(classification, earn, Tunables);

        plans.Should().ContainSingle();
        plans[0].SourceChannelId.Should().Be(1);
        plans[0].DestinationPeerPubKey.Should().Be("B"); // NOT "A", despite A being tried first
    }

    [Fact]
    public void BuildPlans_SizesToMinOfExcessAndDeficit()
    {
        // source excess 300_000, dest deficit 400_000 → amount 300_000.
        var classification = Classify(
            Chan(1, "src", 800_000, 200_000, 0.80, 0.50),
            Chan(3, "dest", 100_000, 900_000, 0.10, 0.50));
        var earn = new Dictionary<int, long> { [1] = 50, [3] = 2500 };

        var plans = RebalanceInitiatorService.BuildPlans(classification, earn, Tunables);

        plans.Should().ContainSingle();
        plans[0].AmountSats.Should().Be(300_000);
    }

    [Fact]
    public void BuildPlans_ClampsAmountToMax()
    {
        // source excess 6_000_000, dest deficit 8_000_000, both above the 5_000_000 cap → amount 5_000_000.
        var classification = Classify(
            Chan(1, "src", 9_000_000, 3_000_000, 0.75, 0.25),   // excess = 9M - 3M = 6M
            Chan(3, "dest", 2_000_000, 18_000_000, 0.10, 0.50)); // deficit = 10M - 2M = 8M
        var earn = new Dictionary<int, long> { [1] = 50, [3] = 2500 };

        var plans = RebalanceInitiatorService.BuildPlans(classification, earn, Tunables);

        plans.Should().ContainSingle();
        plans[0].AmountSats.Should().Be(5_000_000);
    }

    [Fact]
    public void BuildPlans_SkipsWhenSourceExcessBelowMin()
    {
        // Chan 1 qualifies as a source by the smoothed signal (ema 0.80 → d 0.30) but its LIVE excess
        // is only 5_000 sats — below the 10_000 min — so it can't feed the depleted destination.
        var classification = Classify(
            Chan(1, "src", 505_000, 495_000, 0.80, 0.50),   // source, live excess = 505_000 - 500_000 = 5_000
            Chan(3, "dest", 100_000, 900_000, 0.10, 0.50));
        var earn = new Dictionary<int, long> { [1] = 50, [3] = 2500 };

        classification.Sources.Should().ContainSingle();
        classification.Sources[0].ExcessSats.Should().Be(5_000);
        RebalanceInitiatorService.BuildPlans(classification, earn, Tunables).Should().BeEmpty();
    }

    [Fact]
    public void BuildPlans_UsesEachSourceAtMostOncePerRun()
    {
        // Two depleted destinations, a single drainable source → only one plan.
        var classification = Classify(
            Chan(1, "src", 5_000_000, 1_000_000, 0.83, 0.50),
            Chan(3, "destA", 200_000, 800_000, 0.20, 0.50),
            Chan(4, "destB", 200_000, 800_000, 0.20, 0.50));
        var earn = new Dictionary<int, long> { [1] = 50, [3] = 2500, [4] = 2400 };

        var plans = RebalanceInitiatorService.BuildPlans(classification, earn, Tunables);

        plans.Should().ContainSingle();
        plans[0].SourceChannelId.Should().Be(1);
    }

    [Fact]
    public void BuildPlans_RespectsMaxInitiationsCap()
    {
        var tunables = Tunables with { MaxInitiations = 1 };
        var classification = Classify(
            Chan(1, "srcA", 800_000, 200_000, 0.80, 0.50),
            Chan(2, "srcB", 800_000, 200_000, 0.80, 0.50),
            Chan(3, "destA", 200_000, 800_000, 0.20, 0.50),
            Chan(4, "destB", 200_000, 800_000, 0.20, 0.50));
        var earn = new Dictionary<int, long> { [1] = 50, [2] = 60, [3] = 2500, [4] = 2400 };

        RebalanceInitiatorService.BuildPlans(classification, earn, tunables).Should().ContainSingle();
    }

    [Fact]
    public void BuildPlans_WeightsDestinationEarnRateByBalanceBase()
    {
        // Dest peer has two channels: 1000ppm (base 1M) and 3000ppm (base 3M).
        // Weighted avg = (1000·1M + 3000·3M) / 4M = 10_000M/4M = 2500ppm → maxFeePct = 0.5·2500/10_000 = 0.125.
        var classification = Classify(
            Chan(1, "src", 800_000, 200_000, 0.80, 0.50),
            Chan(3, "dest", 200_000, 800_000, 0.20, 0.50),      // base 1M
            Chan(4, "dest", 600_000, 2_400_000, 0.20, 0.50));   // base 3M
        var earn = new Dictionary<int, long> { [1] = 50, [3] = 1000, [4] = 3000 };

        var plans = RebalanceInitiatorService.BuildPlans(classification, earn, Tunables);

        plans.Should().ContainSingle();
        plans[0].MaxFeePct.Should().BeApproximately(0.125, 1e-9);
    }

    // ── Fallback pairing: act on a detected imbalance even with no qualifying counterparty ──

    [Fact]
    public void Classify_ChannelInsideTheDeadband_LandsInBothFallbackPools()
    {
        // Perfectly on target: not a source, not a destination, but able to play either role.
        // Lendable down to 0.35 → 500_000 - 350_000 = 150_000. Absorbable up to 0.65 → 150_000.
        var result = Classify(Chan(1, "peerA", local: 500_000, remote: 500_000, ema: 0.50, target: 0.50));

        result.Sources.Should().BeEmpty();
        result.Destinations.Should().BeEmpty();
        result.FallbackSources.Should().ContainSingle().Which.ExcessSats.Should().Be(150_000);
        result.FallbackDestinations.Should().ContainSingle().Which.DeficitSats.Should().Be(150_000);
    }

    [Fact]
    public void BuildPlans_SourceWithNoQualifyingDestination_DrainsIntoTheFirstFallbackPeerThatFits()
    {
        var classification = Classify(
            Chan(1, "src", 800_000, 200_000, 0.80, 0.50),    // source, excess 300_000
            Chan(2, "peerB", 500_000, 500_000, 0.50, 0.50),  // fallback dest, absorbable 150_000
            Chan(3, "peerC", 450_000, 550_000, 0.45, 0.50)); // fallback dest, absorbable 200_000
        var earn = new Dictionary<int, long> { [1] = 50, [2] = 2000, [3] = 2000 };

        classification.Destinations.Should().BeEmpty("neither peer tripped the -0.15 trigger");

        var plans = RebalanceInitiatorService.BuildPlans(classification, earn, Tunables);

        plans.Should().ContainSingle();
        plans[0].SourceChannelId.Should().Be(1);
        // peerB, not the emptier peerC: pass 2 takes the first fallback destination that yields a
        // plan, and has no preference for the one with the most room.
        plans[0].DestinationPeerPubKey.Should().Be("peerB");
        plans[0].IsFallbackPairing.Should().BeTrue();
        // Capped by peerB's room up to target + deadband (650_000 - 500_000), NOT the source's
        // full 300_000 excess — refilling must never turn the destination into next cycle's source.
        plans[0].AmountSats.Should().Be(150_000);
    }

    [Fact]
    public void BuildPlans_DestinationWithNoQualifyingSource_IsFundedByTheFirstFallbackChannel()
    {
        var classification = Classify(
            Chan(1, "peerA", 600_000, 400_000, 0.60, 0.50),  // fallback source, lendable 250_000
            Chan(2, "peerB", 900_000, 100_000, 0.55, 0.50),  // fallback source, lendable 550_000
            Chan(3, "dest", 100_000, 900_000, 0.10, 0.50));  // destination, deficit 400_000
        var earn = new Dictionary<int, long> { [1] = 50, [2] = 60, [3] = 2500 };

        classification.Sources.Should().BeEmpty("neither peer tripped the +0.15 trigger");

        var plans = RebalanceInitiatorService.BuildPlans(classification, earn, Tunables);

        plans.Should().ContainSingle();
        // peerA, not the fuller peerB: the fallback pool is drawn from in classification order.
        plans[0].SourceChannelId.Should().Be(1);
        plans[0].DestinationPeerPubKey.Should().Be("dest");
        plans[0].IsFallbackPairing.Should().BeTrue();
        // Bounded by what peerA may lend (250_000), not the destination's full 400_000 deficit.
        plans[0].AmountSats.Should().Be(250_000);
    }

    [Fact]
    public void BuildPlans_FallbackSourceIsNotDrainedBelowItsOwnDeadband()
    {
        var classification = Classify(
            Chan(1, "peerA", 400_000, 600_000, 0.40, 0.50), // fallback source: lendable to 0.35 = 50_000
            Chan(2, "dest", 0, 1_000_000, 0.00, 0.50));     // destination, deficit 500_000
        var earn = new Dictionary<int, long> { [1] = 50, [2] = 2500 };

        var plans = RebalanceInitiatorService.BuildPlans(classification, earn, Tunables);

        plans.Should().ContainSingle();
        // 50_000, not the 400_000 it actually holds: lending stops at target - deadband so the
        // fallback source can't become next cycle's destination.
        plans[0].AmountSats.Should().Be(50_000);
    }

    [Fact]
    public void BuildPlans_FallbackPairingIsStillProfitGated()
    {
        var classification = Classify(
            Chan(1, "src", 800_000, 200_000, 0.80, 0.50),
            Chan(2, "peerB", 500_000, 500_000, 0.50, 0.50));
        // The only available destination earns nothing, so there is no margin to pay a route with.
        var earn = new Dictionary<int, long> { [1] = 50, [2] = 0 };

        RebalanceInitiatorService.BuildPlans(classification, earn, Tunables).Should().BeEmpty();
    }

    [Fact]
    public void BuildPlans_RefillsAFallbackDestinationAtMostOncePerRun()
    {
        // Two drainable sources, and the only fallback destination has room for 150_000. Each plan
        // is clamped to that room on its own, so without a per-run guard both sources would send
        // peerX 150_000 and land it 300_000 above where it started — past target + deadband, making
        // the peer we just refilled next cycle's source.
        var classification = Classify(
            Chan(1, "srcA", 800_000, 200_000, 0.80, 0.50),   // detected source, excess 300_000
            Chan(2, "srcB", 800_000, 200_000, 0.80, 0.50),   // detected source, excess 300_000
            Chan(3, "peerX", 500_000, 500_000, 0.50, 0.50)); // fallback dest, absorbable 150_000
        var earn = new Dictionary<int, long> { [1] = 50, [2] = 50, [3] = 2000 };

        classification.Destinations.Should().BeEmpty("peerX did not trip the -0.15 trigger");
        classification.FallbackDestinations.Should().ContainSingle()
            .Which.DeficitSats.Should().Be(150_000);

        var plans = RebalanceInitiatorService.BuildPlans(classification, earn, Tunables);

        plans.Should().ContainSingle("peerX has room for one refill, not one per source");
        plans[0].DestinationPeerPubKey.Should().Be("peerX");
        plans[0].AmountSats.Should().Be(150_000);
        plans.Sum(p => p.AmountSats).Should().Be(150_000, "the peer's room is a per-run allowance");
    }

    [Fact]
    public void BuildPlans_PrefersAQualifyingSourceOverAFallbackOne()
    {
        var classification = Classify(
            Chan(1, "src", 800_000, 200_000, 0.80, 0.50),    // qualifying source
            Chan(2, "peerB", 950_000, 50_000, 0.55, 0.50),   // fallback source, far more to lend
            Chan(3, "dest", 100_000, 900_000, 0.10, 0.50));  // destination
        var earn = new Dictionary<int, long> { [1] = 50, [2] = 60, [3] = 2500 };

        var plans = RebalanceInitiatorService.BuildPlans(classification, earn, Tunables);

        plans.Should().ContainSingle();
        plans[0].SourceChannelId.Should().Be(1, "a channel that tripped the trigger is drained before one that did not");
        plans[0].IsFallbackPairing.Should().BeFalse();
    }
}
