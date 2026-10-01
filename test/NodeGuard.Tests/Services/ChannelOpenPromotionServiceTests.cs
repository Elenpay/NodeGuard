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
using Microsoft.Extensions.Logging;
using NBitcoin;
using NodeGuard.Data.Models;
using NodeGuard.Data.Repositories.Interfaces;
using Quartz;

namespace NodeGuard.Services;

/// <summary>
/// This is where a recommendation turns into a funding instruction, so these cover what the request
/// is built with and which wallets get dispatched without a human signature.
/// </summary>
public class ChannelOpenPromotionServiceTests
{
    private const int RecommendationId = 11;
    private const int NodeIdValue = 7;
    private const int WalletIdValue = 3;
    private const int DestNodeId = 99;
    private const ulong DrainedChanId = 777_666_555;
    private const string PeerPubKey = "03peer";
    private const string NodePubKey = "03alice";

    /// <summary>Any valid PSBT will do; the code under test only checks it is not null.</summary>
    private const string TemplatePsbtHex = "70736274ff01005e01000000013f8f745a7b40c6df77d5c558361509612d34a9fec5540d9e0ab10f1bc1d4eeec0000000000ffffffff0113601200000000002200200b977d20c92e6fb7efaf6286b58f00f4a40cbab98ce7e44fe704b9ac886e3a41000000004f01043587cf032f11242c80000001fc3bee423a018ceb6d226a0663288ed0fb5ee0fa9d790ab370d030e04730e6c803808e2e1cd482bdd07196943c5a8133d169af86022b6bb0381771644ec07444d7101fcce4de3000008001000080010000804f01043587cf0356ac03f480000001f908ae4a66b0ffa1ab14b55af0ce48f2a70225cb9010902d6901af90b93e8f810251af15e05bb8b7eac1895b3f5f47ffefecd8321a02ef22298f89d0a2037df0601060f3a0b33000008001000080010000804f01043587cf037db9468d80000001f52270c7687a3afb351ef4f1b9a30c10f3b6d392170683aea9488c139a11c998032223852338ea67fbfd8dc352ab2625e79a27764621236bf1c0213090d3489e9f10ed0210c83000008001000080010000800001012b8f62120000000000220020997a5fd26a72084c8a2d12bf902acec18669900dd09891d2ea24a8711ca3ec6e220203b0dbc01268f283bf00120c763686bd8984e7789442cf5c2802095f78b9b9ab9a47304402202e1820e8f1e3b8ad7d10117a9a83a3a8567537d73336d01b9f08859fda4856fe0220727a714631e4ffcd954ae4e011b3039232fa16e1ef99f7e6bd6f422fd3187b8502010304020000000105695221028761458f9cc5c051ed6baf4076df9d89ed21daf2d0f7570382c1bcf23c09878121035a4be5f58b8bbe9d399d9f2d8e004b42643649a5c75575fc3739dfac73264b2c2103b0dbc01268f283bf00120c763686bd8984e7789442cf5c2802095f78b9b9ab9a53ae2206028761458f9cc5c051ed6baf4076df9d89ed21daf2d0f7570382c1bcf23c0987811860f3a0b330000080010000800100008001000000050000002206035a4be5f58b8bbe9d399d9f2d8e004b42643649a5c75575fc3739dfac73264b2c18ed0210c83000008001000080010000800100000005000000220603b0dbc01268f283bf00120c763686bd8984e7789442cf5c2802095f78b9b9ab9a181fcce4de30000080010000800100008001000000050000000000";

    private readonly Mock<ILogger<ChannelOpenPromotionService>> _logger = new();
    private readonly Mock<IChannelOpenRecommendationRepository> _recommendationRepository = new();
    private readonly Mock<IChannelOperationRequestRepository> _channelOperationRequestRepository = new();
    private readonly Mock<INodeRepository> _nodeRepository = new();
    private readonly Mock<IWalletRepository> _walletRepository = new();
    private readonly Mock<ILightningService> _lightningService = new();
    private readonly Mock<ISchedulerFactory> _schedulerFactory = new();
    private readonly Mock<IScheduler> _scheduler = new();
    private readonly Mock<IAuditService> _auditService = new();

    private ChannelOpenPromotionService BuildService() =>
        new(
            _logger.Object,
            _recommendationRepository.Object,
            _channelOperationRequestRepository.Object,
            _nodeRepository.Object,
            _walletRepository.Object,
            _lightningService.Object,
            _schedulerFactory.Object,
            _auditService.Object);

