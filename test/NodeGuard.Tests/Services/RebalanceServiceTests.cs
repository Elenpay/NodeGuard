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
using NodeGuard.Data.Models;
using NodeGuard.Data.Repositories.Interfaces;
using NodeGuard.Helpers;
using NodeGuard.Jobs;
using Quartz;
using Channel = NodeGuard.Data.Models.Channel;

namespace NodeGuard.Services;

public class RebalanceServiceTests
{
    private readonly Mock<ILogger<RebalanceService>> _logger = new();
    private readonly Mock<INodeRepository> _nodeRepo = new();
    private readonly Mock<IChannelRepository> _channelRepo = new();
    private readonly Mock<IRebalanceRepository> _rebalanceRepo = new();
    private readonly Mock<ILightningService> _lightning = new();
    private readonly Mock<IAuditService> _audit = new();
    private readonly Mock<ISchedulerFactory> _schedulerFactory = new();
    private readonly Mock<IScheduler> _scheduler = new();

    public RebalanceServiceTests()
    {
        _schedulerFactory.Setup(x => x.GetScheduler(It.IsAny<CancellationToken>()))
            .ReturnsAsync(_scheduler.Object);
        // SourceChannelId is now mandatory on RebalanceRequest, so any test that reaches the
        // source-channel lookup needs a Channel back. The service also resolves the channel's
        // counterparty peer via _nodeRepository.GetById to enforce the "TargetPubkey != counterparty"
        // guard, so the counterparty node has to resolve too. Pre-stub both so individual tests
        // don't repeat it. Tests that explicitly want either lookup to fail can override.
        StubChannelRepo();
        StubCounterpartyNode();
    }

    private RebalanceService CreateService() => new(
        _logger.Object,
        _nodeRepo.Object,
        _channelRepo.Object,
        _rebalanceRepo.Object,
        _lightning.Object,
        _audit.Object,
        _schedulerFactory.Object);

    private static Node CreateNode(int id = 1, string pubkey = "030000000000000000000000000000000000000000000000000000000000000001")
        => new()
        {
            Id = id,
            Name = $"node-{id}",
            PubKey = pubkey,
            Endpoint = "localhost:10009",
            ChannelAdminMacaroon = "mac",
        };

    // SourceChannelId and TargetPubkey are required on the domain request; tests that don't care
    // about either still need to supply something that satisfies the validators (and, for flow
    // tests, something the channel repo can resolve via StubChannelRepo).
    private const int ValidChannelId = 1;
    private const string ValidTargetPubkey = "030000000000000000000000000000000000000000000000000000000000000099";
    // Local node id (matches CreateNode() default) is 1; the source channel's counterparty is
    // node id 2 with this pubkey — different from ValidTargetPubkey so the no-op guard passes
    // by default. Tests that exercise the guard pass CounterpartyPubkey as the request's
    // TargetPubkey to make it collide with the (default) counterparty pubkey.
    private const int CounterpartyNodeId = 2;
    private const string CounterpartyPubkey = "030000000000000000000000000000000000000000000000000000000000000002";

    /// <summary>
    /// Stubs ChannelRepository.GetById so the source-channel lookup inside RebalanceAsync resolves
    /// to a real-looking Channel instead of throwing. The local node (id 1 via CreateNode()) is
    /// set as the SourceNode and CounterpartyNodeId as the DestinationNode, mirroring the
    /// "we opened this channel" case.
    /// </summary>
    private void StubChannelRepo(int channelId = ValidChannelId, ulong chanIdLnd = 12345UL)
    {
        _channelRepo.Setup(x => x.GetById(channelId)).ReturnsAsync(new Channel
        {
            Id = channelId,
            ChanId = chanIdLnd,
            SatsAmount = 1_000_000,
            Status = Channel.ChannelStatus.Open,
            FundingTx = "tx",
            SourceNodeId = 1,
            DestinationNodeId = CounterpartyNodeId,
        });
    }

    /// <summary>
    /// Stubs NodeRepository.GetById for the source channel's counterparty peer. The service
    /// looks this up to evaluate the "TargetPubkey != counterparty" guard.
    /// </summary>
    private void StubCounterpartyNode(string pubkey = CounterpartyPubkey)
    {
        _nodeRepo.Setup(x => x.GetById(CounterpartyNodeId, It.IsAny<bool>())).ReturnsAsync(new Node
        {
            Id = CounterpartyNodeId,
            Name = "counterparty",
            PubKey = pubkey,
            Endpoint = "peer:10009",
            ChannelAdminMacaroon = "mac",
        });
    }

    /// <summary>
    /// Wires up the repository mocks so that AddAsync stamps an Id and GetById returns the
    /// same instance — mimicking what EF would do without an actual database.
    /// </summary>
    private Rebalance? StubRepoForCapture(int newId = 42)
    {
        Rebalance? captured = null;
        _rebalanceRepo.Setup(r => r.AddAsync(It.IsAny<Rebalance>()))
            .Callback<Rebalance>(r => { r.Id = newId; captured = r; })
            .ReturnsAsync((true, (string?)null));
        _rebalanceRepo.Setup(r => r.GetById(newId))
            .ReturnsAsync(() => captured);
        _rebalanceRepo.Setup(r => r.Update(It.IsAny<Rebalance>()))
            .Returns((true, (string?)null));
        return captured;
    }

    [Fact]
    public async Task RebalanceAsync_AmountZero_ThrowsWithTargetEqualsCurrentMessage()
    {
        // Amount=0 is what the modal computes when target inbound % equals (or is below) current inbound %.
        // The service must reject this with a clear, user-facing message.
        var service = CreateService();
        var request = new RebalanceRequest(NodeId: 1, ValidChannelId, ValidTargetPubkey, AmountSats: 0, MaxFeePct: null,
            TimeoutSeconds: Constants.DEFAULT_REBALANCE_TIMEOUT_SECONDS);

        await FluentActions.Awaiting(() => service.RebalanceAsync(request))
            .Should().ThrowAsync<ArgumentException>()
            .WithMessage("*already at or above the requested inbound ratio*");
    }

    [Theory]
    [InlineData(1.5)]   // above the closed upper bound
    [InlineData(0.0)]   // zero would zero the next amount
    [InlineData(-0.1)]  // negative
    public async Task RebalanceAsync_AmountBackoffRatioOutOfRange_Throws(double ratio)
    {
        // Ratio must be in the half-open interval (0, 1]. 1.0 is now allowed (never shrink).
        var service = CreateService();
        var request = new RebalanceRequest(NodeId: 1, ValidChannelId, ValidTargetPubkey,
            AmountSats: 1000, MaxFeePct: null, AmountBackoffRatio: ratio, TimeoutSeconds: Constants.DEFAULT_REBALANCE_TIMEOUT_SECONDS);

        await FluentActions.Awaiting(() => service.RebalanceAsync(request))
            .Should().ThrowAsync<ArgumentException>()
            .WithMessage("*Amount backoff ratio must be in the half-open interval (0, 1]*");
    }

    [Fact]
    public async Task RebalanceAsync_MaxAttemptsZeroOrNegative_Throws()
    {
        var service = CreateService();
        var request = new RebalanceRequest(NodeId: 1, ValidChannelId, ValidTargetPubkey,
            AmountSats: 1000, MaxFeePct: null, MaxAttempts: 0, TimeoutSeconds: Constants.DEFAULT_REBALANCE_TIMEOUT_SECONDS);

        await FluentActions.Awaiting(() => service.RebalanceAsync(request))
            .Should().ThrowAsync<ArgumentException>()
            .WithMessage("*Max attempts must be at least 1*");
    }

