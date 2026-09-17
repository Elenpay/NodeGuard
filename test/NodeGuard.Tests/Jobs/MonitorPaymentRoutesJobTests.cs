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
using NodeGuard.Data.Models;

namespace NodeGuard.Jobs;

/// <summary>
/// Covers the LND-payment → <see cref="PaymentRoute"/> mapping via
/// <see cref="MonitorPaymentRoutesJob.MapToPaymentRoute"/>, the projection every update from the
/// per-node payment stream goes through before it is persisted. Kept free of the repository so
/// these assertions describe the mapping only; the write path is covered by
/// <c>PaymentRouteRepositoryTests</c>.
/// </summary>
public class MonitorPaymentRoutesJobTests
{
    private const string OriginPubKey = "02origin";
    private const string HopA = "02aaa";
    private const string HopB = "02bbb";
    private const string HopC = "02ccc";

    private readonly Node _node = new()
    {
        Id = 1, Name = "origin", PubKey = OriginPubKey, Endpoint = "localhost:10009", ChannelAdminMacaroon = "abc"
    };

    /// <summary>
    /// The projection one stream update goes through. Returns null for updates that are not stored,
    /// so a test that expects nothing persisted asserts on null.
    /// </summary>
    private PaymentRoute? WhenUpdateReceived(Payment payment)
        => MonitorPaymentRoutesJob.MapToPaymentRoute(_node, payment);

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
    public void HandlePaymentUpdate_SucceededPaymentWithFailedAttempts_RecordsEachAttemptsOwnOutcome()
    {
        var mapped = WhenUpdateReceived(MakePayment("hash1", Payment.Types.PaymentStatus.Succeeded,
            MakeAttempt(4001, HTLCAttempt.Types.HTLCStatus.Failed,
                new Failure { Code = Failure.Types.FailureCode.TemporaryChannelFailure, FailureSourceIndex = 1 },
                MakeHop(HopA, 111), MakeHop(HopB, 222)),
            MakeAttempt(4002, HTLCAttempt.Types.HTLCStatus.Failed,
                new Failure { Code = Failure.Types.FailureCode.FeeInsufficient, FailureSourceIndex = 2 },
                MakeHop(HopC, 333), MakeHop(HopB, 444)),
            MakeAttempt(4003, HTLCAttempt.Types.HTLCStatus.Succeeded, null,
                MakeHop(HopA, 555), MakeHop(HopB, 666))));

        var hops = mapped!.Hops;

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
    public void HandlePaymentUpdate_AttemptIndex_IsPerPaymentOrdinalNotLndAttemptId()
    {
        var mapped = WhenUpdateReceived(MakePayment("hash1", Payment.Types.PaymentStatus.Failed,
            MakeAttempt(9_000_000_001, HTLCAttempt.Types.HTLCStatus.Failed, null, MakeHop(HopA, 111)),
            MakeAttempt(9_000_000_002, HTLCAttempt.Types.HTLCStatus.Failed, null, MakeHop(HopB, 222))));

        // Asserted as (index, hop) pairs rather than a bare sequence, so an implementation
        // that stamped every hop with the same ordinal can't pass on list order alone.
        mapped!.Hops.Should().SatisfyRespectively(
            h => { h.AttemptIndex.Should().Be(0); h.ToNode.Should().Be(HopA); },
            h => { h.AttemptIndex.Should().Be(1); h.ToNode.Should().Be(HopB); });
    }

    /// <summary>
    /// A payment that failed over one route and settled over another must report the route
    /// that actually delivered, not the abandoned one it tried first.
    /// </summary>
    [Fact]
    public void HandlePaymentUpdate_Destination_ComesFromTheSettledAttemptNotTheFirstOne()
    {
        var mapped = WhenUpdateReceived(MakePayment("hash1", Payment.Types.PaymentStatus.Succeeded,
            MakeAttempt(1, HTLCAttempt.Types.HTLCStatus.Failed, null, MakeHop(HopA, 111), MakeHop(HopB, 222)),
            MakeAttempt(2, HTLCAttempt.Types.HTLCStatus.Succeeded, null, MakeHop(HopA, 333), MakeHop(HopC, 444))));

        mapped!.Destination.Should().Be(HopC);
    }

    /// <summary>
    /// Nothing settled, but the payment still aimed somewhere — fall back to the first
    /// attempt that had a route rather than losing the destination entirely.
    /// </summary>
    [Fact]
    public void HandlePaymentUpdate_Destination_FallsBackToFirstRoutedAttemptWhenNoneSettled()
    {
        var mapped = WhenUpdateReceived(MakePayment("hash1", Payment.Types.PaymentStatus.Failed,
            MakeAttempt(1, HTLCAttempt.Types.HTLCStatus.Failed, null, MakeHop(HopA, 111), MakeHop(HopB, 222)),
            MakeAttempt(2, HTLCAttempt.Types.HTLCStatus.Failed, null, MakeHop(HopA, 333), MakeHop(HopC, 444))));

        mapped!.Destination.Should().Be(HopB);
    }

    [Fact]
    public void HandlePaymentUpdate_HopSequenceAndFromNode_ChainFromTheOriginPerAttempt()
    {
        var mapped = WhenUpdateReceived(MakePayment("hash1", Payment.Types.PaymentStatus.Succeeded,
            MakeAttempt(1, HTLCAttempt.Types.HTLCStatus.Succeeded, null,
                MakeHop(HopA, 111), MakeHop(HopB, 222), MakeHop(HopC, 333))));

        var hops = mapped!.Hops;
        hops.Select(h => h.HopSequence).Should().Equal(0, 1, 2);
        hops.Select(h => h.FromNode).Should().Equal(OriginPubKey, HopA, HopB);
        hops.Select(h => h.ToNode).Should().Equal(HopA, HopB, HopC);
    }

    /// <summary>
    /// HopSequence indexes LND's route, and so does Failure.failure_source_index — the graph
    /// compares HopSequence + 1 against it to decide which hop broke. A hop we decline to persist
    /// (no pubkey, or no channel id) must therefore still consume its position, otherwise every
    /// later hop shifts down one and the failure is attributed to the wrong node.
    /// </summary>
    [Fact]
    public void HandlePaymentUpdate_UnpersistableHop_StillConsumesItsRoutePosition()
    {
        var mapped = WhenUpdateReceived(MakePayment("hash1", Payment.Types.PaymentStatus.Failed,
            MakeAttempt(1, HTLCAttempt.Types.HTLCStatus.Failed,
                new Failure { Code = Failure.Types.FailureCode.TemporaryChannelFailure, FailureSourceIndex = 3 },
                MakeHop(HopA, 111),
                MakeHop(HopB, 0), // unusable: no channel id, so it is not persisted
                MakeHop(HopC, 333))));

        var hops = mapped!.Hops;
        hops.Select(h => h.ToNode).Should().Equal(HopA, HopC);
        // HopC sits at route position 2, not 1 — so destPos (2 + 1) matches failureSourceIndex 3.
        hops.Select(h => h.HopSequence).Should().Equal(0, 2);
    }

    /// <summary>
    /// Pathfinding-stage failures (NO_ROUTE, INSUFFICIENT_BALANCE) reach us with an empty
    /// htlcs list — LND never dispatched an HTLC. The payment is still tracked; it just has
    /// no route to draw.
    /// </summary>
    [Fact]
    public void HandlePaymentUpdate_FailedPaymentWithNoAttempts_IsPersistedWithoutHops()
    {
        var mapped = WhenUpdateReceived(MakePayment("hash1", Payment.Types.PaymentStatus.Failed));

        var payment = mapped!;
        payment.Status.Should().Be(PaymentRouteStatus.Failed);
        payment.Hops.Should().BeEmpty();
        payment.Destination.Should().BeNull();
    }

    /// <summary>
    /// In-flight updates are still skipped, as they were under polling. Nothing is lost by
    /// waiting for the terminal update: an update's attempt list is cumulative, so the terminal
    /// one carries every attempt the payment ever made, failed ones included.
    /// </summary>
    [Fact]
    public void HandlePaymentUpdate_NonTerminalPayment_IsSkipped()
    {
        var mapped = WhenUpdateReceived(MakePayment("hash1", Payment.Types.PaymentStatus.InFlight,
            MakeAttempt(1, HTLCAttempt.Types.HTLCStatus.InFlight, null, MakeHop(HopA, 111))));

        mapped.Should().BeNull();
    }
}