    private static ChannelOpenRecommendation Recommendation(
        ChannelOpenRecommendationStatus status = ChannelOpenRecommendationStatus.Open,
        int? walletId = WalletIdValue) => new()
    {
        Id = RecommendationId,
        NodeId = NodeIdValue,
        PeerPubKey = PeerPubKey,
        OutgoingChannelId = DrainedChanId,
        InheritedFeeRatePpm = 2_850,
        InheritedBaseFeeMsat = 1_000,
        SuggestedCapacitySats = 5_000_000,
        Bursts = 7,
        MissedFeeMsat = 108_000_000,
        Status = status,
        Node = new Node
        {
            Id = NodeIdValue,
            Name = "alice",
            PubKey = NodePubKey,
            AutoChannelOpenWalletId = walletId,
        },
    };

    /// <summary>
    /// The whole happy path wired up. <paramref name="isHotWallet"/> drives the branch that decides
    /// whether NodeGuard dispatches the open itself or leaves it for human signers.
    /// </summary>
    private void ArrangePromotable(
        ChannelOpenRecommendationStatus status = ChannelOpenRecommendationStatus.Open,
        int? walletId = WalletIdValue,
        bool isHotWallet = false,
        bool templatePsbtGenerated = true,
        bool signaturesCollected = true)
    {
        _recommendationRepository.Setup(x => x.GetById(RecommendationId))
            .ReturnsAsync(Recommendation(status, walletId));

        _recommendationRepository.Setup(x => x.Update(It.IsAny<ChannelOpenRecommendation>())).ReturnsAsync((true, (string?)null));

        _nodeRepository.Setup(x => x.GetOrCreateByPubKey(PeerPubKey, It.IsAny<ILightningService>()))
            .ReturnsAsync(new Node { Id = DestNodeId, PubKey = PeerPubKey });

        // The service reads request.Id straight after, so the insert has to look like one.
        _channelOperationRequestRepository
            .Setup(x => x.AddAsync(It.IsAny<ChannelOperationRequest>()))
            .ReturnsAsync((ChannelOperationRequest r) => { r.Id = 500; return (true, (string?)null); });

        var wallet = new Wallet { Id = WalletIdValue, IsHotWallet = isHotWallet, MofN = 2 };
        _walletRepository.Setup(x => x.GetById(WalletIdValue)).ReturnsAsync(wallet);

        _lightningService.Setup(x => x.GenerateTemplatePSBT(It.IsAny<ChannelOperationRequest>()))
            .ReturnsAsync(templatePsbtGenerated ? (PSBT.Parse(TemplatePsbtHex, Network.RegTest), false) : ((PSBT?)null, false));

        var persisted = new ChannelOperationRequest
        {
            Id = 500,
            Wallet = wallet,
            ChannelOperationRequestPsbts = signaturesCollected
                ? new List<ChannelOperationRequestPSBT> { new() { IsTemplatePSBT = true } }
                : new List<ChannelOperationRequestPSBT>(),
        };
        _channelOperationRequestRepository.Setup(x => x.GetById(500)).ReturnsAsync(persisted);
        _channelOperationRequestRepository.Setup(x => x.Update(It.IsAny<ChannelOperationRequest>())).Returns((true, (string?)null));

        _schedulerFactory.Setup(x => x.GetScheduler(default)).ReturnsAsync(_scheduler.Object);
    }

    // ── Promote: refusals ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task Promote_RecommendationNotFound_Refuses()
    {
        _recommendationRepository.Setup(x => x.GetById(RecommendationId)).ReturnsAsync((ChannelOpenRecommendation?)null);

        var (ok, error) = await BuildService().Promote(RecommendationId, 5_000_000, userId: "u1");

        ok.Should().BeFalse();
        error.Should().Contain("not found");
    }

    [Theory]
    [InlineData(ChannelOpenRecommendationStatus.Promoted)]
    [InlineData(ChannelOpenRecommendationStatus.Dismissed)]
    [InlineData(ChannelOpenRecommendationStatus.Expired)]
    public async Task Promote_RecommendationAlreadyDecided_RefusesNamingTheStatus(ChannelOpenRecommendationStatus status)
    {
        ArrangePromotable(status: status);

        var (ok, error) = await BuildService().Promote(RecommendationId, 5_000_000, userId: "u1");

        ok.Should().BeFalse();
        error.Should().Contain(status.ToString());
        _channelOperationRequestRepository.Verify(x => x.AddAsync(It.IsAny<ChannelOperationRequest>()), Times.Never);
    }

