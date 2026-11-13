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
using Microsoft.Extensions.Logging;
using NBXplorer.DerivationStrategy;
using NodeGuard.Data.Models;
using NodeGuard.Data.Repositories.Interfaces;
using NodeGuard.Helpers;
using NodeGuard.Services;
using NodeGuard.TestHelpers;
using NodeGuard.Tests.Jobs;
using Quartz;

namespace NodeGuard.Jobs;

/// <summary>
/// Covers the orchestration the pure planner cannot: the kill switches, how per-channel inputs roll up
/// to a peer, the expire-then-reopen lifecycle, and which of record / audit / promote runs per mode.
/// </summary>
[Collection("RoutingEngine")]
public class AutoChannelOpenJobTests
{
    private const string NodePubKey = "managedPubKey";
    private const string Peer = "03peer";
    private const int NodeId = 7;
    private const ulong ChanId = 1001;
    private const ulong SecondChanToPeer = 1003;
    private const string SecondPeer = "03peer2";
    private const ulong SecondPeerChanId = 1002;
    private const long SizeSats = 5_000_000;

    private readonly Mock<ILogger<AutoChannelOpenJob>> _logger = new();
    private readonly Mock<INodeRepository> _nodeRepository = new();
    private readonly Mock<IForwardingHtlcEventRepository> _forwardingHtlcEventRepository = new();
    private readonly Mock<IChannelOpenRecommendationRepository> _recommendationRepository = new();
    private readonly Mock<IChannelOpenPromotionService> _promotionService = new();
    private readonly Mock<ILightningService> _lightningService = new();
    private readonly Mock<INBXplorerService> _nbXplorerService = new();
    private readonly Mock<IChannelOperationRequestRepository> _channelOperationRequestRepository = new();
    private readonly Mock<IAuditService> _auditService = new();
    private readonly Mock<ICoinSelectionService> _coinSelectionService = new();

    private AutoChannelOpenJob BuildJob() =>
        new(
            _logger.Object,
            _nodeRepository.Object,
            _forwardingHtlcEventRepository.Object,
            _recommendationRepository.Object,
            _promotionService.Object,
            _lightningService.Object,
            _nbXplorerService.Object,
            _channelOperationRequestRepository.Object,
            _auditService.Object,
            _coinSelectionService.Object);

    /// <summary>
    /// Both switches are process-wide statics, so they are set and restored around every run to keep
    /// one test from leaking into the next.
    /// </summary>
    private async Task Run(bool engine = true, bool feature = true)
    {
        await RoutingEngineSwitch.WithEngine(engine, async () =>
        {
            var previous = Constants.AUTO_CHANNEL_OPEN_ENABLED;
            Constants.AUTO_CHANNEL_OPEN_ENABLED = feature;
            try
            {
                await BuildJob().Execute(Mock.Of<IJobExecutionContext>());
            }
            finally
            {
                Constants.AUTO_CHANNEL_OPEN_ENABLED = previous;
            }
        });
    }

