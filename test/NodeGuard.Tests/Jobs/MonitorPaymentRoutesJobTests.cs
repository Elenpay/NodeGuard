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
using NodeGuard.Services;
using Quartz;

namespace NodeGuard.Jobs;

public class MonitorPaymentRoutesJobTests
{
    private const string OriginPubKey = "02origin";
    private const string HopA = "02aaa";
    private const string HopB = "02bbb";
    private const string HopC = "02ccc";

    private readonly Mock<INodeRepository> _nodeRepositoryMock = new();
    private readonly Mock<ILightningClientService> _lightningClientServiceMock = new();
    private readonly Mock<IPaymentRouteRepository> _paymentRouteRepositoryMock = new();
    private readonly MonitorPaymentRoutesJob _job;

    private readonly List<PaymentRoute> _persisted = new();

    public MonitorPaymentRoutesJobTests()
    {
        _nodeRepositoryMock
            .Setup(x => x.GetAllManagedByNodeGuard(It.IsAny<bool>()))
            .ReturnsAsync(new List<Node>
            {
                new() { Id = 1, Name = "origin", PubKey = OriginPubKey, Endpoint = "localhost:10009", ChannelAdminMacaroon = "abc" }
            });

        _paymentRouteRepositoryMock
            .Setup(x => x.InsertIfNewAsync(It.IsAny<PaymentRoute>()))
            .Callback<PaymentRoute>(p => _persisted.Add(p))
            .ReturnsAsync((true, (string?)null));

        _job = new MonitorPaymentRoutesJob(
            new Mock<ILogger<MonitorPaymentRoutesJob>>().Object,
            _nodeRepositoryMock.Object,
            _lightningClientServiceMock.Object,
            _paymentRouteRepositoryMock.Object);
    }

    private void GivenPayments(params Payment[] payments)
    {
        var response = new ListPaymentsResponse { LastIndexOffset = 0 };
        response.Payments.AddRange(payments);

        // LastIndexOffset stays 0, so the tracker's pagination loop stops after one page.
        _lightningClientServiceMock
            .Setup(x => x.ListPayments(It.IsAny<Node>(), It.IsAny<ListPaymentsRequest>(), null))
            .ReturnsAsync(response);
    }

    private static Hop MakeHop(string pubKey, ulong chanId) =>
        new() { PubKey = pubKey, ChanId = chanId, AmtToForwardMsat = 1000 };

    private static HTLCAttempt MakeAttempt(ulong attemptId, HTLCAttempt.Types.HTLCStatus status,
        Failure? failure, params Hop[] hops)
    {
        var route = new Route();
        route.Hops.AddRange(hops);
        return new HTLCAttempt { AttemptId = attemptId, Status = status, Route = route, Failure = failure };
    }

    private static Payment MakePayment(string hash, Payment.Types.PaymentStatus status, params HTLCAttempt[] attempts)
    {
        var payment = new Payment
        {
            PaymentHash = hash,
            Status = status,
            ValueMsat = 1000,
            CreationTimeNs = 1_700_000_000L * 1_000_000_000L
        };
        payment.Htlcs.AddRange(attempts);
        return payment;
    }

    /// <summary>
    /// The regression this whole change exists for: a payment that finally SUCCEEDED after
    /// retrying carries its abandoned attempts in the same htlcs list. Deriving hop colour
    /// from the payment's status painted those failed routes green.
    /// </summary>
    [Fact]
    public async Task Execute_SucceededPaymentWithFailedAttempts_RecordsEachAttemptsOwnOutcome()
    {
        GivenPayments(MakePayment("hash1", Payment.Types.PaymentStatus.Succeeded,
            MakeAttempt(4001, HTLCAttempt.Types.HTLCStatus.Failed,
                new Failure { Code = Failure.Types.FailureCode.TemporaryChannelFailure, FailureSourceIndex = 1 },
                MakeHop(HopA, 111), MakeHop(HopB, 222)),
            MakeAttempt(4002, HTLCAttempt.Types.HTLCStatus.Failed,
                new Failure { Code = Failure.Types.FailureCode.FeeInsufficient, FailureSourceIndex = 2 },
                MakeHop(HopC, 333), MakeHop(HopB, 444)),
            MakeAttempt(4003, HTLCAttempt.Types.HTLCStatus.Succeeded, null,
                MakeHop(HopA, 555), MakeHop(HopB, 666))));

        await _job.Execute(new Mock<IJobExecutionContext>().Object);

        var hops = _persisted.Single().Hops;

        hops.Where(h => h.AttemptIndex == 0).Should()
            .OnlyContain(h => h.AttemptStatus == PaymentRouteAttemptStatus.Failed
                              && h.FailureCode == "TEMPORARY_CHANNEL_FAILURE"
                              && h.FailureSourceIndex == 1);

        hops.Where(h => h.AttemptIndex == 1).Should()
            .OnlyContain(h => h.AttemptStatus == PaymentRouteAttemptStatus.Failed
                              && h.FailureCode == "FEE_INSUFFICIENT"
                              && h.FailureSourceIndex == 2);

        hops.Where(h => h.AttemptIndex == 2).Should()
            .OnlyContain(h => h.AttemptStatus == PaymentRouteAttemptStatus.Succeeded
                              && h.FailureCode == null
                              && h.FailureSourceIndex == null);
    }

