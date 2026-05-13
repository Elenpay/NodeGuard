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
        var request = new RebalanceRequest(NodeId: 1, null, null, AmountSats: 0, MaxFeePct: null);

        await FluentActions.Awaiting(() => service.RebalanceAsync(request))
            .Should().ThrowAsync<ArgumentException>()
            .WithMessage("*already at or above the requested inbound ratio*");
    }

    [Fact]
    public async Task RebalanceAsync_ProbeBackoffRatioOutOfRange_Throws()
    {
        // Ratio must be in (0, 1) exclusive. 1.0 never shrinks; 0 zeroes the next try.
        var service = CreateService();
        var request = new RebalanceRequest(NodeId: 1, null, null,
            AmountSats: 1000, MaxFeePct: null, ProbeBackoffRatio: 1.0);

        await FluentActions.Awaiting(() => service.RebalanceAsync(request))
            .Should().ThrowAsync<ArgumentException>()
            .WithMessage("*Probe backoff ratio must be in the open interval (0, 1)*");
    }

    [Fact]
    public async Task RebalanceAsync_MaxAttemptsZeroOrNegative_Throws()
    {
        var service = CreateService();
        var request = new RebalanceRequest(NodeId: 1, null, null,
            AmountSats: 1000, MaxFeePct: null, MaxAttempts: 0);

        await FluentActions.Awaiting(() => service.RebalanceAsync(request))
            .Should().ThrowAsync<ArgumentException>()
            .WithMessage("*Max attempts must be at least 1*");
    }

    [Fact]
    public async Task RebalanceAsync_RetryMaxFeePctOutOfRange_Throws()
    {
        var service = CreateService();
        var request = new RebalanceRequest(NodeId: 1, null, null,
            AmountSats: 1000, MaxFeePct: null, RetryMaxFeePct: -0.1);

        await FluentActions.Awaiting(() => service.RebalanceAsync(request))
            .Should().ThrowAsync<ArgumentException>()
            .WithMessage("*Retry max fee % must be greater than 0*");
    }

    [Fact]
    public async Task RebalanceAsync_NodeNotFound_Throws()
    {
        _nodeRepo.Setup(x => x.GetById(99, It.IsAny<bool>())).ReturnsAsync((Node?)null);
        var service = CreateService();
        var request = new RebalanceRequest(NodeId: 99, null, null, AmountSats: 1000, MaxFeePct: null);

        await FluentActions.Awaiting(() => service.RebalanceAsync(request))
            .Should().ThrowAsync<ArgumentException>();
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
        _lightning.Setup(x => x.ProbeRouteAsync(node, It.IsAny<long>(), It.IsAny<long>(),
                It.IsAny<ulong?>(), It.IsAny<string?>(), It.IsAny<double>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ProbeResult.NoRoute("test"));

        var service = CreateService();
        var request = new RebalanceRequest(NodeId: node.Id, null, null,
            AmountSats: 100_000, MaxFeePct: 0.1234);

        var result = await service.RebalanceAsync(request);

        result.MaxFeePct.Should().Be(0.1234);
    }


    [Fact]
    public async Task RebalanceAsync_UserSuppliedProbeBackoffRatio_PersistedAndPassedToProbe()
    {
        var node = CreateNode();
        _nodeRepo.Setup(x => x.GetById(node.Id, It.IsAny<bool>())).ReturnsAsync(node);
        StubRepoForCapture();
        _lightning.Setup(x => x.AddInvoiceAsync(node, It.IsAny<long>(), It.IsAny<string>(), It.IsAny<long>()))
            .ReturnsAsync(new AddInvoiceResponse { PaymentRequest = "lnbc..." });

        double capturedRatio = 0;
        _lightning.Setup(x => x.ProbeRouteAsync(node, It.IsAny<long>(), It.IsAny<long>(),
                It.IsAny<ulong?>(), It.IsAny<string?>(), It.IsAny<double>(), It.IsAny<CancellationToken>()))
            .Callback<Node, long, long, ulong?, string?, double, CancellationToken>((_, _, _, _, _, ratio, _) => capturedRatio = ratio)
            .ReturnsAsync(new ProbeResult.NoRoute("test"));

        var service = CreateService();
        var request = new RebalanceRequest(NodeId: node.Id, null, null,
            AmountSats: 100_000, MaxFeePct: 0.025, ProbeBackoffRatio: 0.8);

        var result = await service.RebalanceAsync(request);

        result.ProbeBackoffRatio.Should().Be(0.8);
        capturedRatio.Should().Be(0.8);
    }

    [Fact]
    public async Task RebalanceAsync_NullProbeBackoffRatio_FallsBackToConstant()
    {
        var node = CreateNode();
        _nodeRepo.Setup(x => x.GetById(node.Id, It.IsAny<bool>())).ReturnsAsync(node);
        StubRepoForCapture();
        _lightning.Setup(x => x.AddInvoiceAsync(node, It.IsAny<long>(), It.IsAny<string>(), It.IsAny<long>()))
            .ReturnsAsync(new AddInvoiceResponse { PaymentRequest = "lnbc..." });

        double capturedRatio = 0;
        _lightning.Setup(x => x.ProbeRouteAsync(node, It.IsAny<long>(), It.IsAny<long>(),
                It.IsAny<ulong?>(), It.IsAny<string?>(), It.IsAny<double>(), It.IsAny<CancellationToken>()))
            .Callback<Node, long, long, ulong?, string?, double, CancellationToken>((_, _, _, _, _, ratio, _) => capturedRatio = ratio)
            .ReturnsAsync(new ProbeResult.NoRoute("test"));

        var service = CreateService();
        var request = new RebalanceRequest(NodeId: node.Id, null, null,
            AmountSats: 100_000, MaxFeePct: 0.025, ProbeBackoffRatio: null);

        var result = await service.RebalanceAsync(request);

        result.ProbeBackoffRatio.Should().BeNull();
        capturedRatio.Should().Be(Constants.REBALANCE_PROBE_BACKOFF_RATIO);
    }

    [Fact]
    public async Task RebalanceAsync_NoUserFeePct_FallsBackToDefaultFeePct()
    {
        // When no MaxFeePct is supplied the service must derive it from
        // Constants.REBALANCE_DEFAULT_MAX_FEE_PCT (0.05). No LND outbound-rate call should be made.
        var node = CreateNode();
        var peerPubkey = "030000000000000000000000000000000000000000000000000000000000000002";
        _nodeRepo.Setup(x => x.GetById(node.Id, It.IsAny<bool>())).ReturnsAsync(node);
        StubRepoForCapture();
        _lightning.Setup(x => x.AddInvoiceAsync(node, It.IsAny<long>(), It.IsAny<string>(), It.IsAny<long>()))
            .ReturnsAsync(new AddInvoiceResponse { PaymentRequest = "lnbc..." });

        // Make the rebalance succeed so retry-escalation doesn't bump ppm before assertion.
        _lightning.Setup(x => x.ProbeRouteAsync(node, It.IsAny<long>(), It.IsAny<long>(),
                It.IsAny<ulong?>(), It.IsAny<string?>(), It.IsAny<double>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ProbeResult.Success(100_000, new Lnrpc.Route()));
        _lightning.Setup(x => x.SendPaymentV2Async(node, It.IsAny<string>(), It.IsAny<long>(),
            It.IsAny<ulong[]?>(), It.IsAny<string?>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Payment { Status = Payment.Types.PaymentStatus.Succeeded, FeeMsat = 1000 });

        var service = CreateService();
        var request = new RebalanceRequest(NodeId: node.Id, null, TargetPubkey: peerPubkey,
            AmountSats: 100_000, MaxFeePct: null);

        var result = await service.RebalanceAsync(request);

        result.MaxFeePct.Should().Be((double)Constants.REBALANCE_DEFAULT_MAX_FEE_PCT);
        result.TargetPubkey.Should().Be(peerPubkey);
        result.AttemptNumber.Should().Be(1);
        // GetLocalOutboundFeeRatePpmByPeerAsync must NOT be called — fee is % of amount only.
        _lightning.Verify(x => x.GetLocalOutboundFeeRatePpmByPeerAsync(It.IsAny<Node>(), It.IsAny<string>()),
            Times.Never);
    }

    [Fact]
    public async Task ExecuteAsync_ProbeSucceeds_PaymentSucceeds_StatusSucceeded()
    {
        var node = CreateNode();
        _nodeRepo.Setup(x => x.GetById(node.Id, It.IsAny<bool>())).ReturnsAsync(node);
        StubRepoForCapture();

        _lightning.Setup(x => x.AddInvoiceAsync(node, It.IsAny<long>(), It.IsAny<string>(), It.IsAny<long>()))
            .ReturnsAsync(new AddInvoiceResponse { PaymentRequest = "lnbc..." });

        var route = new Lnrpc.Route();
        route.Hops.Add(new Hop { ChanId = 1, AmtToForwardMsat = 100_000_000 });
        _lightning.Setup(x => x.ProbeRouteAsync(node, It.IsAny<long>(), It.IsAny<long>(),
                It.IsAny<ulong?>(), It.IsAny<string?>(), It.IsAny<double>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ProbeResult.Success(100_000, route));

        _lightning.Setup(x => x.SendPaymentV2Async(node, It.IsAny<string>(), It.IsAny<long>(),
            It.IsAny<ulong[]?>(), It.IsAny<string?>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Payment
            {
                Status = Payment.Types.PaymentStatus.Succeeded,
                FeeMsat = 12_345,
                PaymentPreimage = "deadbeef",
            });

        var service = CreateService();
        var request = new RebalanceRequest(node.Id, null, null, 100_000, MaxFeePct: 0.05);
        var result = await service.RebalanceAsync(request);

        result.Status.Should().Be(RebalanceStatus.Succeeded);
        result.FeePaidMsat.Should().Be(12_345);
        result.FeePaidSats.Should().Be(12);
        result.PreimageHex.Should().Be("deadbeef");
    }

    [Fact]
    public async Task ExecuteAsync_ProbeNoRoute_StatusNoRoute_RetryScheduled()
    {
        var node = CreateNode();
        _nodeRepo.Setup(x => x.GetById(node.Id, It.IsAny<bool>())).ReturnsAsync(node);
        StubRepoForCapture();
        _lightning.Setup(x => x.AddInvoiceAsync(node, It.IsAny<long>(), It.IsAny<string>(), It.IsAny<long>()))
            .ReturnsAsync(new AddInvoiceResponse { PaymentRequest = "lnbc..." });
        _lightning.Setup(x => x.ProbeRouteAsync(node, It.IsAny<long>(), It.IsAny<long>(),
                It.IsAny<ulong?>(), It.IsAny<string?>(), It.IsAny<double>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ProbeResult.NoRoute("exhausted"));

        var service = CreateService();
        var request = new RebalanceRequest(node.Id, null, null, 100_000, MaxFeePct: 0.05);
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
        _lightning.Setup(x => x.ProbeRouteAsync(node, It.IsAny<long>(), It.IsAny<long>(),
                It.IsAny<ulong?>(), It.IsAny<string?>(), It.IsAny<double>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ProbeResult.Success(100_000, new Lnrpc.Route()));
        _lightning.Setup(x => x.SendPaymentV2Async(node, It.IsAny<string>(), It.IsAny<long>(),
            It.IsAny<ulong[]?>(), It.IsAny<string?>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Payment
            {
                Status = Payment.Types.PaymentStatus.Failed,
                FailureReason = PaymentFailureReason.FailureReasonInsufficientBalance,
            });

        var service = CreateService();
        var request = new RebalanceRequest(node.Id, null, null, 100_000, MaxFeePct: 0.05);
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
        _lightning.Setup(x => x.ProbeRouteAsync(node, It.IsAny<long>(), It.IsAny<long>(),
                It.IsAny<ulong?>(), It.IsAny<string?>(), It.IsAny<double>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ProbeResult.NoRoute());

        var service = CreateService();
        var request = new RebalanceRequest(node.Id, null, TargetPubkey: null,
            AmountSats: 100_000, MaxFeePct: null);
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
        _lightning.Setup(x => x.ProbeRouteAsync(node, It.IsAny<long>(), It.IsAny<long>(),
                It.IsAny<ulong?>(), It.IsAny<string?>(), It.IsAny<double>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ProbeResult.NoRoute());

        var service = CreateService();
        // Per-row retry pct = 0.09%, overriding the constant default
        // (REBALANCE_DEFAULT_RETRY_MAX_FEE_PCT = 0.05%). After one NoRoute the
        // escalated cap should be max(initial=0.05%, retry=0.09%) = 0.09%.
        var request = new RebalanceRequest(node.Id, null, TargetPubkey: null,
            AmountSats: 100_000, MaxFeePct: null, RetryMaxFeePct: 0.09);
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
        _lightning.Setup(x => x.ProbeRouteAsync(node, It.IsAny<long>(), It.IsAny<long>(),
                It.IsAny<ulong?>(), It.IsAny<string?>(), It.IsAny<double>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ProbeResult.NoRoute());

        var service = CreateService();
        // MaxAttempts=1 → first attempt is also the last; no Quartz retry should be scheduled.
        var request = new RebalanceRequest(node.Id, null, null,
            AmountSats: 100_000, MaxFeePct: 0.025, MaxAttempts: 1);
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
        _lightning.Setup(x => x.ProbeRouteAsync(node, It.IsAny<long>(), It.IsAny<long>(),
                It.IsAny<ulong?>(), It.IsAny<string?>(), It.IsAny<double>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ProbeResult.NoRoute());

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
        _lightning.Setup(x => x.ProbeRouteAsync(node, It.IsAny<long>(), It.IsAny<long>(),
                It.IsAny<ulong?>(), It.IsAny<string?>(), It.IsAny<double>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ProbeResult.NoRoute());

        var service = CreateService();
        await service.RebalanceAsync(new RebalanceRequest(node.Id, null, null, AmountSats: 100_000, MaxFeePct: 0.1, TimeoutSeconds: 500));

        _audit.Verify(a => a.LogAsync(
                AuditActionType.RebalanceInitiated,
                AuditEventType.Attempt,
                AuditObjectType.Rebalance,
                It.IsAny<string>(),
                It.IsAny<object?>()),
            Times.Once);
    }
}