    /// <summary>
    /// A peer with two channels that together clear every gate: the earned fees and the refusals are
    /// split across them, and both are nearly empty. Optionally a second peer that qualifies the same way.
    /// </summary>
    private Node ArrangeQualifyingPeer(
        AutoChannelOpenMode mode = AutoChannelOpenMode.Recommendation,
        long? sizeSats = SizeSats,
        long budgetSats = 1_000_000_000,
        long committedSats = 0,
        bool dryRun = false,
        long[]? walletUtxoSats = null,
        bool secondPeer = false)
    {
        var node = new Node
        {
            Id = NodeId,
            PubKey = NodePubKey,
            Name = "alice",
            AutoChannelOpenEnabled = true,
            AutoChannelOpenMode = mode,
            AutoChannelOpenWalletId = 3,
            AutoChannelOpenWallet = FundingWallet(),
            AutoChannelOpenSizeSats = sizeSats,
            AutoChannelOpenBudgetSats = budgetSats,
            AutoChannelOpenBudgetStartDatetime = DateTimeOffset.UtcNow.AddHours(-1),
            RoutingEngineDryRun = dryRun,
        };

        _nodeRepository.Setup(x => x.GetAllWithAutoChannelOpenEnabled()).ReturnsAsync(new List<Node> { node });
        _nbXplorerService.Setup(x => x.GetFeesByType(MempoolRecommendedFeesType.HourFee, default)).ReturnsAsync(10m);
        _recommendationRepository.Setup(x => x.ExpireOpen(NodeId)).ReturnsAsync(0);
        _recommendationRepository.Setup(x => x.FailUnrealizedPromotions(NodeId)).ReturnsAsync(0);
        _recommendationRepository.Setup(x => x.GetLastDecisionByPeer(NodeId))
            .ReturnsAsync(new Dictionary<string, DateTimeOffset>());
        _channelOperationRequestRepository
            .Setup(x => x.GetOpenSatsCommittedSince(NodeId, It.IsAny<DateTimeOffset>()))
            .ReturnsAsync(committedSats);

        // Each channel alone falls short of both gates; only the peer-level sums clear them.
        var minFeeMsat = Constants.AUTO_CHANNEL_OPEN_MIN_EARNED_FEE_SATS * 1000;
        var minRefusals = Constants.AUTO_CHANNEL_OPEN_MIN_REFUSALS;
        var fees = new Dictionary<ulong, long> { [ChanId] = minFeeMsat / 2, [SecondChanToPeer] = minFeeMsat / 2 };
        var refusals = new Dictionary<ulong, int> { [ChanId] = minRefusals - minRefusals / 2, [SecondChanToPeer] = minRefusals / 2 };
        var outgoing = new Dictionary<ulong, long> { [ChanId] = 3_000_000_000, [SecondChanToPeer] = 1_000_000_000 };

        var channels = new Lnrpc.ListChannelsResponse
        {
            Channels =
            {
                DryChannel(ChanId, Peer),
                DryChannel(SecondChanToPeer, Peer),
            },
        };
        var policies = new Dictionary<ulong, LocalOutboundPolicy>
        {
            [ChanId] = new(900, 1_000),
            [SecondChanToPeer] = new(1_200, 0),
        };

        if (secondPeer)
        {
            channels.Channels.Add(DryChannel(SecondPeerChanId, SecondPeer));
            policies[SecondPeerChanId] = new LocalOutboundPolicy(900, 1_000);
            fees[SecondPeerChanId] = minFeeMsat * 2;
            refusals[SecondPeerChanId] = minRefusals;
        }

        _forwardingHtlcEventRepository
            .Setup(x => x.GetEarnedFeesMsatByChannel(NodePubKey, It.IsAny<DateTimeOffset>()))
            .ReturnsAsync(fees);
        _forwardingHtlcEventRepository
            .Setup(x => x.GetInsufficientBalanceCountsByChannel(NodePubKey, It.IsAny<DateTimeOffset>()))
            .ReturnsAsync(refusals);
        _forwardingHtlcEventRepository
            .Setup(x => x.GetOutgoingAmountsMsatByChannel(NodePubKey, It.IsAny<DateTimeOffset>()))
            .ReturnsAsync(outgoing);
        _lightningService.Setup(x => x.ListChannels(It.IsAny<Node>())).ReturnsAsync(channels);
        _lightningService.Setup(x => x.GetLocalOutboundPoliciesAsync(It.IsAny<Node>())).ReturnsAsync(policies);

        var utxos = (walletUtxoSats ?? [1_000_000_000])
            .Select((sats, i) => TestUtxos.Spendable(node.AutoChannelOpenWallet, (uint)i, sats))
            .ToList();
        _coinSelectionService.Setup(x => x.GetAvailableUTXOsAsync(It.IsAny<DerivationStrategyBase>()))
            .ReturnsAsync(utxos);

        SetupUpsertReturning(ChannelOpenRecommendationStatus.Open);

        _promotionService
            .Setup(x => x.Promote(It.IsAny<int>(), It.IsAny<long>(), It.IsAny<string?>()))
            .ReturnsAsync((true, (string?)null));

        return node;
    }

    // 100k usable on 10M: 1%, well under the low-outbound threshold.
    private static Lnrpc.Channel DryChannel(ulong chanId, string peer) => new()
    {
        ChanId = chanId,
        RemotePubkey = peer,
        Active = true,
        Initiator = true,
        Capacity = 10_000_000,
        LocalBalance = 200_000,
        LocalChanReserveSat = 100_000,
    };

    private static Wallet FundingWallet()
    {
        var wallet = CreateWallet.SingleSig(CreateWallet.CreateInternalWallet());
        wallet.Id = 3;
        return wallet;
    }

