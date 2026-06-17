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
using NodeGuard.Services;
using Quartz;

namespace NodeGuard.Jobs;

public class MonitorRebalancesJobTests
{
    private readonly Mock<ILogger<MonitorRebalancesJob>> _logger = new();
    private readonly Mock<IRebalanceRepository> _rebalanceRepo = new();
    private readonly Mock<ILightningService> _lightning = new();
    private readonly Mock<IAuditService> _audit = new();
    private readonly Mock<IJobExecutionContext> _ctx = new();

    private MonitorRebalancesJob CreateJob() => new(
        _logger.Object,
        _rebalanceRepo.Object,
        _lightning.Object,
        _audit.Object);

    private static Node Node(int id = 1) => new()
    {
        Id = id,
        Name = $"node-{id}",
        Endpoint = "localhost:10009",
        ChannelAdminMacaroon = "mac",
        PubKey = "030000000000000000000000000000000000000000000000000000000000000001",
    };

    private static Rebalance MakeRebalance(
        RebalanceStatus status,
        string? hashHex = "abcdef01",
        Node? node = null,
        DateTimeOffset? updateDatetime = null)
        => new()
        {
            Id = 42,
            NodeId = node?.Id ?? 1,
            Node = node ?? Node(),
            Status = status,
            PaymentHashHex = hashHex,
            SatsAmount = 100_000,
            RequestedAmountSats = 100_000,
            MaxFeePct = 0.05,
            // Default to "fresh" so tests don't accidentally trip the age-based stuck-row
            // fallback in the Pending/Probing null branch.
            UpdateDatetime = updateDatetime ?? DateTimeOffset.UtcNow,
        };

