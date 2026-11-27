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
    private const long Size = 5_000_000;

    private static readonly ChannelOpenInitiatorTunables Tunables = new(
        MinEarnedFeeSats: 10_000,
        MinRefusals: 5,
        LowOutboundPct: 20,
        SizeSats: Size,
        WalletSpendableSats: 100_000_000,
        Budget: Budget(100_000_000),
        MaxPlansPerRun: 5);

    private static ChannelOpenBudget Budget(long sats, Dictionary<string, int>? reservedPct = null)
        => ChannelOpenBudget.Build(sats, reservedPct ?? new Dictionary<string, int>(), new Dictionary<string, long>());

    /// <summary>Clears every gate by default; each test breaks exactly one.</summary>
    private static PeerSnapshot Peer(
        string pubKey = "03peer",
        long earnedFeeSats = 10_000,
        int refusals = 5,
        long usableLocalSats = 1_999_999,
        long capacitySats = 10_000_000,
        LocalOutboundPolicy? policy = null,
        bool noPolicy = false,
        bool onCooldown = false,
        bool reserved = false)
        => new(pubKey, earnedFeeSats, 4_000_000, refusals, usableLocalSats, capacitySats,
            noPolicy ? null : policy ?? new LocalOutboundPolicy(1_200, 1_000), onCooldown, reserved);

    /// <summary>A reserved peer with none of the earning evidence, short of outbound.</summary>
    private static PeerSnapshot ReservedPeer(
        string pubKey = "03loop",
        long usableLocalSats = 1_999_999,
        long capacitySats = 10_000_000,
        bool noPolicy = false,
        bool onCooldown = false)
        => Peer(pubKey, earnedFeeSats: 0, refusals: 0, usableLocalSats: usableLocalSats, capacitySats: capacitySats,
            noPolicy: noPolicy, onCooldown: onCooldown, reserved: true);

    private static ChannelOpenClassification Evaluate(PeerSnapshot peer, ChannelOpenInitiatorTunables? t = null)
        => ChannelOpenInitiatorService.Evaluate([peer], t ?? Tunables);

    [Fact]
    public void Evaluate_PeerAtEveryThreshold_IsPlannedAtTheFixedSizeAndInheritedPolicy()
    {
        var result = Evaluate(Peer());

        result.Rejected.Should().BeEmpty();
        var plan = result.Plans.Should().ContainSingle().Subject;
        plan.PeerPubKey.Should().Be("03peer");
        plan.SuggestedCapacitySats.Should().Be(Size);
        plan.EarnedFeeSats.Should().Be(10_000);
        plan.Refusals.Should().Be(5);
        plan.UsableLocalSats.Should().Be(1_999_999);
        plan.PeerCapacitySats.Should().Be(10_000_000);
        plan.InheritedFeeRatePpm.Should().Be(1_200);
        plan.InheritedBaseFeeMsat.Should().Be(1_000);
        plan.Status.Should().Be(ChannelOpenRecommendationStatus.Open);
    }

    [Fact]
    public void Evaluate_EarnedTooLittle_IsRejected()
        => Evaluate(Peer(earnedFeeSats: 9_999)).Plans.Should().BeEmpty();

    [Fact]
    public void Evaluate_TooFewRefusals_IsRejected()
        => Evaluate(Peer(refusals: 4)).Plans.Should().BeEmpty();

    [Fact]
    public void Evaluate_OutboundAtTheThreshold_IsRejected()
    {
        // 20% of 10M exactly: the shortage has passed, whatever the refusals said.
        var result = Evaluate(Peer(usableLocalSats: 2_000_000));

        result.Plans.Should().BeEmpty();
        result.Rejected.Should().ContainSingle().Which.Reason.Should().Contain("usable outbound");
    }

    [Fact]
    public void Evaluate_OnCooldown_IsRejected()
        => Evaluate(Peer(onCooldown: true)).Plans.Should().BeEmpty();

    [Fact]
    public void Evaluate_NoOutboundPolicy_IsRejected()
        => Evaluate(Peer(noPolicy: true)).Plans.Should().BeEmpty();

    [Fact]
    public void Evaluate_NoSizeConfigured_IsRejected()
        => Evaluate(Peer(), Tunables with { SizeSats = 0 }).Plans.Should().BeEmpty();

    [Fact]
    public void Evaluate_WalletCannotFundTheSize_IsRejectedRatherThanShrunk()
    {
        var result = Evaluate(Peer(), Tunables with { WalletSpendableSats = Size - 1 });

        result.Plans.Should().BeEmpty();
        result.Rejected.Should().ContainSingle().Which.Reason.Should().Contain("funding wallet");
    }

    [Fact]
    public void Evaluate_BudgetCannotCoverTheSize_IsRejectedRatherThanShrunk()
    {
        var result = Evaluate(Peer(), Tunables with { Budget = Budget(Size - 1) });

        result.Plans.Should().BeEmpty();
        result.Rejected.Should().ContainSingle().Which.Reason.Should().Contain("budget");
    }

    [Fact]
    public void Evaluate_MorePeersThanTheRunCap_KeepsTheHighestEarners()
    {
        var peers = new[]
        {
            Peer("03low", earnedFeeSats: 10_000),
            Peer("03high", earnedFeeSats: 90_000),
            Peer("03mid", earnedFeeSats: 50_000),
        };

        var result = ChannelOpenInitiatorService.Evaluate(peers, Tunables with { MaxPlansPerRun = 2 });

        result.Plans.Select(x => x.PeerPubKey).Should().Equal("03high", "03mid");
        result.Rejected.Should().ContainSingle().Which.PeerPubKey.Should().Be("03low");
    }

    // ── Reserved peers ─────────────────────────────────────────────────────────────────

    [Fact]
    public void Evaluate_ReservedPeerShortOfOutbound_IsPlannedWithoutAnyEvidence()
    {
        var result = Evaluate(ReservedPeer());

        var plan = result.Plans.Should().ContainSingle().Subject;
        plan.IsReservedPeer.Should().BeTrue();
        plan.SuggestedCapacitySats.Should().Be(Size);
        plan.InheritedFeeRatePpm.Should().Be(1_200);
    }

    [Fact]
    public void Evaluate_ReservedPeerWithNoChannel_IsPlannedAtTheNodeDefaultFee()
    {
        var result = Evaluate(ReservedPeer(usableLocalSats: 0, capacitySats: 0, noPolicy: true));

        var plan = result.Plans.Should().ContainSingle().Subject;
        plan.IsReservedPeer.Should().BeTrue();
        plan.InheritedFeeRatePpm.Should().BeNull();
        plan.InheritedBaseFeeMsat.Should().BeNull();
    }

    [Fact]
    public void Evaluate_ReservedPeerWithOutboundAtTheThreshold_IsRejected()
    {
        var result = Evaluate(ReservedPeer(usableLocalSats: 2_000_000));

        result.Plans.Should().BeEmpty();
        result.Rejected.Should().ContainSingle().Which.Reason.Should().Contain("usable outbound");
    }

    [Fact]
    public void Evaluate_ReservedPeerOnCooldown_IsRejected()
        => Evaluate(ReservedPeer(onCooldown: true)).Plans.Should().BeEmpty();

    [Fact]
    public void Evaluate_ReservedPeer_GoesAheadOfHigherEarnersUnderTheRunCap()
    {
        var peers = new[] { Peer("03high", earnedFeeSats: 90_000), ReservedPeer() };

        var result = ChannelOpenInitiatorService.Evaluate(peers, Tunables with { MaxPlansPerRun = 1 });

        result.Plans.Should().ContainSingle().Which.PeerPubKey.Should().Be("03loop");
    }

    [Fact]
    public void Evaluate_OrdinaryPeer_CannotSpendAReservedShare()
    {
        // A budget for two channels, 60% reserved: what is left for other peers is short of one channel.
        var budget = Budget(Size * 2, new Dictionary<string, int> { ["03loop"] = 60 });
        var peers = new[] { Peer("03peer"), ReservedPeer() };

        var result = ChannelOpenInitiatorService.Evaluate(peers, Tunables with { Budget = budget });

        result.Plans.Should().ContainSingle().Which.PeerPubKey.Should().Be("03loop");
        result.Rejected.Should().ContainSingle().Which.Reason.Should().Contain("budget");
    }
}