    private void SetupUpsertReturning(ChannelOpenRecommendationStatus status) =>
        _recommendationRepository
            .Setup(x => x.Upsert(It.IsAny<ChannelOpenRecommendation>()))
            .ReturnsAsync((ChannelOpenRecommendation plan) =>
            {
                plan.Id = 42;
                plan.Status = status;
                return (plan, (string?)null);
            });

    private void VerifyRecorded(Times times) =>
        _recommendationRepository.Verify(x => x.Upsert(It.IsAny<ChannelOpenRecommendation>()), times);

    private void VerifyPromoted(Times times) =>
        _promotionService.Verify(x => x.Promote(It.IsAny<int>(), It.IsAny<long>(), It.IsAny<string?>()), times);

    private void VerifyAudited(Times times) =>
        _auditService.Verify(x => x.LogAsync(
            AuditActionType.Create,
            AuditEventType.Success,
            AuditObjectType.ChannelOpenRecommendation,
            It.IsAny<string?>(),
            It.IsAny<object?>()), times);

    // ── Kill switches ──────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Execute_RoutingEngineOff_DoesNotEvenLookForNodes()
    {
        ArrangeQualifyingPeer();

        await Run(engine: false);

        _nodeRepository.Verify(x => x.GetAllWithAutoChannelOpenEnabled(), Times.Never);
    }

    [Fact]
    public async Task Execute_FeatureOff_DoesNotEvenLookForNodes()
    {
        ArrangeQualifyingPeer();

        await Run(feature: false);

        _nodeRepository.Verify(x => x.GetAllWithAutoChannelOpenEnabled(), Times.Never);
    }

    [Fact]
    public async Task Execute_NoFeerate_SkipsBeforeTouchingAnyNode()
    {
        ArrangeQualifyingPeer();
        _nbXplorerService.Setup(x => x.GetFeesByType(MempoolRecommendedFeesType.HourFee, default))
            .ReturnsAsync((decimal?)null);

        await Run();

        // The feerate prices the funding fee, so without it the wallet check can't run.
        _nodeRepository.Verify(x => x.GetAllWithAutoChannelOpenEnabled(), Times.Never);
        VerifyRecorded(Times.Never());
    }

    // ── Peer roll-up ───────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Execute_PeerWithTwoChannels_RecordsOneRowWithTheSummedEvidence()
    {
        ArrangeQualifyingPeer();

        await Run();

        _recommendationRepository.Verify(x => x.Upsert(It.Is<ChannelOpenRecommendation>(r =>
            r.NodeId == NodeId
            && r.PeerPubKey == Peer
            && r.EarnedFeeSats == Constants.AUTO_CHANNEL_OPEN_MIN_EARNED_FEE_SATS
            && r.Refusals == Constants.AUTO_CHANNEL_OPEN_MIN_REFUSALS
            && r.SettledOutSats == 4_000_000
            && r.UsableLocalSats == 200_000
            && r.PeerCapacitySats == 20_000_000
            && r.SuggestedCapacitySats == SizeSats
            // The dearest channel's policy, base fee from that same channel
            && r.InheritedFeeRatePpm == 1_200
            && r.InheritedBaseFeeMsat == 0)), Times.Once);
    }

    [Fact]
    public async Task Execute_RefusalsOnAClosedChannel_AreDropped()
    {
        ArrangeQualifyingPeer();
        _forwardingHtlcEventRepository
            .Setup(x => x.GetInsufficientBalanceCountsByChannel(NodePubKey, It.IsAny<DateTimeOffset>()))
            .ReturnsAsync(new Dictionary<ulong, int> { [999_999] = 100 });

        await Run();

        // No listed channel maps the scid back to a peer, and a closed channel is not a route.
        VerifyRecorded(Times.Never());
    }

    // ── The expire-first lifecycle ─────────────────────────────────────────────────────

    [Fact]
    public async Task Execute_PeerClearsEveryGate_ExpiresFirstThenRecords()
    {
        ArrangeQualifyingPeer();

        await Run();

        // Expiry runs before anything is planned, so the panel is always this run's output.
        _recommendationRepository.Verify(x => x.ExpireOpen(NodeId), Times.Once);
        VerifyRecorded(Times.Once());
    }

