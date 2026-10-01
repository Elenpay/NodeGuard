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
using NBitcoin;
using NBXplorer.Models;
using NodeGuard.Data.Models;
using NodeGuard.Data.Repositories.Interfaces;
using NodeGuard.Helpers;
using NodeGuard.Services;
using NodeGuard.Tests.Jobs;
using Quartz;

namespace NodeGuard.Jobs;

/// <summary>
/// Covers the orchestration the pure planner cannot: the kill switches, the expire-then-reopen
/// lifecycle, and which of record / audit / promote runs for a given node mode.
/// </summary>
[Collection("RoutingEngine")]
public class AutoChannelOpenJobTests
{
    private const string NodePubKey = "managedPubKey";
    private const string Peer = "03peer";
    private const int NodeId = 7;
    private const ulong ChanId = 1001;
    private const string SecondPeer = "03peer2";
    private const ulong SecondChanId = 1002;

    /// <summary>
    /// The fee gate wants ~1.1 BTC of refused volume at 900 ppm, so the arranged demand is large on
    /// purpose. MIN_BURSTS bursts of this clear it with margin.
    /// </summary>
    private const long BurstSats = 50_000_000;

    private const long BurstMsat = BurstSats * 1000;

    private readonly Mock<ILogger<AutoChannelOpenJob>> _logger = new();
    private readonly Mock<INodeRepository> _nodeRepository = new();
    private readonly Mock<IForwardingHtlcEventRepository> _forwardingHtlcEventRepository = new();
    private readonly Mock<IChannelOpenRecommendationRepository> _recommendationRepository = new();
    private readonly Mock<IChannelOpenPromotionService> _promotionService = new();
    private readonly Mock<ILightningService> _lightningService = new();
    private readonly Mock<INBXplorerService> _nbXplorerService = new();
    private readonly Mock<IChannelOperationRequestRepository> _channelOperationRequestRepository = new();
    private readonly Mock<IAuditService> _auditService = new();

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
            _auditService.Object);

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
    /// A node whose peer refused three separate payments, each far apart enough to stay its own
    /// burst, against a channel too dry to have served them. Clears every gate by default.
    /// </summary>
    private Node ArrangeQualifyingDemand(
        AutoChannelOpenMode mode = AutoChannelOpenMode.Recommendation,
        long budgetSats = 1_000_000_000,
        long committedSats = 0,
        bool dryRun = false,
        long walletBalanceSats = 1_000_000_000,
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
            AutoChannelOpenWallet = new Wallet { Id = 3 },
            AutoChannelOpenBudgetSats = budgetSats,
            AutoChannelOpenBudgetStartDatetime = DateTimeOffset.UtcNow.AddHours(-1),
            RoutingEngineDryRun = dryRun,
        };

        _nodeRepository.Setup(x => x.GetAllWithAutoChannelOpenEnabled()).ReturnsAsync(new List<Node> { node });
        _nbXplorerService.Setup(x => x.GetFeesByType(MempoolRecommendedFeesType.HourFee, default)).ReturnsAsync(10m);
        _recommendationRepository.Setup(x => x.ExpireOpen(NodeId)).ReturnsAsync(0);
        _recommendationRepository.Setup(x => x.FailUnrealizedPromotions(NodeId)).ReturnsAsync(0);
        _channelOperationRequestRepository
            .Setup(x => x.GetOpenSatsCommittedSince(NodeId, It.IsAny<DateTimeOffset>()))
            .ReturnsAsync(committedSats);

        var now = DateTimeOffset.UtcNow;
        // Exactly MIN_BURSTS bursts per peer, 10 minutes apart so none merge.
        var failures = Enumerable.Range(1, Constants.AUTO_CHANNEL_OPEN_MIN_BURSTS)
            .Select(i => new ForwardingHtlcFailure(ChanId, now.AddMinutes(-10 * i), BurstMsat, 900, null, "peerAlias"))
            .ToList();

        if (secondPeer)
        {
            failures.AddRange(Enumerable.Range(1, Constants.AUTO_CHANNEL_OPEN_MIN_BURSTS)
                .Select(i => new ForwardingHtlcFailure(SecondChanId, now.AddMinutes(-10 * i - 1), BurstMsat, 900, null, "peer2Alias")));
        }
        _forwardingHtlcEventRepository
            .Setup(x => x.GetInsufficientBalanceFailures(NodePubKey, It.IsAny<DateTimeOffset>()))
            .ReturnsAsync(failures);

        // Usable local is a rounding error next to a single refused payment, so the live check agrees
        // the channel is genuinely dry rather than merely momentarily empty.
        var channels = new Lnrpc.ListChannelsResponse
        {
            Channels =
            {
                new Lnrpc.Channel
                {
                    ChanId = ChanId, RemotePubkey = Peer, Active = true, Initiator = true,
                    Capacity = 20_000_000, LocalBalance = 1_000_000, LocalChanReserveSat = 0,
                },
            },
        };
        var policies = new Dictionary<ulong, LocalOutboundPolicy> { [ChanId] = new(900, 1_000) };

        if (secondPeer)
        {
            channels.Channels.Add(new Lnrpc.Channel
            {
                ChanId = SecondChanId, RemotePubkey = SecondPeer, Active = true, Initiator = true,
                Capacity = 20_000_000, LocalBalance = 1_000_000, LocalChanReserveSat = 0,
            });
            policies[SecondChanId] = new LocalOutboundPolicy(900, 1_000);
        }

        _lightningService.Setup(x => x.ListChannels(It.IsAny<Node>())).ReturnsAsync(channels);
        _lightningService.Setup(x => x.GetLocalOutboundPoliciesAsync(It.IsAny<Node>())).ReturnsAsync(policies);
        _forwardingHtlcEventRepository
            .Setup(x => x.GetOutgoingAmountsMsatByChannel(NodePubKey, It.IsAny<DateTimeOffset>()))
            .ReturnsAsync(new Dictionary<ulong, long>());
        _forwardingHtlcEventRepository
            .Setup(x => x.GetIncomingAmountsMsatByChannel(NodePubKey, It.IsAny<DateTimeOffset>()))
            .ReturnsAsync(new Dictionary<ulong, long>());
        _recommendationRepository.Setup(x => x.GetLastDecisionByPeer(NodeId))
            .ReturnsAsync(new Dictionary<string, DateTimeOffset>());

        _lightningService.Setup(x => x.GetWalletBalance(It.IsAny<Wallet>()))
            .ReturnsAsync(new GetBalanceResponse { Confirmed = Money.Satoshis(walletBalanceSats) });

        SetupUpsertReturning(ChannelOpenRecommendationStatus.Open);

        _promotionService
            .Setup(x => x.Promote(It.IsAny<int>(), It.IsAny<long>(), It.IsAny<string?>()))
            .ReturnsAsync((true, (string?)null));

        return node;
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
        ArrangeQualifyingDemand();

        await Run(engine: false);

        _nodeRepository.Verify(x => x.GetAllWithAutoChannelOpenEnabled(), Times.Never);
    }

    [Fact]
    public async Task Execute_FeatureOff_DoesNotEvenLookForNodes()
    {
        ArrangeQualifyingDemand();

        await Run(feature: false);

        _nodeRepository.Verify(x => x.GetAllWithAutoChannelOpenEnabled(), Times.Never);
    }

    [Fact]
    public async Task Execute_NoFeerate_SkipsBeforeTouchingAnyNode()
    {
        ArrangeQualifyingDemand();
        _nbXplorerService.Setup(x => x.GetFeesByType(MempoolRecommendedFeesType.HourFee, default))
            .ReturnsAsync((decimal?)null);

        await Run();

        // The feerate prices the chain-cost floor, so without it no plan can be sized at all.
        _nodeRepository.Verify(x => x.GetAllWithAutoChannelOpenEnabled(), Times.Never);
        VerifyRecorded(Times.Never());
    }

    // ── The expire-first lifecycle ─────────────────────────────────────────────────────

    [Fact]
    public async Task Execute_DemandClearsEveryGate_ExpiresFirstThenRecords()
    {
        ArrangeQualifyingDemand();

        await Run();

        // Expiry is unconditional and runs before anything is planned, so the panel is always this
        // run's output rather than an accumulation of earlier ones.
        _recommendationRepository.Verify(x => x.ExpireOpen(NodeId), Times.Once);
        VerifyRecorded(Times.Once());
    }

    [Fact]
    public async Task Execute_UnrealizedPromotions_AreReleasedBeforeTheCooldownIsRead()
    {
        ArrangeQualifyingDemand();
        var released = false;
        var cooldownSawTheRelease = false;
        _recommendationRepository.Setup(x => x.FailUnrealizedPromotions(NodeId))
            .ReturnsAsync(1)
            .Callback(() => released = true);
        _recommendationRepository.Setup(x => x.GetLastDecisionByPeer(NodeId))
            .ReturnsAsync(new Dictionary<string, DateTimeOffset>())
            .Callback(() => cooldownSawTheRelease = released);

        await Run();

        // The cooldown is derived from the stored statuses, so a promotion that came to nothing has to
        // be cleared first or its peer stays suppressed for a channel it never got.
        released.Should().BeTrue();
        cooldownSawTheRelease.Should().BeTrue();
    }

    [Fact]
    public async Task Execute_BudgetExhausted_StillExpiresAndRecordsNothing()
    {
        ArrangeQualifyingDemand(budgetSats: 1_000, committedSats: 1_000);

        await Run();

        // The accepted cost of expiring first: a node that cannot spend clears its panel anyway,
        // even though the demand behind those rows has not gone away.
        _recommendationRepository.Verify(x => x.ExpireOpen(NodeId), Times.Once);
        VerifyRecorded(Times.Never());
    }

    [Fact]
    public async Task Execute_FundingWalletEmpty_RecordsNothing()
    {
        ArrangeQualifyingDemand(walletBalanceSats: 0);

        await Run();

        VerifyRecorded(Times.Never());
    }

    // ── Record, audit, promote ─────────────────────────────────────────────────────────

    [Fact]
    public async Task Execute_RecommendationMode_RecordsAndAuditsButNeverPromotes()
    {
        ArrangeQualifyingDemand(mode: AutoChannelOpenMode.Recommendation);

        await Run();

        VerifyRecorded(Times.Once());
        VerifyAudited(Times.Once());
        VerifyPromoted(Times.Never());
    }

    [Fact]
    public async Task Execute_AutoMode_Promotes()
    {
        ArrangeQualifyingDemand(mode: AutoChannelOpenMode.Auto);

        await Run();

        VerifyRecorded(Times.Once());
        VerifyPromoted(Times.Once());
    }

    [Fact]
    public async Task Execute_AutoModeButDryRun_RecordsWithoutPromoting()
    {
        ArrangeQualifyingDemand(mode: AutoChannelOpenMode.Auto, dryRun: true);

        await Run();

        VerifyRecorded(Times.Once());
        VerifyPromoted(Times.Never());
    }

    [Fact]
    public async Task Execute_UpsertReturnsARowItDidNotOpen_WritesNoAuditEntryAndDoesNotPromote()
    {
        ArrangeQualifyingDemand(mode: AutoChannelOpenMode.Auto);

        // What the upsert returns when the peer's previous open is still unresolved. Auditing a
        // Create here would claim a recommendation that was never written, once per run, forever.
        SetupUpsertReturning(ChannelOpenRecommendationStatus.Promoted);

        await Run();

        VerifyAudited(Times.Never());
        VerifyPromoted(Times.Never());
    }

    // ── Gates the job owns rather than the planner ─────────────────────────────────────

    [Fact]
    public async Task Execute_PeerWithinTheCooldown_IsNotRecommended()
    {
        ArrangeQualifyingDemand();
        _recommendationRepository.Setup(x => x.GetLastDecisionByPeer(NodeId))
            .ReturnsAsync(new Dictionary<string, DateTimeOffset> { [Peer] = DateTimeOffset.UtcNow });

        await Run();

        VerifyRecorded(Times.Never());
    }

    [Fact]
    public async Task Execute_TwoQualifyingPeersButBudgetForOne_DropsTheSecondEntirely()
    {
        // Every plan is sized against the *whole* remaining budget, so without the dispatcher
        // decrementing as it goes both would be promoted and the period committed twice over.
        ArrangeQualifyingDemand(mode: AutoChannelOpenMode.Auto, budgetSats: 20_000_000, secondPeer: true);

        await Run();

        // The budget check sits ahead of the record step, so the peer that no longer fits is not
        // written either. Its demand is real but an operator never sees it on the panel.
        VerifyRecorded(Times.Once());
        VerifyPromoted(Times.Once());
    }
}