    [Fact]
    public async Task Promote_NodeWithoutAFundingWallet_RefusesBeforeCreatingAnything()
    {
        ArrangePromotable(walletId: null);

        var (ok, error) = await BuildService().Promote(RecommendationId, 5_000_000, userId: "u1");

        ok.Should().BeFalse();
        error.Should().Contain("funding wallet");
        _channelOperationRequestRepository.Verify(x => x.AddAsync(It.IsAny<ChannelOperationRequest>()), Times.Never);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task Promote_NonPositiveCapacity_Refuses(long capacity)
    {
        ArrangePromotable();

        var (ok, error) = await BuildService().Promote(RecommendationId, capacity, userId: "u1");

        ok.Should().BeFalse();
        error.Should().Contain("greater than zero");
        _channelOperationRequestRepository.Verify(x => x.AddAsync(It.IsAny<ChannelOperationRequest>()), Times.Never);
    }

    // ── Promote: the request it builds ─────────────────────────────────────────────────

    [Fact]
    public async Task Promote_BuildsAPendingOpenRequestFundedFromTheNodesWallet()
    {
        ArrangePromotable();
        ChannelOperationRequest? captured = null;
        _channelOperationRequestRepository
            .Setup(x => x.AddAsync(It.IsAny<ChannelOperationRequest>()))
            .ReturnsAsync((ChannelOperationRequest r) => { r.Id = 500; captured = r; return (true, (string?)null); });

        var (ok, _) = await BuildService().Promote(RecommendationId, 5_000_000, userId: "u1");

        ok.Should().BeTrue();
        captured.Should().NotBeNull();
        captured!.Status.Should().Be(ChannelOperationRequestStatus.Pending);
        captured.RequestType.Should().Be(OperationRequestType.Open);
        captured.WalletId.Should().Be(WalletIdValue);
        captured.SourceNodeId.Should().Be(NodeIdValue);
        captured.DestNodeId.Should().Be(DestNodeId);
        captured.UserId.Should().Be("u1");
    }

    [Fact]
    public async Task Promote_CapacityEditedByTheOperator_OverridesTheSuggestion()
    {
        ArrangePromotable();
        ChannelOperationRequest? captured = null;
        _channelOperationRequestRepository
            .Setup(x => x.AddAsync(It.IsAny<ChannelOperationRequest>()))
            .ReturnsAsync((ChannelOperationRequest r) => { r.Id = 500; captured = r; return (true, (string?)null); });

        // The panel lets the number be changed before promoting, so the suggestion must not win.
        await BuildService().Promote(RecommendationId, 9_999_999, userId: "u1");

        captured!.SatsAmount.Should().Be(9_999_999);
        captured.Description.Should().Contain("Auto channel open");
    }

    [Fact]
    public async Task Promote_MarksTheRecommendationPromotedAndLinksItToTheRequest()
    {
        ArrangePromotable();
        ChannelOpenRecommendation? updated = null;
        _recommendationRepository
            .Setup(x => x.Update(It.IsAny<ChannelOpenRecommendation>()))
            .ReturnsAsync((ChannelOpenRecommendation r) => { updated = r; return (true, (string?)null); });

        await BuildService().Promote(RecommendationId, 5_000_000, userId: "u1");

        // The link is what later tells the job that this peer's open has not resolved yet.
        updated!.Status.Should().Be(ChannelOpenRecommendationStatus.Promoted);
        updated.ChannelOperationRequestId.Should().Be(500);
    }

    // ── Promote: the fee the channel opens at ──────────────────────────────────────────

    [Fact]
    public async Task Promote_OpensAtThePolicyTheRecommendationWasGatedOn()
    {
        // The new channel arrives full next to a sibling the engine already priced high. Opening at the
        // global default would make it the cheap route to a peer we know drains us.
        ArrangePromotable();

        ChannelOperationRequest? captured = null;
        _channelOperationRequestRepository
            .Setup(x => x.AddAsync(It.IsAny<ChannelOperationRequest>()))
            .ReturnsAsync((ChannelOperationRequest r) => { r.Id = 500; captured = r; return (true, (string?)null); });

        await BuildService().Promote(RecommendationId, 5_000_000, userId: "u1");

        captured!.InitialChannelFeeRatePpm.Should().Be(2_850);
        captured.InitialChannelBaseFeeMsat.Should().Be(1_000);
    }

    [Fact]
    public async Task Promote_NeverRepricesAgainstLND()
    {
        // Re-reading the policy here would price off gossip and could contradict the number the
        // operator saw and approved on the panel.
        ArrangePromotable();

        await BuildService().Promote(RecommendationId, 5_000_000, userId: "u1");

        _lightningService.Verify(x => x.GetChannelFeePolicy(It.IsAny<ulong>(), It.IsAny<Node>()), Times.Never);
    }

    [Fact]
    public async Task Promote_RowPredatingFeeCapture_RefusesRatherThanGuessingAPrice()
    {
        // A row written before the policy was captured has no price to open at, and inventing one
        // would just undercut the sibling. The next run replaces it with a priced row.
        ArrangePromotable();
        _recommendationRepository
            .Setup(x => x.GetById(RecommendationId))
            .ReturnsAsync(() =>
            {
                var stale = Recommendation();
                stale.InheritedFeeRatePpm = null;
                stale.InheritedBaseFeeMsat = null;
                return stale;
            });

        var (ok, error) = await BuildService().Promote(RecommendationId, 5_000_000, userId: "u1");

        ok.Should().BeFalse();
        error.Should().Contain("no inherited fee policy");
        _channelOperationRequestRepository.Verify(x => x.AddAsync(It.IsAny<ChannelOperationRequest>()), Times.Never);
    }

    // ── Promote: dispatch ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task Promote_MultisigWallet_LeavesTheRequestPendingForSigners()
    {
        ArrangePromotable(isHotWallet: false);

        var (ok, error) = await BuildService().Promote(RecommendationId, 5_000_000, userId: "u1");

        ok.Should().BeTrue();
        error.Should().BeNull();
        _lightningService.Verify(x => x.GenerateTemplatePSBT(It.IsAny<ChannelOperationRequest>()), Times.Never);
        _scheduler.Verify(x => x.ScheduleJob(It.IsAny<IJobDetail>(), It.IsAny<ITrigger>(), default), Times.Never);
    }

    [Fact]
    public async Task Promote_HotWalletWithItsTemplate_SchedulesTheOpenItself()
    {
        // A hot wallet has no external signer, so without this the request would sit Pending forever
        // with no UI action that can move it.
        ArrangePromotable(isHotWallet: true);

        var (ok, _) = await BuildService().Promote(RecommendationId, 5_000_000, userId: "u1");

        ok.Should().BeTrue();
        _scheduler.Verify(x => x.ScheduleJob(It.IsAny<IJobDetail>(), It.IsAny<ITrigger>(), default), Times.Once);
    }

    [Fact]
    public async Task Promote_HotWalletWithNoTemplatePsbt_FailsTheRequestRatherThanLeavingItPending()
    {
        ArrangePromotable(isHotWallet: true, templatePsbtGenerated: false);
        ChannelOperationRequest? failed = null;
        _channelOperationRequestRepository
            .Setup(x => x.Update(It.IsAny<ChannelOperationRequest>()))
            .Returns((ChannelOperationRequest r) => { failed = r; return (true, (string?)null); });

        var (ok, error) = await BuildService().Promote(RecommendationId, 5_000_000, userId: "u1");

        ok.Should().BeFalse();
        error.Should().NotBeNull();
        failed!.Status.Should().Be(ChannelOperationRequestStatus.Failed);
        _scheduler.Verify(x => x.ScheduleJob(It.IsAny<IJobDetail>(), It.IsAny<ITrigger>(), default), Times.Never);
    }

    [Fact]
    public async Task Promote_HotWalletStillMissingSignatures_DoesNotSchedule()
    {
        ArrangePromotable(isHotWallet: true, signaturesCollected: false);

        var (ok, _) = await BuildService().Promote(RecommendationId, 5_000_000, userId: "u1");

        ok.Should().BeTrue();
        _scheduler.Verify(x => x.ScheduleJob(It.IsAny<IJobDetail>(), It.IsAny<ITrigger>(), default), Times.Never);
    }

    // ── Dismiss ────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Dismiss_RecordsTheStatusAndTheReason()
    {
        ArrangePromotable();
        ChannelOpenRecommendation? updated = null;
        _recommendationRepository
            .Setup(x => x.Update(It.IsAny<ChannelOpenRecommendation>()))
            .ReturnsAsync((ChannelOpenRecommendation r) => { updated = r; return (true, (string?)null); });

        var (ok, _) = await BuildService().Dismiss(RecommendationId, "not now", userId: "u1");

        ok.Should().BeTrue();
        updated!.Status.Should().Be(ChannelOpenRecommendationStatus.Dismissed);
        updated.DismissReason.Should().Be("not now");
    }

    [Fact]
    public async Task Dismiss_AlreadyDecided_Refuses()
    {
        ArrangePromotable(status: ChannelOpenRecommendationStatus.Promoted);

        var (ok, error) = await BuildService().Dismiss(RecommendationId, "not now", userId: "u1");

        ok.Should().BeFalse();
        error.Should().Contain("dismissed");
    }
}