    [Fact]
    public async Task Execute_UnrealizedPromotions_AreReleasedBeforeTheCooldownIsRead()
    {
        ArrangeQualifyingPeer();
        var released = false;
        var cooldownSawTheRelease = false;
        _recommendationRepository.Setup(x => x.FailUnrealizedPromotions(NodeId))
            .ReturnsAsync(1)
            .Callback(() => released = true);
        _recommendationRepository.Setup(x => x.GetLastDecisionByPeer(NodeId))
            .ReturnsAsync(new Dictionary<string, DateTimeOffset>())
            .Callback(() => cooldownSawTheRelease = released);

        await Run();

        // A promotion that came to nothing has to be cleared first, or its peer stays suppressed.
        released.Should().BeTrue();
        cooldownSawTheRelease.Should().BeTrue();
    }

    [Fact]
    public async Task Execute_NoSizeConfigured_RecordsNothing()
    {
        ArrangeQualifyingPeer(sizeSats: null);

        await Run();

        VerifyRecorded(Times.Never());
    }

    [Fact]
    public async Task Execute_BudgetExhausted_StillExpiresAndRecordsNothing()
    {
        ArrangeQualifyingPeer(budgetSats: 1_000, committedSats: 1_000);

        await Run();

        _recommendationRepository.Verify(x => x.ExpireOpen(NodeId), Times.Once);
        VerifyRecorded(Times.Never());
    }

    [Fact]
    public async Task Execute_FundingWalletEmpty_RecordsNothing()
    {
        ArrangeQualifyingPeer(walletUtxoSats: []);

        await Run();

        VerifyRecorded(Times.Never());
    }

    [Fact]
    public async Task Execute_WalletJustUnderTheSizeOnceFeeAndChangeAreKept_RecordsNothing()
    {
        // One coin of exactly the size: the funding fee and a dust-safe change output don't fit on top.
        ArrangeQualifyingPeer(walletUtxoSats: [SizeSats]);

        await Run();

        VerifyRecorded(Times.Never());
    }

    // ── Record, audit, promote ─────────────────────────────────────────────────────────

    [Fact]
    public async Task Execute_RecommendationMode_RecordsAndAuditsButNeverPromotes()
    {
        ArrangeQualifyingPeer(mode: AutoChannelOpenMode.Recommendation);

        await Run();

        VerifyRecorded(Times.Once());
        VerifyAudited(Times.Once());
        VerifyPromoted(Times.Never());
    }

    [Fact]
    public async Task Execute_AutoMode_PromotesAtTheFixedSize()
    {
        ArrangeQualifyingPeer(mode: AutoChannelOpenMode.Auto);

        await Run();

        VerifyRecorded(Times.Once());
        _promotionService.Verify(x => x.Promote(42, SizeSats, null), Times.Once);
    }

    [Fact]
    public async Task Execute_AutoModeButDryRun_RecordsWithoutPromoting()
    {
        ArrangeQualifyingPeer(mode: AutoChannelOpenMode.Auto, dryRun: true);

        await Run();

        VerifyRecorded(Times.Once());
        VerifyPromoted(Times.Never());
    }

    [Fact]
    public async Task Execute_UpsertReturnsARowItDidNotOpen_WritesNoAuditEntryAndDoesNotPromote()
    {
        ArrangeQualifyingPeer(mode: AutoChannelOpenMode.Auto);

        // What the upsert returns when the peer's previous open is still unresolved.
        SetupUpsertReturning(ChannelOpenRecommendationStatus.Promoted);

        await Run();

        VerifyAudited(Times.Never());
        VerifyPromoted(Times.Never());
    }

    [Fact]
    public async Task Execute_PeerWithinTheCooldown_IsNotRecommended()
    {
        ArrangeQualifyingPeer();
        _recommendationRepository.Setup(x => x.GetLastDecisionByPeer(NodeId))
            .ReturnsAsync(new Dictionary<string, DateTimeOffset> { [Peer] = DateTimeOffset.UtcNow });

        await Run();

        VerifyRecorded(Times.Never());
    }

    [Fact]
    public async Task Execute_TwoQualifyingPeersButBudgetForOne_PromotesOnlyTheHigherEarner()
    {
        // Each plan is checked against the whole remaining budget, so the job has to decrement as it
        // promotes or both would commit the period's budget.
        ArrangeQualifyingPeer(mode: AutoChannelOpenMode.Auto, budgetSats: SizeSats, secondPeer: true);

        await Run();

        VerifyRecorded(Times.Once());
        _recommendationRepository.Verify(x => x.Upsert(It.Is<ChannelOpenRecommendation>(r => r.PeerPubKey == SecondPeer)), Times.Once);
        VerifyPromoted(Times.Once());
    }
}