    /// <summary>
    /// AttemptIndex must be the attempt's ordinal inside its own payment. LND's attempt_id is
    /// a node-global uint64 that both overflows int and renders as "attempt 4002" in the UI.
    /// </summary>
    [Fact]
    public async Task Execute_AttemptIndex_IsPerPaymentOrdinalNotLndAttemptId()
    {
        GivenPayments(MakePayment("hash1", Payment.Types.PaymentStatus.Failed,
            MakeAttempt(9_000_000_001, HTLCAttempt.Types.HTLCStatus.Failed, null, MakeHop(HopA, 111)),
            MakeAttempt(9_000_000_002, HTLCAttempt.Types.HTLCStatus.Failed, null, MakeHop(HopB, 222))));

        await _job.Execute(new Mock<IJobExecutionContext>().Object);

        // Asserted as (index, hop) pairs rather than a bare sequence, so an implementation
        // that stamped every hop with the same ordinal can't pass on list order alone.
        _persisted.Single().Hops.Should().SatisfyRespectively(
            h => { h.AttemptIndex.Should().Be(0); h.ToNode.Should().Be(HopA); },
            h => { h.AttemptIndex.Should().Be(1); h.ToNode.Should().Be(HopB); });
    }

    /// <summary>
    /// A payment that failed over one route and settled over another must report the route
    /// that actually delivered, not the abandoned one it tried first.
    /// </summary>
    [Fact]
    public async Task Execute_Destination_ComesFromTheSettledAttemptNotTheFirstOne()
    {
        GivenPayments(MakePayment("hash1", Payment.Types.PaymentStatus.Succeeded,
            MakeAttempt(1, HTLCAttempt.Types.HTLCStatus.Failed, null, MakeHop(HopA, 111), MakeHop(HopB, 222)),
            MakeAttempt(2, HTLCAttempt.Types.HTLCStatus.Succeeded, null, MakeHop(HopA, 333), MakeHop(HopC, 444))));

        await _job.Execute(new Mock<IJobExecutionContext>().Object);

        _persisted.Single().Destination.Should().Be(HopC);
    }

    /// <summary>
    /// Nothing settled, but the payment still aimed somewhere — fall back to the first
    /// attempt that had a route rather than losing the destination entirely.
    /// </summary>
    [Fact]
    public async Task Execute_Destination_FallsBackToFirstRoutedAttemptWhenNoneSettled()
    {
        GivenPayments(MakePayment("hash1", Payment.Types.PaymentStatus.Failed,
            MakeAttempt(1, HTLCAttempt.Types.HTLCStatus.Failed, null, MakeHop(HopA, 111), MakeHop(HopB, 222)),
            MakeAttempt(2, HTLCAttempt.Types.HTLCStatus.Failed, null, MakeHop(HopA, 333), MakeHop(HopC, 444))));

        await _job.Execute(new Mock<IJobExecutionContext>().Object);

        _persisted.Single().Destination.Should().Be(HopB);
    }

    [Fact]
    public async Task Execute_HopSequenceAndFromNode_ChainFromTheOriginPerAttempt()
    {
        GivenPayments(MakePayment("hash1", Payment.Types.PaymentStatus.Succeeded,
            MakeAttempt(1, HTLCAttempt.Types.HTLCStatus.Succeeded, null,
                MakeHop(HopA, 111), MakeHop(HopB, 222), MakeHop(HopC, 333))));

        await _job.Execute(new Mock<IJobExecutionContext>().Object);

        var hops = _persisted.Single().Hops;
        hops.Select(h => h.HopSequence).Should().Equal(0, 1, 2);
        hops.Select(h => h.FromNode).Should().Equal(OriginPubKey, HopA, HopB);
        hops.Select(h => h.ToNode).Should().Equal(HopA, HopB, HopC);
    }

    /// <summary>
    /// Pathfinding-stage failures (NO_ROUTE, INSUFFICIENT_BALANCE) reach us with an empty
    /// htlcs list — LND never dispatched an HTLC. The payment is still tracked; it just has
    /// no route to draw.
    /// </summary>
    [Fact]
    public async Task Execute_FailedPaymentWithNoAttempts_IsPersistedWithoutHops()
    {
        GivenPayments(MakePayment("hash1", Payment.Types.PaymentStatus.Failed));

        await _job.Execute(new Mock<IJobExecutionContext>().Object);

        var payment = _persisted.Single();
        payment.Status.Should().Be(PaymentRouteStatus.Failed);
        payment.Hops.Should().BeEmpty();
        payment.Destination.Should().BeNull();
    }

    [Fact]
    public async Task Execute_NonTerminalPayment_IsSkipped()
    {
        GivenPayments(MakePayment("hash1", Payment.Types.PaymentStatus.InFlight,
            MakeAttempt(1, HTLCAttempt.Types.HTLCStatus.InFlight, null, MakeHop(HopA, 111))));

        await _job.Execute(new Mock<IJobExecutionContext>().Object);

        _persisted.Should().BeEmpty();
    }
}