    [Fact]
    public async Task RebalanceAsync_RetryMaxFeePctOutOfRange_Throws()
    {
        var service = CreateService();
        var request = new RebalanceRequest(NodeId: 1, ValidChannelId, ValidTargetPubkey,
            AmountSats: 1000, MaxFeePct: null, RetryMaxFeePct: -0.1, TimeoutSeconds: Constants.DEFAULT_REBALANCE_TIMEOUT_SECONDS);

        await FluentActions.Awaiting(() => service.RebalanceAsync(request))
            .Should().ThrowAsync<ArgumentException>()
            .WithMessage("*Retry max fee % must be greater than 0*");
    }

    [Fact]
    public async Task RebalanceAsync_NodeNotFound_Throws()
    {
        _nodeRepo.Setup(x => x.GetById(99, It.IsAny<bool>())).ReturnsAsync((Node?)null);
        var service = CreateService();
        var request = new RebalanceRequest(NodeId: 99, ValidChannelId, ValidTargetPubkey, AmountSats: 1000, MaxFeePct: null,
            TimeoutSeconds: Constants.DEFAULT_REBALANCE_TIMEOUT_SECONDS);

        await FluentActions.Awaiting(() => service.RebalanceAsync(request))
            .Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public async Task RebalanceAsync_SourceChannelIdMissing_Throws()
    {
        var service = CreateService();
        var request = new RebalanceRequest(NodeId: 1, SourceChannelId: 0, ValidTargetPubkey,
            AmountSats: 1000, MaxFeePct: null, TimeoutSeconds: Constants.DEFAULT_REBALANCE_TIMEOUT_SECONDS);

        await FluentActions.Awaiting(() => service.RebalanceAsync(request))
            .Should().ThrowAsync<ArgumentException>()
            .WithMessage("*Source channel id is required*");
    }

    [Fact]
    public async Task RebalanceAsync_TargetPubkeyEmpty_Throws()
    {
        var service = CreateService();
        var request = new RebalanceRequest(NodeId: 1, ValidChannelId, TargetPubkey: "   ",
            AmountSats: 1000, MaxFeePct: null, TimeoutSeconds: Constants.DEFAULT_REBALANCE_TIMEOUT_SECONDS);

        await FluentActions.Awaiting(() => service.RebalanceAsync(request))
            .Should().ThrowAsync<ArgumentException>()
            .WithMessage("*Target pubkey is required*");
    }

    [Fact]
    public async Task RebalanceAsync_SourceChannelNotFound_Throws()
    {
        // ValidChannelId is pre-stubbed in the constructor; this test overrides to null so the
        // source-channel lookup fails after the args pass the cheap validators.
        var node = CreateNode();
        _nodeRepo.Setup(x => x.GetById(node.Id, It.IsAny<bool>())).ReturnsAsync(node);
        _channelRepo.Setup(x => x.GetById(ValidChannelId)).ReturnsAsync((Channel?)null);

        var service = CreateService();
        var request = new RebalanceRequest(node.Id, ValidChannelId, ValidTargetPubkey,
            AmountSats: 100_000, MaxFeePct: null, TimeoutSeconds: Constants.DEFAULT_REBALANCE_TIMEOUT_SECONDS);

        await FluentActions.Awaiting(() => service.RebalanceAsync(request))
            .Should().ThrowAsync<ArgumentException>()
            .WithMessage($"*Source channel {ValidChannelId} not found*");
    }

    [Fact]
    public async Task RebalanceAsync_TargetPubkeyEqualsSourceCounterparty_Throws()
    {
        // Pinning the LastHopPubkey to the same peer that's already the source channel's
        // counterparty would route the sats straight back to where they came from — a no-op
        // rebalance. The service must reject this.
        var node = CreateNode();
        _nodeRepo.Setup(x => x.GetById(node.Id, It.IsAny<bool>())).ReturnsAsync(node);
        // The default StubChannelRepo + StubCounterpartyNode in the ctor already sets up a
        // channel where node 1 is the source and node 2 (with CounterpartyPubkey) is the
        // destination. Asking the service to pin the last hop to CounterpartyPubkey hits the
        // guard.
        var service = CreateService();
        var request = new RebalanceRequest(node.Id, ValidChannelId, TargetPubkey: CounterpartyPubkey,
            AmountSats: 100_000, MaxFeePct: null, TimeoutSeconds: Constants.DEFAULT_REBALANCE_TIMEOUT_SECONDS);

        await FluentActions.Awaiting(() => service.RebalanceAsync(request))
            .Should().ThrowAsync<ArgumentException>()
            .WithMessage("*same as the source channel's counterparty peer*");
    }

    [Fact]
    public async Task RebalanceAsync_TargetPubkeyEqualsSourceCounterparty_NodeIsDestination_Throws()
    {
        // Same guard, mirrored: the local node is the channel's DESTINATION (peer opened the
        // channel to us). Counterparty is then the SourceNode side. Override the channel stub
        // to flip the orientation; the counterparty node lookup still returns CounterpartyPubkey
        // via the constructor's StubCounterpartyNode.
        var node = CreateNode();
        _nodeRepo.Setup(x => x.GetById(node.Id, It.IsAny<bool>())).ReturnsAsync(node);
        _channelRepo.Setup(x => x.GetById(ValidChannelId)).ReturnsAsync(new Channel
        {
            Id = ValidChannelId,
            ChanId = 67890UL,
            SatsAmount = 1_000_000,
            Status = Channel.ChannelStatus.Open,
            FundingTx = "tx",
            // Peer opened the channel to us: counterparty sits on the source side.
            SourceNodeId = CounterpartyNodeId,
            DestinationNodeId = node.Id,
        });

        var service = CreateService();
        var request = new RebalanceRequest(node.Id, ValidChannelId, TargetPubkey: CounterpartyPubkey,
            AmountSats: 100_000, MaxFeePct: null, TimeoutSeconds: Constants.DEFAULT_REBALANCE_TIMEOUT_SECONDS);

        await FluentActions.Awaiting(() => service.RebalanceAsync(request))
            .Should().ThrowAsync<ArgumentException>()
            .WithMessage("*same as the source channel's counterparty peer*");
    }

    [Fact]
    public async Task RebalanceAsync_UserSuppliedFeePct_PersistedAsIs()
    {
        var node = CreateNode();
        _nodeRepo.Setup(x => x.GetById(node.Id, It.IsAny<bool>())).ReturnsAsync(node);
        StubRepoForCapture();

        // Probe returns NoRoute so we short-circuit before payment.
        _lightning.Setup(x => x.AddInvoiceAsync(node, It.IsAny<long>(), It.IsAny<string>(), It.IsAny<long>()))
            .ReturnsAsync(new AddInvoiceResponse { PaymentRequest = "lnbc..." });
        _lightning.Setup(x => x.SendPaymentV2Async(node, It.IsAny<string>(), It.IsAny<long>(), It.IsAny<long>(),
                It.IsAny<ulong[]?>(), It.IsAny<string?>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Payment { Status = Payment.Types.PaymentStatus.Failed, FailureReason = PaymentFailureReason.FailureReasonNoRoute });

        var service = CreateService();
        var request = new RebalanceRequest(NodeId: node.Id, ValidChannelId, ValidTargetPubkey,
            AmountSats: 100_000, MaxFeePct: 0.1234, TimeoutSeconds: Constants.DEFAULT_REBALANCE_TIMEOUT_SECONDS);

        var result = await service.RebalanceAsync(request);

        result.MaxFeePct.Should().Be(0.1234);
    }


    [Fact]
    public async Task RebalanceAsync_UserSuppliedBackoffRatio_Persisted()
    {
        var node = CreateNode();
        _nodeRepo.Setup(x => x.GetById(node.Id, It.IsAny<bool>())).ReturnsAsync(node);
        StubRepoForCapture();
        _lightning.Setup(x => x.AddInvoiceAsync(node, It.IsAny<long>(), It.IsAny<string>(), It.IsAny<long>()))
            .ReturnsAsync(new AddInvoiceResponse { PaymentRequest = "lnbc..." });
        _lightning.Setup(x => x.SendPaymentV2Async(node, It.IsAny<string>(), It.IsAny<long>(), It.IsAny<long>(),
                It.IsAny<ulong[]?>(), It.IsAny<string?>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Payment { Status = Payment.Types.PaymentStatus.Failed, FailureReason = PaymentFailureReason.FailureReasonNoRoute });

        var service = CreateService();
        var request = new RebalanceRequest(NodeId: node.Id, ValidChannelId, ValidTargetPubkey,
            AmountSats: 100_000, MaxFeePct: 0.025, AmountBackoffRatio: 0.8, TimeoutSeconds: Constants.DEFAULT_REBALANCE_TIMEOUT_SECONDS);

        var result = await service.RebalanceAsync(request);

        result.AmountBackoffRatio.Should().Be(0.8);
    }

    [Fact]
    public async Task RebalanceAsync_NullBackoffRatio_PersistedAsNull()
    {
        var node = CreateNode();
        _nodeRepo.Setup(x => x.GetById(node.Id, It.IsAny<bool>())).ReturnsAsync(node);
        StubRepoForCapture();
        _lightning.Setup(x => x.AddInvoiceAsync(node, It.IsAny<long>(), It.IsAny<string>(), It.IsAny<long>()))
            .ReturnsAsync(new AddInvoiceResponse { PaymentRequest = "lnbc..." });
        _lightning.Setup(x => x.SendPaymentV2Async(node, It.IsAny<string>(), It.IsAny<long>(), It.IsAny<long>(),
                It.IsAny<ulong[]?>(), It.IsAny<string?>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Payment { Status = Payment.Types.PaymentStatus.Failed, FailureReason = PaymentFailureReason.FailureReasonNoRoute });

        var service = CreateService();
        var request = new RebalanceRequest(NodeId: node.Id, ValidChannelId, ValidTargetPubkey,
            AmountSats: 100_000, MaxFeePct: 0.025, AmountBackoffRatio: null, TimeoutSeconds: Constants.DEFAULT_REBALANCE_TIMEOUT_SECONDS);

        var result = await service.RebalanceAsync(request);

        result.AmountBackoffRatio.Should().BeNull();
    }

    [Fact]
    public async Task RebalanceAsync_NoUserFeePct_FallsBackToDefaultFeePct()
    {
        // When no MaxFeePct is supplied the service must derive it from
        // Constants.REBALANCE_DEFAULT_MAX_FEE_PCT (0.05). No LND outbound-rate call should be made.
        var node = CreateNode();
        // Use a pubkey distinct from CounterpartyPubkey so the no-op guard doesn't trip.
        var peerPubkey = ValidTargetPubkey;
        _nodeRepo.Setup(x => x.GetById(node.Id, It.IsAny<bool>())).ReturnsAsync(node);
        StubRepoForCapture();
        _lightning.Setup(x => x.AddInvoiceAsync(node, It.IsAny<long>(), It.IsAny<string>(), It.IsAny<long>()))
            .ReturnsAsync(new AddInvoiceResponse { PaymentRequest = "lnbc..." });

        // Make the rebalance succeed so retry-escalation doesn't bump ppm before assertion.
        _lightning.Setup(x => x.SendPaymentV2Async(node, It.IsAny<string>(), It.IsAny<long>(), It.IsAny<long>(),
            It.IsAny<ulong[]?>(), It.IsAny<string?>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Payment { Status = Payment.Types.PaymentStatus.Succeeded, FeeMsat = 1000 });

        var service = CreateService();
        var request = new RebalanceRequest(NodeId: node.Id, ValidChannelId, TargetPubkey: peerPubkey,
            AmountSats: 100_000, MaxFeePct: null, TimeoutSeconds: Constants.DEFAULT_REBALANCE_TIMEOUT_SECONDS);

        var result = await service.RebalanceAsync(request);

        result.MaxFeePct.Should().Be((double)Constants.REBALANCE_DEFAULT_MAX_FEE_PCT);
        result.TargetPubkey.Should().Be(peerPubkey);
        result.AttemptNumber.Should().Be(1);
        // GetLocalOutboundFeeRatePpmByPeerAsync must NOT be called — fee is % of amount only.
        _lightning.Verify(x => x.GetLocalOutboundFeeRatePpmByPeerAsync(It.IsAny<Node>(), It.IsAny<string>()),
            Times.Never);
    }

    [Fact]
    public async Task ExecuteAsync_PaymentSucceeds_StatusSucceeded()
    {
        var node = CreateNode();
        _nodeRepo.Setup(x => x.GetById(node.Id, It.IsAny<bool>())).ReturnsAsync(node);
        StubRepoForCapture();

        _lightning.Setup(x => x.AddInvoiceAsync(node, It.IsAny<long>(), It.IsAny<string>(), It.IsAny<long>()))
            .ReturnsAsync(new AddInvoiceResponse { PaymentRequest = "lnbc..." });

        _lightning.Setup(x => x.SendPaymentV2Async(node, It.IsAny<string>(), It.IsAny<long>(), It.IsAny<long>(),
            It.IsAny<ulong[]?>(), It.IsAny<string?>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Payment
            {
                Status = Payment.Types.PaymentStatus.Succeeded,
                FeeMsat = 12_345,
                PaymentPreimage = "deadbeef",
            });

        var service = CreateService();
        var request = new RebalanceRequest(node.Id, ValidChannelId, ValidTargetPubkey, 100_000, MaxFeePct: 0.05,
            TimeoutSeconds: Constants.DEFAULT_REBALANCE_TIMEOUT_SECONDS);
        var result = await service.RebalanceAsync(request);

        result.Status.Should().Be(RebalanceStatus.Succeeded);
        result.FeePaidMsat.Should().Be(12_345);
        result.FeePaidSats.Should().Be(12);
        result.PreimageHex.Should().Be("deadbeef");
    }

    [Fact]
    public async Task ExecuteAsync_InvoiceExpiryCoversFullRetryWindowPlusBuffer()
    {
        // Invoice expiry must outlive the worst-case retry timeline so a delayed attempt
        // still has a live invoice when SendPaymentV2 fires. With defaults that's
        // TimeoutSeconds + (initial + initial*mult + ... over MaxAttempts-1 retries) + buffer.
        var node = CreateNode();
        _nodeRepo.Setup(x => x.GetById(node.Id, It.IsAny<bool>())).ReturnsAsync(node);
        StubRepoForCapture();

        long capturedExpiry = 0;
        _lightning.Setup(x => x.AddInvoiceAsync(node, It.IsAny<long>(), It.IsAny<string>(), It.IsAny<long>()))
            .Callback<Node, long, string, long>((_, _, _, expiry) => capturedExpiry = expiry)
            .ReturnsAsync(new AddInvoiceResponse { PaymentRequest = "lnbc..." });
        _lightning.Setup(x => x.SendPaymentV2Async(node, It.IsAny<string>(), It.IsAny<long>(), It.IsAny<long>(),
                It.IsAny<ulong[]?>(), It.IsAny<string?>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Payment { Status = Payment.Types.PaymentStatus.Failed, FailureReason = PaymentFailureReason.FailureReasonNoRoute });

        var service = CreateService();
        // TimeoutSeconds=60, MaxAttempts=3 → backoff = 60 + 120 = 180 → 60 + 180 + buffer.
        var request = new RebalanceRequest(node.Id, ValidChannelId, ValidTargetPubkey, 100_000, MaxFeePct: 0.05,
            TimeoutSeconds: 60, MaxAttempts: 3);

        await service.RebalanceAsync(request);

        long expected = 60
            + Constants.REBALANCE_INITIAL_RETRY_DELAY_SECONDS
            + (long)(Constants.REBALANCE_INITIAL_RETRY_DELAY_SECONDS * Constants.REBALANCE_RETRY_BACKOFF_MULTIPLIER)
            + 60;
        capturedExpiry.Should().Be(expected);
        capturedExpiry.Should().BeGreaterThan(60 + 60, "old TimeoutSeconds+60 expiry was demonstrably too short");
    }

    [Fact]
    public async Task ExecuteAsync_InvoiceExpiry_MaxAttempts1_OmitsBackoffSum()
    {
        var node = CreateNode();
        _nodeRepo.Setup(x => x.GetById(node.Id, It.IsAny<bool>())).ReturnsAsync(node);
        StubRepoForCapture();

        long capturedExpiry = 0;
        _lightning.Setup(x => x.AddInvoiceAsync(node, It.IsAny<long>(), It.IsAny<string>(), It.IsAny<long>()))
            .Callback<Node, long, string, long>((_, _, _, expiry) => capturedExpiry = expiry)
            .ReturnsAsync(new AddInvoiceResponse { PaymentRequest = "lnbc..." });
        _lightning.Setup(x => x.SendPaymentV2Async(node, It.IsAny<string>(), It.IsAny<long>(), It.IsAny<long>(),
                It.IsAny<ulong[]?>(), It.IsAny<string?>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Payment { Status = Payment.Types.PaymentStatus.Failed, FailureReason = PaymentFailureReason.FailureReasonNoRoute });

        var service = CreateService();
        var request = new RebalanceRequest(node.Id, ValidChannelId, ValidTargetPubkey, 100_000, MaxFeePct: 0.05,
            TimeoutSeconds: 45, MaxAttempts: 1);

        await service.RebalanceAsync(request);

        capturedExpiry.Should().Be(45 + 60);
    }

    [Fact]
    public async Task RebalanceAsync_TimeoutZero_FallsBackToTheDefaultTimeout()
    {
        // Callers that pass TimeoutSeconds: 0 (what gRPC sends when the field is left unset)
        // must land on Constants.DEFAULT_REBALANCE_TIMEOUT_SECONDS, not on a hardcoded 60s.
        var node = CreateNode();
        _nodeRepo.Setup(x => x.GetById(node.Id, It.IsAny<bool>())).ReturnsAsync(node);
        StubRepoForCapture();

        var capturedTimeout = 0;
        _lightning.Setup(x => x.AddInvoiceAsync(node, It.IsAny<long>(), It.IsAny<string>(), It.IsAny<long>()))
            .ReturnsAsync(new AddInvoiceResponse { PaymentRequest = "lnbc..." });
        _lightning.Setup(x => x.SendPaymentV2Async(node, It.IsAny<string>(), It.IsAny<long>(), It.IsAny<long>(),
                It.IsAny<ulong[]?>(), It.IsAny<string?>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .Callback<Node, string, long, long, ulong[]?, string?, int, CancellationToken>(
                (_, _, _, _, _, _, timeout, _) => capturedTimeout = timeout)
            .ReturnsAsync(new Payment { Status = Payment.Types.PaymentStatus.Failed, FailureReason = PaymentFailureReason.FailureReasonNoRoute });

        var service = CreateService();
        var result = await service.RebalanceAsync(
            new RebalanceRequest(node.Id, ValidChannelId, ValidTargetPubkey, 100_000, MaxFeePct: 0.05, TimeoutSeconds: 0));

        result.TimeoutSeconds.Should().Be(Constants.DEFAULT_REBALANCE_TIMEOUT_SECONDS);
        capturedTimeout.Should().Be(Constants.DEFAULT_REBALANCE_TIMEOUT_SECONDS);
    }

    [Fact]
    public async Task ExecuteAsync_PersistsPaymentHashHexBeforePayment()
    {
        // The monitor job depends on the payment hash being on the row before SendPaymentV2 is
        // dispatched — otherwise a process crash mid-stream leaves nothing for the
        // reconciliation lookup to use.
        var node = CreateNode();
        _nodeRepo.Setup(x => x.GetById(node.Id, It.IsAny<bool>())).ReturnsAsync(node);
        StubRepoForCapture();

        var hashBytes = new byte[] { 0xAB, 0xCD, 0xEF, 0x01 };
        _lightning.Setup(x => x.AddInvoiceAsync(node, It.IsAny<long>(), It.IsAny<string>(), It.IsAny<long>()))
            .ReturnsAsync(new AddInvoiceResponse
            {
                PaymentRequest = "lnbc...",
                RHash = Google.Protobuf.ByteString.CopyFrom(hashBytes),
            });

        string? hashAtDispatch = null;
        _lightning.Setup(x => x.SendPaymentV2Async(node, It.IsAny<string>(), It.IsAny<long>(), It.IsAny<long>(),
                It.IsAny<ulong[]?>(), It.IsAny<string?>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .Callback<Node, string, long, long, ulong[]?, string?, int, CancellationToken>((_, _, _, _, _, _, _, _) =>
            {
                // By the time SendPaymentV2 is dispatched, the hash must already be on the row.
                var captured = _rebalanceRepo.Invocations
                    .Where(i => i.Method.Name == nameof(IRebalanceRepository.Update))
                    .Select(i => (Rebalance)i.Arguments[0])
                    .LastOrDefault();
                hashAtDispatch = captured?.PaymentHashHex;
            })
            .ReturnsAsync(new Payment { Status = Payment.Types.PaymentStatus.Failed, FailureReason = PaymentFailureReason.FailureReasonNoRoute });

        var service = CreateService();
        var request = new RebalanceRequest(node.Id, ValidChannelId, ValidTargetPubkey, 100_000, MaxFeePct: 0.05,
            TimeoutSeconds: Constants.DEFAULT_REBALANCE_TIMEOUT_SECONDS);
        var result = await service.RebalanceAsync(request);

        result.PaymentHashHex.Should().Be("abcdef01");
        hashAtDispatch.Should().Be("abcdef01");
    }

    [Fact]
    public async Task ExecuteAsync_ProbeNoRoute_StatusNoRoute_RetryScheduled()
    {
        var node = CreateNode();
        _nodeRepo.Setup(x => x.GetById(node.Id, It.IsAny<bool>())).ReturnsAsync(node);
        StubRepoForCapture();
        _lightning.Setup(x => x.AddInvoiceAsync(node, It.IsAny<long>(), It.IsAny<string>(), It.IsAny<long>()))
            .ReturnsAsync(new AddInvoiceResponse { PaymentRequest = "lnbc..." });
        _lightning.Setup(x => x.SendPaymentV2Async(node, It.IsAny<string>(), It.IsAny<long>(), It.IsAny<long>(),
                It.IsAny<ulong[]?>(), It.IsAny<string?>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Payment { Status = Payment.Types.PaymentStatus.Failed, FailureReason = PaymentFailureReason.FailureReasonNoRoute });

        var service = CreateService();
        var request = new RebalanceRequest(node.Id, ValidChannelId, ValidTargetPubkey, 100_000, MaxFeePct: 0.05,
            TimeoutSeconds: Constants.DEFAULT_REBALANCE_TIMEOUT_SECONDS);
        var result = await service.RebalanceAsync(request);

        // After scheduling a retry, AttemptNumber is bumped to 2 and Status is reset to Pending.
        result.Status.Should().Be(RebalanceStatus.Pending);
        result.AttemptNumber.Should().Be(2);
        _scheduler.Verify(s => s.ScheduleJob(It.IsAny<IJobDetail>(), It.IsAny<ITrigger>(), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task ExecuteAsync_PaymentInsufficientBalance_NoRetryScheduled()
    {
        var node = CreateNode();
        _nodeRepo.Setup(x => x.GetById(node.Id, It.IsAny<bool>())).ReturnsAsync(node);
        StubRepoForCapture();
        _lightning.Setup(x => x.AddInvoiceAsync(node, It.IsAny<long>(), It.IsAny<string>(), It.IsAny<long>()))
            .ReturnsAsync(new AddInvoiceResponse { PaymentRequest = "lnbc..." });
        _lightning.Setup(x => x.SendPaymentV2Async(node, It.IsAny<string>(), It.IsAny<long>(), It.IsAny<long>(),
            It.IsAny<ulong[]?>(), It.IsAny<string?>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Payment
            {
                Status = Payment.Types.PaymentStatus.Failed,
                FailureReason = PaymentFailureReason.FailureReasonInsufficientBalance,
            });

        var service = CreateService();
        var request = new RebalanceRequest(node.Id, ValidChannelId, ValidTargetPubkey, 100_000, MaxFeePct: 0.05,
            TimeoutSeconds: Constants.DEFAULT_REBALANCE_TIMEOUT_SECONDS);
        var result = await service.RebalanceAsync(request);

        result.Status.Should().Be(RebalanceStatus.InsufficientBalance);
        result.AttemptNumber.Should().Be(1);
        _scheduler.Verify(s => s.ScheduleJob(It.IsAny<IJobDetail>(), It.IsAny<ITrigger>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task ExecuteAsync_ScheduleRetry_EscalatesFeePctFromInitialToRetry()
    {
        var node = CreateNode();
        _nodeRepo.Setup(x => x.GetById(node.Id, It.IsAny<bool>())).ReturnsAsync(node);

        StubRepoForCapture();
        _lightning.Setup(x => x.AddInvoiceAsync(node, It.IsAny<long>(), It.IsAny<string>(), It.IsAny<long>()))
            .ReturnsAsync(new AddInvoiceResponse { PaymentRequest = "lnbc..." });
        _lightning.Setup(x => x.SendPaymentV2Async(node, It.IsAny<string>(), It.IsAny<long>(), It.IsAny<long>(),
                It.IsAny<ulong[]?>(), It.IsAny<string?>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Payment { Status = Payment.Types.PaymentStatus.Failed, FailureReason = PaymentFailureReason.FailureReasonNoRoute });

        var service = CreateService();
        var request = new RebalanceRequest(node.Id, ValidChannelId, TargetPubkey: ValidTargetPubkey,
            AmountSats: 100_000, MaxFeePct: null, TimeoutSeconds: Constants.DEFAULT_REBALANCE_TIMEOUT_SECONDS);
        var result = await service.RebalanceAsync(request);

        var initialPct = (double)Constants.REBALANCE_DEFAULT_MAX_FEE_PCT;
        var retryPct = (double)Constants.REBALANCE_DEFAULT_RETRY_MAX_FEE_PCT;
        // Retry keeps the higher of initial and retry caps.
        result.MaxFeePct.Should().Be(Math.Max(initialPct, retryPct));
        result.AttemptNumber.Should().Be(2);
    }

    [Fact]
    public async Task ExecuteAsync_PerRowRetryMaxFeePct_OverridesConstantOnEscalation()
    {
        var node = CreateNode();
        _nodeRepo.Setup(x => x.GetById(node.Id, It.IsAny<bool>())).ReturnsAsync(node);
        StubRepoForCapture();
        _lightning.Setup(x => x.AddInvoiceAsync(node, It.IsAny<long>(), It.IsAny<string>(), It.IsAny<long>()))
            .ReturnsAsync(new AddInvoiceResponse { PaymentRequest = "lnbc..." });
        _lightning.Setup(x => x.SendPaymentV2Async(node, It.IsAny<string>(), It.IsAny<long>(), It.IsAny<long>(),
                It.IsAny<ulong[]?>(), It.IsAny<string?>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Payment { Status = Payment.Types.PaymentStatus.Failed, FailureReason = PaymentFailureReason.FailureReasonNoRoute });

        var service = CreateService();
        // Per-row retry pct = 0.09%, overriding the constant default
        // (REBALANCE_DEFAULT_RETRY_MAX_FEE_PCT = 0.05%). After one NoRoute the
        // escalated cap should be max(initial=0.05%, retry=0.09%) = 0.09%.
        var request = new RebalanceRequest(node.Id, ValidChannelId, TargetPubkey: ValidTargetPubkey,
            AmountSats: 100_000, MaxFeePct: null, RetryMaxFeePct: 0.09, TimeoutSeconds: Constants.DEFAULT_REBALANCE_TIMEOUT_SECONDS);
        var result = await service.RebalanceAsync(request);

        result.MaxFeePct.Should().Be(0.09);
        result.AttemptNumber.Should().Be(2);
        result.RetryMaxFeePct.Should().Be(0.09);
    }

    [Fact]
    public async Task ExecuteAsync_PerRowMaxAttempts1_NoRetryScheduled()
    {
        var node = CreateNode();
        _nodeRepo.Setup(x => x.GetById(node.Id, It.IsAny<bool>())).ReturnsAsync(node);
        StubRepoForCapture();
        _lightning.Setup(x => x.AddInvoiceAsync(node, It.IsAny<long>(), It.IsAny<string>(), It.IsAny<long>()))
            .ReturnsAsync(new AddInvoiceResponse { PaymentRequest = "lnbc..." });
        _lightning.Setup(x => x.SendPaymentV2Async(node, It.IsAny<string>(), It.IsAny<long>(), It.IsAny<long>(),
                It.IsAny<ulong[]?>(), It.IsAny<string?>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Payment { Status = Payment.Types.PaymentStatus.Failed, FailureReason = PaymentFailureReason.FailureReasonNoRoute });

        var service = CreateService();
        // MaxAttempts=1 → first attempt is also the last; no Quartz retry should be scheduled.
        var request = new RebalanceRequest(node.Id, ValidChannelId, ValidTargetPubkey,
            AmountSats: 100_000, MaxFeePct: 0.025, MaxAttempts: 1, TimeoutSeconds: Constants.DEFAULT_REBALANCE_TIMEOUT_SECONDS);
        var result = await service.RebalanceAsync(request);

        result.Status.Should().Be(RebalanceStatus.NoRoute);
        result.AttemptNumber.Should().Be(1);
        _scheduler.Verify(s => s.ScheduleJob(It.IsAny<IJobDetail>(), It.IsAny<ITrigger>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task ExecuteAsync_AtMaxAttempts_NoFurtherRetryScheduled()
    {
        var node = CreateNode();
        _nodeRepo.Setup(x => x.GetById(node.Id, It.IsAny<bool>())).ReturnsAsync(node);

        // Pre-built rebalance row at the max attempt. ExecuteAsync should not schedule another retry.
        var existing = new Rebalance
        {
            Id = 100,
            NodeId = node.Id,
            Node = node,
            Status = RebalanceStatus.Pending,
            AttemptNumber = Constants.REBALANCE_MAX_ATTEMPTS,
            RequestedAmountSats = 100_000,
            SatsAmount = 100_000,
            MaxFeePct = 0.05,
            TimeoutSeconds = 60,
        };
        _rebalanceRepo.Setup(r => r.GetById(existing.Id)).ReturnsAsync(existing);
        _rebalanceRepo.Setup(r => r.Update(It.IsAny<Rebalance>())).Returns((true, (string?)null));

        _lightning.Setup(x => x.AddInvoiceAsync(node, It.IsAny<long>(), It.IsAny<string>(), It.IsAny<long>()))
            .ReturnsAsync(new AddInvoiceResponse { PaymentRequest = "lnbc..." });
        _lightning.Setup(x => x.SendPaymentV2Async(node, It.IsAny<string>(), It.IsAny<long>(), It.IsAny<long>(),
                It.IsAny<ulong[]?>(), It.IsAny<string?>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Payment { Status = Payment.Types.PaymentStatus.Failed, FailureReason = PaymentFailureReason.FailureReasonNoRoute });

        var service = CreateService();
        var result = await service.ExecuteAsync(existing.Id);

        result.Status.Should().Be(RebalanceStatus.NoRoute); // terminal, no escalation
        result.AttemptNumber.Should().Be(Constants.REBALANCE_MAX_ATTEMPTS);
        _scheduler.Verify(s => s.ScheduleJob(It.IsAny<IJobDetail>(), It.IsAny<ITrigger>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task RebalanceAsync_AuditsInitiation()
    {
        var node = CreateNode();
        _nodeRepo.Setup(x => x.GetById(node.Id, It.IsAny<bool>())).ReturnsAsync(node);
        StubRepoForCapture();
        _lightning.Setup(x => x.AddInvoiceAsync(node, It.IsAny<long>(), It.IsAny<string>(), It.IsAny<long>()))
            .ReturnsAsync(new AddInvoiceResponse { PaymentRequest = "lnbc..." });
        _lightning.Setup(x => x.SendPaymentV2Async(node, It.IsAny<string>(), It.IsAny<long>(), It.IsAny<long>(),
                It.IsAny<ulong[]?>(), It.IsAny<string?>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Payment { Status = Payment.Types.PaymentStatus.Failed, FailureReason = PaymentFailureReason.FailureReasonNoRoute });

        var service = CreateService();
        await service.RebalanceAsync(new RebalanceRequest(node.Id, ValidChannelId, ValidTargetPubkey, AmountSats: 100_000, MaxFeePct: 0.1, TimeoutSeconds: 500));

        _audit.Verify(a => a.LogAsync(
                AuditActionType.RebalanceInitiated,
                AuditEventType.Attempt,
                AuditObjectType.Rebalance,
                It.IsAny<string>(),
                It.IsAny<object?>()),
            Times.Once);
    }

    /// <summary>
    /// Builds a retry row simulating the exact post-ScheduleRetryIfEligibleAsync state:
    /// Status=Pending, AttemptNumber>1, and the prior attempt's PaymentHashHex still on the row.
    /// </summary>
    private Rebalance BuildRetryRow(Node node, string priorHashHex = "deadbeef", int attempt = 2)
        => new()
        {
            Id = 200,
            NodeId = node.Id,
            Node = node,
            Status = RebalanceStatus.Pending,
            AttemptNumber = attempt,
            RequestedAmountSats = 100_000,
            SatsAmount = 100_000,
            MaxFeePct = 0.05,
            TimeoutSeconds = 60,
            PaymentHashHex = priorHashHex,
        };

    [Fact]
    public async Task ExecuteAsync_RetryPreflight_PriorSucceeded_AdoptsAndSkipsRetry()
    {
        // The lying-stream case: local row was marked Failed (then queued for retry), but LND
        // actually settled the prior attempt. The retry's pre-flight TrackPaymentV2 must catch
        // this and adopt the success WITHOUT dispatching another payment.
        var node = CreateNode();
        var rebalance = BuildRetryRow(node);
        _rebalanceRepo.Setup(r => r.GetById(rebalance.Id)).ReturnsAsync(rebalance);
        _rebalanceRepo.Setup(r => r.Update(It.IsAny<Rebalance>())).Returns((true, (string?)null));

        _lightning.Setup(x => x.TrackPaymentV2Async(node, It.IsAny<byte[]>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Payment
            {
                Status = Payment.Types.PaymentStatus.Succeeded,
                FeeMsat = 4_321,
                PaymentPreimage = "abc",
            });

        var service = CreateService();
        var result = await service.ExecuteAsync(rebalance.Id);

        result.Status.Should().Be(RebalanceStatus.Succeeded);
        result.FeePaidMsat.Should().Be(4_321);
        result.PreimageHex.Should().Be("abc");
        _lightning.Verify(x => x.AddInvoiceAsync(It.IsAny<Node>(), It.IsAny<long>(), It.IsAny<string>(), It.IsAny<long>()),
            Times.Never);
        _lightning.Verify(x => x.SendPaymentV2Async(It.IsAny<Node>(), It.IsAny<string>(), It.IsAny<long>(), It.IsAny<long>(),
                It.IsAny<ulong[]?>(), It.IsAny<string?>(), It.IsAny<int>(), It.IsAny<CancellationToken>()),
            Times.Never);
        _audit.Verify(a => a.LogAsync(
                AuditActionType.RebalanceCompleted, AuditEventType.Success,
                AuditObjectType.Rebalance, rebalance.Id.ToString(), It.IsAny<object?>()),
            Times.Once);
    }

    [Fact]
    public async Task ExecuteAsync_RetryPreflight_PriorFailed_ProceedsWithNewInvoice()
    {
        // The common retry case: LND confirms the prior attempt failed, matching the local
        // row's pre-retry state. Pre-flight must NOT bail — it must fall through to the normal
        // AddInvoice → pay flow so the retry actually runs.
        var node = CreateNode();
        var rebalance = BuildRetryRow(node);
        _rebalanceRepo.Setup(r => r.GetById(rebalance.Id)).ReturnsAsync(rebalance);
        _rebalanceRepo.Setup(r => r.Update(It.IsAny<Rebalance>())).Returns((true, (string?)null));

        _lightning.Setup(x => x.TrackPaymentV2Async(node, It.IsAny<byte[]>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Payment
            {
                Status = Payment.Types.PaymentStatus.Failed,
                FailureReason = PaymentFailureReason.FailureReasonNoRoute,
            });
        _lightning.Setup(x => x.AddInvoiceAsync(node, It.IsAny<long>(), It.IsAny<string>(), It.IsAny<long>()))
            .ReturnsAsync(new AddInvoiceResponse
            {
                PaymentRequest = "lnbc...",
                RHash = Google.Protobuf.ByteString.CopyFrom(new byte[] { 0xAA, 0xBB }),
            });
        _lightning.Setup(x => x.SendPaymentV2Async(node, It.IsAny<string>(), It.IsAny<long>(), It.IsAny<long>(),
                It.IsAny<ulong[]?>(), It.IsAny<string?>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Payment { Status = Payment.Types.PaymentStatus.Failed, FailureReason = PaymentFailureReason.FailureReasonNoRoute });

        var service = CreateService();
        var result = await service.ExecuteAsync(rebalance.Id);

        _lightning.Verify(x => x.AddInvoiceAsync(node, It.IsAny<long>(), It.IsAny<string>(), It.IsAny<long>()),
            Times.Once);
        // The new invoice's hash replaced the stale one.
        result.PaymentHashHex.Should().Be("aabb");
    }

    [Fact]
    public async Task ExecuteAsync_RetryPreflight_LndNotFound_ProceedsWithNewInvoice()
    {
        // LND returned NotFound for the prior hash. The pre-flight only short-circuits on
        // Succeeded; for null it must fall through to the normal flow so the retry runs.
        var node = CreateNode();
        var rebalance = BuildRetryRow(node);
        _rebalanceRepo.Setup(r => r.GetById(rebalance.Id)).ReturnsAsync(rebalance);
        _rebalanceRepo.Setup(r => r.Update(It.IsAny<Rebalance>())).Returns((true, (string?)null));

        _lightning.Setup(x => x.TrackPaymentV2Async(node, It.IsAny<byte[]>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Payment?)null);
        _lightning.Setup(x => x.AddInvoiceAsync(node, It.IsAny<long>(), It.IsAny<string>(), It.IsAny<long>()))
            .ReturnsAsync(new AddInvoiceResponse { PaymentRequest = "lnbc..." });
        _lightning.Setup(x => x.SendPaymentV2Async(node, It.IsAny<string>(), It.IsAny<long>(), It.IsAny<long>(),
                It.IsAny<ulong[]?>(), It.IsAny<string?>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Payment { Status = Payment.Types.PaymentStatus.Failed, FailureReason = PaymentFailureReason.FailureReasonNoRoute });

        var service = CreateService();
        await service.ExecuteAsync(rebalance.Id);

        _lightning.Verify(x => x.AddInvoiceAsync(node, It.IsAny<long>(), It.IsAny<string>(), It.IsAny<long>()),
            Times.Once);
    }

    [Theory]
    [InlineData(Payment.Types.PaymentStatus.InFlight)]
    [InlineData(Payment.Types.PaymentStatus.Initiated)]
    public async Task ExecuteAsync_RetryPreflight_PriorStillInFlight_SchedulesRetryWithoutRepaying(
        Payment.Types.PaymentStatus priorLndStatus)
    {
        // The prior attempt is still settling in LND (InFlight/Initiated). The pre-flight must
        // NOT dispatch another payment on top of the live one — that's the double-pay window the
        // payment-hash fix closes. Instead it leaves the in-flight payment alone and schedules a
        // retry, whose own pre-flight re-checks the hash once LND reaches a terminal state.
        var node = CreateNode();
        var rebalance = BuildRetryRow(node);
        _rebalanceRepo.Setup(r => r.GetById(rebalance.Id)).ReturnsAsync(rebalance);
        _rebalanceRepo.Setup(r => r.Update(It.IsAny<Rebalance>())).Returns((true, (string?)null));

        _lightning.Setup(x => x.TrackPaymentV2Async(node, It.IsAny<byte[]>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Payment { Status = priorLndStatus });

        var service = CreateService();
        var result = await service.ExecuteAsync(rebalance.Id);

        // No new invoice or payment — the live in-flight payment is left untouched.
        _lightning.Verify(x => x.AddInvoiceAsync(It.IsAny<Node>(), It.IsAny<long>(), It.IsAny<string>(), It.IsAny<long>()),
            Times.Never);
        _lightning.Verify(x => x.SendPaymentV2Async(It.IsAny<Node>(), It.IsAny<string>(), It.IsAny<long>(), It.IsAny<long>(),
                It.IsAny<ulong[]?>(), It.IsAny<string?>(), It.IsAny<int>(), It.IsAny<CancellationToken>()),
            Times.Never);
        // The prior attempt's hash is preserved, not overwritten by a fresh invoice.
        result.PaymentHashHex.Should().Be("deadbeef");
        // The prior payment was NOT adopted as terminal.
        result.PreimageHex.Should().BeNull();
        // A retry is queued (attempt 2 -> 3) so the true outcome gets reconciled later.
        result.Status.Should().Be(RebalanceStatus.Pending);
        result.AttemptNumber.Should().Be(3);
        _scheduler.Verify(s => s.ScheduleJob(It.IsAny<IJobDetail>(), It.IsAny<ITrigger>(), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task ExecuteAsync_FirstAttempt_NoPriorHash_SkipsPreflightTrack()
    {
        // First attempt: PaymentHashHex starts null, so the pre-flight TrackPaymentV2 must NOT
        // fire. Otherwise every fresh rebalance pays an extra LND round trip for nothing.
        var node = CreateNode();
        _nodeRepo.Setup(x => x.GetById(node.Id, It.IsAny<bool>())).ReturnsAsync(node);
        StubRepoForCapture();
        _lightning.Setup(x => x.AddInvoiceAsync(node, It.IsAny<long>(), It.IsAny<string>(), It.IsAny<long>()))
            .ReturnsAsync(new AddInvoiceResponse { PaymentRequest = "lnbc..." });
        _lightning.Setup(x => x.SendPaymentV2Async(node, It.IsAny<string>(), It.IsAny<long>(), It.IsAny<long>(),
                It.IsAny<ulong[]?>(), It.IsAny<string?>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Payment { Status = Payment.Types.PaymentStatus.Failed, FailureReason = PaymentFailureReason.FailureReasonNoRoute });

        var service = CreateService();
        await service.RebalanceAsync(new RebalanceRequest(node.Id, ValidChannelId, ValidTargetPubkey, 100_000, MaxFeePct: 0.05,
            TimeoutSeconds: Constants.DEFAULT_REBALANCE_TIMEOUT_SECONDS));

        _lightning.Verify(x => x.TrackPaymentV2Async(It.IsAny<Node>(), It.IsAny<byte[]>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task ExecuteAsync_CreatesAmountlessInvoice()
    {
        // The self-invoice must be amountless (Value 0) so the per-attempt amount can ride on
        // SendPaymentV2 and the same invoice can settle a shrunk retry.
        var node = CreateNode();
        _nodeRepo.Setup(x => x.GetById(node.Id, It.IsAny<bool>())).ReturnsAsync(node);
        StubRepoForCapture();

        long capturedInvoiceAmount = -1;
        _lightning.Setup(x => x.AddInvoiceAsync(node, It.IsAny<long>(), It.IsAny<string>(), It.IsAny<long>()))
            .Callback<Node, long, string, long>((_, amount, _, _) => capturedInvoiceAmount = amount)
            .ReturnsAsync(new AddInvoiceResponse { PaymentRequest = "lnbc..." });
        _lightning.Setup(x => x.SendPaymentV2Async(node, It.IsAny<string>(), It.IsAny<long>(), It.IsAny<long>(),
                It.IsAny<ulong[]?>(), It.IsAny<string?>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Payment { Status = Payment.Types.PaymentStatus.Failed, FailureReason = PaymentFailureReason.FailureReasonNoRoute });

        var service = CreateService();
        await service.RebalanceAsync(new RebalanceRequest(node.Id, ValidChannelId, ValidTargetPubkey, 100_000, MaxFeePct: 0.05,
            TimeoutSeconds: Constants.DEFAULT_REBALANCE_TIMEOUT_SECONDS));

        capturedInvoiceAmount.Should().Be(0);
    }

    [Fact]
    public async Task ExecuteAsync_SendsFullRequestedAmountOnFirstAttempt()
    {
        // Attempt 1 always pays the full requested amount (ratio^0 = 1), regardless of the ratio.
        var node = CreateNode();
        _nodeRepo.Setup(x => x.GetById(node.Id, It.IsAny<bool>())).ReturnsAsync(node);
        StubRepoForCapture();
        _lightning.Setup(x => x.AddInvoiceAsync(node, It.IsAny<long>(), It.IsAny<string>(), It.IsAny<long>()))
            .ReturnsAsync(new AddInvoiceResponse { PaymentRequest = "lnbc..." });

        long capturedAmount = -1;
        _lightning.Setup(x => x.SendPaymentV2Async(node, It.IsAny<string>(), It.IsAny<long>(), It.IsAny<long>(),
                It.IsAny<ulong[]?>(), It.IsAny<string?>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .Callback<Node, string, long, long, ulong[]?, string?, int, CancellationToken>((_, _, amount, _, _, _, _, _) => capturedAmount = amount)
            .ReturnsAsync(new Payment { Status = Payment.Types.PaymentStatus.Succeeded, FeeMsat = 1000 });

        var service = CreateService();
        await service.RebalanceAsync(new RebalanceRequest(node.Id, ValidChannelId, ValidTargetPubkey,
            100_000, MaxFeePct: 0.05, AmountBackoffRatio: 0.8, TimeoutSeconds: Constants.DEFAULT_REBALANCE_TIMEOUT_SECONDS));

        capturedAmount.Should().Be(100_000);
    }

    [Fact]
    public async Task ExecuteAsync_ShrinksAmountOnRetryByBackoffRatio()
    {
        // On a retry (attempt 2) with ratio 0.8, the amount paid is RequestedAmountSats × 0.8.
        var node = CreateNode();
        var rebalance = BuildRetryRow(node);
        rebalance.PaymentRequest = "lnbc-prior";
        rebalance.AmountBackoffRatio = 0.8;
        _rebalanceRepo.Setup(r => r.GetById(rebalance.Id)).ReturnsAsync(rebalance);
        _rebalanceRepo.Setup(r => r.Update(It.IsAny<Rebalance>())).Returns((true, (string?)null));
        // Prior hash known to LND as Failed → pre-flight falls through to a real retry.
        _lightning.Setup(x => x.TrackPaymentV2Async(node, It.IsAny<byte[]>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Payment { Status = Payment.Types.PaymentStatus.Failed, FailureReason = PaymentFailureReason.FailureReasonNoRoute });

        long capturedAmount = -1;
        _lightning.Setup(x => x.SendPaymentV2Async(node, It.IsAny<string>(), It.IsAny<long>(), It.IsAny<long>(),
                It.IsAny<ulong[]?>(), It.IsAny<string?>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .Callback<Node, string, long, long, ulong[]?, string?, int, CancellationToken>((_, _, amount, _, _, _, _, _) => capturedAmount = amount)
            .ReturnsAsync(new Payment { Status = Payment.Types.PaymentStatus.Succeeded, FeeMsat = 1000 });

        var service = CreateService();
        await service.ExecuteAsync(rebalance.Id);

        capturedAmount.Should().Be(80_000); // 100_000 × 0.8^(2-1)
    }

    [Fact]
    public async Task ExecuteAsync_RatioOne_DoesNotShrinkOnRetry()
    {
        // ratio = 1 ⇒ every attempt retries the full amount, even on a retry.
        var node = CreateNode();
        var rebalance = BuildRetryRow(node);
        rebalance.PaymentRequest = "lnbc-prior";
        rebalance.AmountBackoffRatio = 1.0;
        _rebalanceRepo.Setup(r => r.GetById(rebalance.Id)).ReturnsAsync(rebalance);
        _rebalanceRepo.Setup(r => r.Update(It.IsAny<Rebalance>())).Returns((true, (string?)null));
        _lightning.Setup(x => x.TrackPaymentV2Async(node, It.IsAny<byte[]>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Payment { Status = Payment.Types.PaymentStatus.Failed, FailureReason = PaymentFailureReason.FailureReasonNoRoute });

        long capturedAmount = -1;
        _lightning.Setup(x => x.SendPaymentV2Async(node, It.IsAny<string>(), It.IsAny<long>(), It.IsAny<long>(),
                It.IsAny<ulong[]?>(), It.IsAny<string?>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .Callback<Node, string, long, long, ulong[]?, string?, int, CancellationToken>((_, _, amount, _, _, _, _, _) => capturedAmount = amount)
            .ReturnsAsync(new Payment { Status = Payment.Types.PaymentStatus.Succeeded, FeeMsat = 1000 });

        var service = CreateService();
        await service.ExecuteAsync(rebalance.Id);

        capturedAmount.Should().Be(100_000);
    }

    [Fact]
    public async Task ExecuteAsync_RetryReusesExistingInvoice_DoesNotCreateNew()
    {
        // A retry row already carries the amountless invoice from the first attempt. ExecuteAsync
        // must reuse it (one invoice per rebalance), not mint a fresh one.
        var node = CreateNode();
        var rebalance = BuildRetryRow(node);
        rebalance.PaymentRequest = "lnbc-original-invoice";
        _rebalanceRepo.Setup(r => r.GetById(rebalance.Id)).ReturnsAsync(rebalance);
        _rebalanceRepo.Setup(r => r.Update(It.IsAny<Rebalance>())).Returns((true, (string?)null));
        _lightning.Setup(x => x.TrackPaymentV2Async(node, It.IsAny<byte[]>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Payment { Status = Payment.Types.PaymentStatus.Failed, FailureReason = PaymentFailureReason.FailureReasonNoRoute });

        string? paidRequest = null;
        _lightning.Setup(x => x.SendPaymentV2Async(node, It.IsAny<string>(), It.IsAny<long>(), It.IsAny<long>(),
                It.IsAny<ulong[]?>(), It.IsAny<string?>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .Callback<Node, string, long, long, ulong[]?, string?, int, CancellationToken>((_, pr, _, _, _, _, _, _) => paidRequest = pr)
            .ReturnsAsync(new Payment { Status = Payment.Types.PaymentStatus.Failed, FailureReason = PaymentFailureReason.FailureReasonNoRoute });

        var service = CreateService();
        var result = await service.ExecuteAsync(rebalance.Id);

        _lightning.Verify(x => x.AddInvoiceAsync(It.IsAny<Node>(), It.IsAny<long>(), It.IsAny<string>(), It.IsAny<long>()),
            Times.Never);
        paidRequest.Should().Be("lnbc-original-invoice");
        // The stable prior hash is preserved (single invoice across attempts).
        result.PaymentHashHex.Should().Be("deadbeef");
    }
}