    [Fact]
    public async Task Execute_InFlight_LndSucceeded_RowFlipsToSucceededAndAudits()
    {
        var reb = MakeRebalance(RebalanceStatus.InFlight);
        _rebalanceRepo.Setup(r => r.GetReconcilable(It.IsAny<TimeSpan>())).ReturnsAsync(new List<Rebalance> { reb });
        _lightning.Setup(x => x.TrackPaymentV2Async(It.IsAny<Node>(), It.IsAny<byte[]>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Payment
            {
                Status = Payment.Types.PaymentStatus.Succeeded,
                FeeMsat = 12_345,
                PaymentPreimage = "deadbeef",
            });

        await CreateJob().Execute(_ctx.Object);

        reb.Status.Should().Be(RebalanceStatus.Succeeded);
        reb.FeePaidMsat.Should().Be(12_345);
        reb.PreimageHex.Should().Be("deadbeef");
        _rebalanceRepo.Verify(r => r.Update(reb), Times.Once);
        _audit.Verify(a => a.LogSystemAsync(
                AuditActionType.RebalanceCompleted, AuditEventType.Success,
                AuditObjectType.Rebalance, reb.Id.ToString(), It.IsAny<object>()),
            Times.Once);
    }

    [Fact]
    public async Task Execute_RecentlyFailed_LndSucceeded_RowCorrectedToSucceeded()
    {
        // The core bug-fix case: catch-block wrongly marked it Failed, LND actually settled.
        var reb = MakeRebalance(RebalanceStatus.Failed);
        _rebalanceRepo.Setup(r => r.GetReconcilable(It.IsAny<TimeSpan>())).ReturnsAsync(new List<Rebalance> { reb });
        _lightning.Setup(x => x.TrackPaymentV2Async(It.IsAny<Node>(), It.IsAny<byte[]>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Payment
            {
                Status = Payment.Types.PaymentStatus.Succeeded,
                FeeMsat = 5_000,
                PaymentPreimage = "cafebabe",
            });

        await CreateJob().Execute(_ctx.Object);

        reb.Status.Should().Be(RebalanceStatus.Succeeded);
        reb.PreimageHex.Should().Be("cafebabe");
        _audit.Verify(a => a.LogSystemAsync(
                AuditActionType.RebalanceCompleted, AuditEventType.Success,
                AuditObjectType.Rebalance, reb.Id.ToString(), It.IsAny<object>()),
            Times.Once);
    }

    [Fact]
    public async Task Execute_InFlight_LndFailedNoRoute_RowFlipsToNoRoute()
    {
        var reb = MakeRebalance(RebalanceStatus.InFlight);
        _rebalanceRepo.Setup(r => r.GetReconcilable(It.IsAny<TimeSpan>())).ReturnsAsync(new List<Rebalance> { reb });
        _lightning.Setup(x => x.TrackPaymentV2Async(It.IsAny<Node>(), It.IsAny<byte[]>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Payment
            {
                Status = Payment.Types.PaymentStatus.Failed,
                FailureReason = PaymentFailureReason.FailureReasonNoRoute,
            });

        await CreateJob().Execute(_ctx.Object);

        reb.Status.Should().Be(RebalanceStatus.NoRoute);
        _audit.Verify(a => a.LogSystemAsync(
                AuditActionType.RebalanceCompleted, AuditEventType.Failure,
                AuditObjectType.Rebalance, reb.Id.ToString(), It.IsAny<object>()),
            Times.Once);
    }

    [Fact]
    public async Task Execute_InFlight_LndNotFound_RowMarkedFailed()
    {
        // TrackPaymentV2Async returns null on StatusCode.NotFound. For non-terminal rows the
        // monitor treats this as "LND never saw it" and flips them to Failed.
        var reb = MakeRebalance(RebalanceStatus.InFlight);
        _rebalanceRepo.Setup(r => r.GetReconcilable(It.IsAny<TimeSpan>())).ReturnsAsync(new List<Rebalance> { reb });
        _lightning.Setup(x => x.TrackPaymentV2Async(It.IsAny<Node>(), It.IsAny<byte[]>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Payment?)null);

        await CreateJob().Execute(_ctx.Object);

        reb.Status.Should().Be(RebalanceStatus.Failed);
        _rebalanceRepo.Verify(r => r.Update(reb), Times.Once);
    }

    [Fact]
    public async Task Execute_Probing_LndNotFound_RowLeftAlone()
    {
        // ExecuteAsync persists PaymentHashHex right after AddInvoice but does not call
        // SendPaymentV2 until the probe succeeds. While Status=Probing, "no record in LND" is
        // the expected state — flipping to Failed here would kill an in-progress probe.
        var reb = MakeRebalance(RebalanceStatus.Probing);
        _rebalanceRepo.Setup(r => r.GetReconcilable(It.IsAny<TimeSpan>())).ReturnsAsync(new List<Rebalance> { reb });
        _lightning.Setup(x => x.TrackPaymentV2Async(It.IsAny<Node>(), It.IsAny<byte[]>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Payment?)null);

        await CreateJob().Execute(_ctx.Object);

        reb.Status.Should().Be(RebalanceStatus.Probing);
        _rebalanceRepo.Verify(r => r.Update(It.IsAny<Rebalance>()), Times.Never);
        _audit.Verify(a => a.LogSystemAsync(
                AuditActionType.RebalanceCompleted, AuditEventType.Failure,
                AuditObjectType.Rebalance, It.IsAny<string>(), It.IsAny<object>()),
            Times.Never);
    }

    [Fact]
    public async Task Execute_Pending_LndNotFound_RowLeftAlone()
    {
        // Same reasoning as Probing — the brief Pending+hash window inside ExecuteAsync
        // between AddInvoice and the Status=Probing flip. SendPaymentV2 hasn't fired; LND
        // can't possibly know about the hash yet.
        var reb = MakeRebalance(RebalanceStatus.Pending);
        _rebalanceRepo.Setup(r => r.GetReconcilable(It.IsAny<TimeSpan>())).ReturnsAsync(new List<Rebalance> { reb });
        _lightning.Setup(x => x.TrackPaymentV2Async(It.IsAny<Node>(), It.IsAny<byte[]>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Payment?)null);

        await CreateJob().Execute(_ctx.Object);

        reb.Status.Should().Be(RebalanceStatus.Pending);
        _rebalanceRepo.Verify(r => r.Update(It.IsAny<Rebalance>()), Times.Never);
        _audit.Verify(a => a.LogSystemAsync(
                AuditActionType.RebalanceCompleted, AuditEventType.Failure,
                AuditObjectType.Rebalance, It.IsAny<string>(), It.IsAny<object>()),
            Times.Never);
    }

    [Fact]
    public async Task Execute_Pending_LndNotFound_OlderThanWindow_FlipsToFailed()
    {
        // Safety net for a Pending row stuck past the reconciliation window — typically a
        // crash mid-ExecuteAsync with no Quartz retry queued. The age-based fallback flips it
        // to Failed so it doesn't sit non-terminal forever.
        var stale = DateTimeOffset.UtcNow.AddHours(-(Constants.REBALANCE_RECONCILE_TERMINAL_WINDOW_HOURS + 1));
        var reb = MakeRebalance(RebalanceStatus.Pending, updateDatetime: stale);
        _rebalanceRepo.Setup(r => r.GetReconcilable(It.IsAny<TimeSpan>())).ReturnsAsync(new List<Rebalance> { reb });
        _lightning.Setup(x => x.TrackPaymentV2Async(It.IsAny<Node>(), It.IsAny<byte[]>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Payment?)null);

        await CreateJob().Execute(_ctx.Object);

        reb.Status.Should().Be(RebalanceStatus.Failed);
        _rebalanceRepo.Verify(r => r.Update(reb), Times.Once);
        _audit.Verify(a => a.LogSystemAsync(
                AuditActionType.RebalanceCompleted, AuditEventType.Failure,
                AuditObjectType.Rebalance, reb.Id.ToString(), It.IsAny<object>()),
            Times.Once);
    }

    [Fact]
    public async Task Execute_Pending_LndFailedPayment_RowLeftAlone()
    {
        // The oscillation case: ScheduleRetryIfEligibleAsync left the row at Pending with the
        // PRIOR attempt's hash. LND still has that prior payment record (Failed). The monitor
        // must NOT write the prior failure onto a queued-for-retry row — local execution
        // (ExecuteAsync's pre-flight) is the only thing allowed to touch it.
        var reb = MakeRebalance(RebalanceStatus.Pending);
        _rebalanceRepo.Setup(r => r.GetReconcilable(It.IsAny<TimeSpan>())).ReturnsAsync(new List<Rebalance> { reb });
        _lightning.Setup(x => x.TrackPaymentV2Async(It.IsAny<Node>(), It.IsAny<byte[]>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Payment
            {
                Status = Payment.Types.PaymentStatus.Failed,
                FailureReason = PaymentFailureReason.FailureReasonNoRoute,
            });

        await CreateJob().Execute(_ctx.Object);

        reb.Status.Should().Be(RebalanceStatus.Pending);
        _rebalanceRepo.Verify(r => r.Update(It.IsAny<Rebalance>()), Times.Never);
        _audit.Verify(a => a.LogSystemAsync(
                It.IsAny<AuditActionType>(), It.IsAny<AuditEventType>(),
                It.IsAny<AuditObjectType>(), It.IsAny<string>(), It.IsAny<object>()),
            Times.Never);
    }

    [Fact]
    public async Task Execute_Pending_LndSucceededPayment_RowLeftAlone()
    {
        // The lying-stream variant of the oscillation: prior attempt actually settled in LND
        // but the local stream said Failed. The monitor still must NOT adopt that success
        // here — the row is Pending (queued for retry), and ExecuteAsync's pre-flight in the
        // retry path is the component responsible for adopting the prior success.
        var reb = MakeRebalance(RebalanceStatus.Pending);
        _rebalanceRepo.Setup(r => r.GetReconcilable(It.IsAny<TimeSpan>())).ReturnsAsync(new List<Rebalance> { reb });
        _lightning.Setup(x => x.TrackPaymentV2Async(It.IsAny<Node>(), It.IsAny<byte[]>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Payment
            {
                Status = Payment.Types.PaymentStatus.Succeeded,
                FeeMsat = 1_000,
                PaymentPreimage = "deadbeef",
            });

        await CreateJob().Execute(_ctx.Object);

        reb.Status.Should().Be(RebalanceStatus.Pending);
        _rebalanceRepo.Verify(r => r.Update(It.IsAny<Rebalance>()), Times.Never);
    }

    [Fact]
    public async Task Execute_Probing_LndHasRecord_RowLeftAlone()
    {
        // While Status=Probing, the local ExecuteAsync owns the row — the monitor stays back
        // regardless of what LND has on the hash. Covers the normal in-progress probe.
        var reb = MakeRebalance(RebalanceStatus.Probing);
        _rebalanceRepo.Setup(r => r.GetReconcilable(It.IsAny<TimeSpan>())).ReturnsAsync(new List<Rebalance> { reb });
        _lightning.Setup(x => x.TrackPaymentV2Async(It.IsAny<Node>(), It.IsAny<byte[]>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Payment { Status = Payment.Types.PaymentStatus.InFlight });

        await CreateJob().Execute(_ctx.Object);

        reb.Status.Should().Be(RebalanceStatus.Probing);
        _rebalanceRepo.Verify(r => r.Update(It.IsAny<Rebalance>()), Times.Never);
    }

    [Fact]
    public async Task Execute_RecentlyFailed_LndNotFound_RowLeftAlone()
    {
        // Track returning null for an already-terminal row is treated as "no LND truth" —
        // we don't reopen a Failed row on transient errors.
        var reb = MakeRebalance(RebalanceStatus.Failed);
        _rebalanceRepo.Setup(r => r.GetReconcilable(It.IsAny<TimeSpan>())).ReturnsAsync(new List<Rebalance> { reb });
        _lightning.Setup(x => x.TrackPaymentV2Async(It.IsAny<Node>(), It.IsAny<byte[]>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Payment?)null);

        await CreateJob().Execute(_ctx.Object);

        reb.Status.Should().Be(RebalanceStatus.Failed);
        _rebalanceRepo.Verify(r => r.Update(It.IsAny<Rebalance>()), Times.Never);
    }

    [Fact]
    public async Task Execute_LndStillInFlight_RowUntouched()
    {
        var reb = MakeRebalance(RebalanceStatus.InFlight);
        _rebalanceRepo.Setup(r => r.GetReconcilable(It.IsAny<TimeSpan>())).ReturnsAsync(new List<Rebalance> { reb });
        _lightning.Setup(x => x.TrackPaymentV2Async(It.IsAny<Node>(), It.IsAny<byte[]>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Payment { Status = Payment.Types.PaymentStatus.InFlight });

        await CreateJob().Execute(_ctx.Object);

        reb.Status.Should().Be(RebalanceStatus.InFlight);
        _rebalanceRepo.Verify(r => r.Update(It.IsAny<Rebalance>()), Times.Never);
    }

    [Fact]
    public async Task Execute_StatusUnchanged_DoesNotUpdateOrAudit()
    {
        // LND confirms the same status the DB already has — no need to write or audit.
        var reb = MakeRebalance(RebalanceStatus.Failed);
        _rebalanceRepo.Setup(r => r.GetReconcilable(It.IsAny<TimeSpan>())).ReturnsAsync(new List<Rebalance> { reb });
        _lightning.Setup(x => x.TrackPaymentV2Async(It.IsAny<Node>(), It.IsAny<byte[]>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Payment
            {
                Status = Payment.Types.PaymentStatus.Failed,
                FailureReason = PaymentFailureReason.FailureReasonError,
            });

        await CreateJob().Execute(_ctx.Object);

        reb.Status.Should().Be(RebalanceStatus.Failed);
        _rebalanceRepo.Verify(r => r.Update(It.IsAny<Rebalance>()), Times.Never);
        _audit.Verify(a => a.LogSystemAsync(
                It.IsAny<AuditActionType>(), It.IsAny<AuditEventType>(),
                It.IsAny<AuditObjectType>(), It.IsAny<string>(), It.IsAny<object>()),
            Times.Never);
    }

    [Fact]
    public async Task Execute_PerRowExceptionDoesNotAbortOthers()
    {
        // One row's TrackPaymentV2 throws; the other should still be reconciled.
        var failing = MakeRebalance(RebalanceStatus.InFlight, hashHex: "11111111");
        failing.Id = 1;
        var succeeding = MakeRebalance(RebalanceStatus.InFlight, hashHex: "22222222");
        succeeding.Id = 2;

        _rebalanceRepo.Setup(r => r.GetReconcilable(It.IsAny<TimeSpan>()))
            .ReturnsAsync(new List<Rebalance> { failing, succeeding });

        _lightning.Setup(x => x.TrackPaymentV2Async(It.IsAny<Node>(),
                It.Is<byte[]>(b => b.Length == 4 && b[0] == 0x11),
                It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("boom"));
        _lightning.Setup(x => x.TrackPaymentV2Async(It.IsAny<Node>(),
                It.Is<byte[]>(b => b.Length == 4 && b[0] == 0x22),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Payment { Status = Payment.Types.PaymentStatus.Succeeded, FeeMsat = 1000, PaymentPreimage = "aa" });

        await CreateJob().Execute(_ctx.Object);

        succeeding.Status.Should().Be(RebalanceStatus.Succeeded);
    }

    [Fact]
    public async Task Execute_MalformedHashHex_SkipsRow()
    {
        var reb = MakeRebalance(RebalanceStatus.InFlight, hashHex: "not-hex!!");
        _rebalanceRepo.Setup(r => r.GetReconcilable(It.IsAny<TimeSpan>())).ReturnsAsync(new List<Rebalance> { reb });

        await CreateJob().Execute(_ctx.Object);

        _lightning.Verify(x => x.TrackPaymentV2Async(It.IsAny<Node>(), It.IsAny<byte[]>(), It.IsAny<CancellationToken>()),
            Times.Never);
        reb.Status.Should().Be(RebalanceStatus.InFlight);
    }
}
