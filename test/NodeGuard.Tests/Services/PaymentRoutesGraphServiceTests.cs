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
using NSubstitute;

namespace NodeGuard.Services;

public class PaymentRoutesGraphServiceTests
{
    // Mirrors graph_builder._hop_status_for cases from LightningEye.

    [Fact]
    public void HopStatusFor_SuccessfulPayment_AlwaysSuccess()
    {
        var (status, code) = PaymentRoutesGraphService.HopStatusFor(PaymentRouteStatus.Success, 2, 3, "X");
        status.Should().Be("success");
        code.Should().BeNull();
    }

    [Fact]
    public void HopStatusFor_FailedNoSourceIndex_FallsBackToFailed()
    {
        var (status, code) = PaymentRoutesGraphService.HopStatusFor(PaymentRouteStatus.Failed, 0, null, null);
        status.Should().Be("failed");
        code.Should().BeNull();
    }

    // failure_source_index F = 2. dest_pos = hopIndex + 1.
    [Theory]
    [InlineData(0, "ok")]          // dest_pos 1 < 2  → traversed before the failure
    [InlineData(1, "failed_here")] // dest_pos 2 == 2 → broke here
    [InlineData(2, "unreached")]   // dest_pos 3 > 2  → never attempted
    public void HopStatusFor_FailedWithSourceIndex_ClassifiesPerHop(int hopIndex, string expected)
    {
        var (status, code) = PaymentRoutesGraphService.HopStatusFor(PaymentRouteStatus.Failed, hopIndex, 2, "TEMPORARY_CHANNEL_FAILURE");
        status.Should().Be(expected);
        code.Should().Be(expected == "failed_here" ? "TEMPORARY_CHANNEL_FAILURE" : null);
    }

    // ── HopStatusForHop: attempt-level resolution ───────────────────────────────

    private static PaymentRouteHop Hop(PaymentRouteAttemptStatus attemptStatus, int hopSequence = 0,
        int? failureSourceIndex = null, string? failureCode = null) => new()
    {
        AttemptStatus = attemptStatus,
        HopSequence = hopSequence,
        FailureSourceIndex = failureSourceIndex,
        FailureCode = failureCode
    };

    /// <summary>
    /// The mislabel this resolver exists to prevent: an abandoned attempt of a payment that
    /// ultimately succeeded must not render as a successful route.
    /// </summary>
    [Fact]
    public void HopStatusForHop_FailedAttemptOfSucceededPayment_IsNotSuccess()
    {
        var (status, code) = PaymentRoutesGraphService.HopStatusForHop(
            Hop(PaymentRouteAttemptStatus.Failed, hopSequence: 1, failureSourceIndex: 2, failureCode: "FEE_INSUFFICIENT"),
            PaymentRouteStatus.Success);

        status.Should().Be("failed_here");
        code.Should().Be("FEE_INSUFFICIENT");
    }

    [Fact]
    public void HopStatusForHop_SucceededAttempt_IsSuccessRegardlessOfStaleFailureData()
    {
        var (status, code) = PaymentRoutesGraphService.HopStatusForHop(
            Hop(PaymentRouteAttemptStatus.Succeeded, failureSourceIndex: 1, failureCode: "X"),
            PaymentRouteStatus.Success);

        status.Should().Be("success");
        code.Should().BeNull();
    }

    /// <summary>
    /// An in-flight shard of a settled MPP payment is dispatched but unproven — it must not
    /// borrow the payment's success.
    /// </summary>
    [Fact]
    public void HopStatusForHop_InFlightAttempt_IsOkNotTheParentPaymentsStatus()
    {
        var (status, code) = PaymentRoutesGraphService.HopStatusForHop(
            Hop(PaymentRouteAttemptStatus.InFlight), PaymentRouteStatus.Success);

        status.Should().Be("ok");
        code.Should().BeNull();
    }

    /// <summary>
    /// Rows tracked before per-attempt data existed carry AttemptStatus = Unknown; they must
    /// keep rendering exactly as they did under the old payment-level derivation.
    /// </summary>
    [Theory]
    [InlineData(PaymentRouteStatus.Success, "success")]
    [InlineData(PaymentRouteStatus.Failed, "failed")]
    public void HopStatusForHop_UnknownAttemptStatus_FallsBackToPaymentStatus(
        PaymentRouteStatus paymentStatus, string expected)
    {
        var (status, _) = PaymentRoutesGraphService.HopStatusForHop(
            Hop(PaymentRouteAttemptStatus.Unknown), paymentStatus);

        status.Should().Be(expected);
    }

    /// <summary>
    /// A failed attempt LND gave us no failure detail for still has to be legible: every hop
    /// goes red rather than silently classifying against a missing source index.
    /// </summary>
    [Fact]
    public void HopStatusForHop_FailedAttemptWithoutFailureDetail_IsFailed()
    {
        var (status, code) = PaymentRoutesGraphService.HopStatusForHop(
            Hop(PaymentRouteAttemptStatus.Failed, hopSequence: 3), PaymentRouteStatus.Failed);

        status.Should().Be("failed");
        code.Should().BeNull();
    }

    // ── Alias resolution ────────────────────────────────────────────────────────
    // A node whose alias we can't resolve must come back with Alias = null so the frontend
    // labels it by its pubkey. It must never receive an invented name.

    private const string OriginKey = "03origin";
    private const string HopKey = "02hop";

    private static PaymentRoute PaymentWithOneHop() => new()
    {
        PaymentHash = "hash1",
        OriginNodePubKey = OriginKey,
        Status = PaymentRouteStatus.Success,
        Hops = new List<PaymentRouteHop>
        {
            new() { PaymentHash = "hash1", FromNode = OriginKey, ToNode = HopKey, AttemptStatus = PaymentRouteAttemptStatus.Succeeded }
        }
    };

    private static (PaymentRoutesGraphService service, INodeRepository nodes, ILightningClientService clients) BuildService(
        Dictionary<string, string> storedNames, string? gossipAlias)
    {
        var payments = Substitute.For<IPaymentRouteRepository>();
        payments.GetByCreatedAtRangeAsync(OriginKey, Arg.Any<DateTimeOffset>(), Arg.Any<DateTimeOffset>())
            .Returns(new List<PaymentRoute> { PaymentWithOneHop() });

        var nodes = Substitute.For<INodeRepository>();
        nodes.GetNamesByPubKeys(Arg.Any<IReadOnlyCollection<string>>()).Returns(storedNames);
        nodes.GetByPubkey(OriginKey).Returns(new Node
        {
            PubKey = OriginKey, Name = "alice", Endpoint = "localhost:10009", ChannelAdminMacaroon = "0201"
        });

        var clients = Substitute.For<ILightningClientService>();
        clients.GetNodeInfo(Arg.Any<Node>(), Arg.Any<string>(), Arg.Any<Lightning.LightningClient?>())
            .Returns(gossipAlias is null ? (LightningNode?)null : new LightningNode { Alias = gossipAlias });

        return (new PaymentRoutesGraphService(payments, nodes, clients,
            Substitute.For<ILogger<PaymentRoutesGraphService>>()), nodes, clients);
    }

    private static async Task<PaymentGraphNode> HopNodeOf(PaymentRoutesGraphService service)
    {
        var graph = await service.BuildGraphAsync(OriginKey, DateTimeOffset.MinValue, DateTimeOffset.MaxValue);
        return graph.Nodes.Single(n => n.Id == HopKey);
    }

    /// <summary>
    /// The Nodes table outlives gossip: once LND zombie-prunes the channel, GetNodeInfo stops
    /// resolving the routing node entirely, so a name we already hold must be used and must not
    /// cost an RPC.
    /// </summary>
    [Fact]
    public async Task BuildGraphAsync_PubKeyKnownLocally_UsesStoredNameWithoutQueryingGossip()
    {
        var (service, _, clients) = BuildService(new Dictionary<string, string> { [HopKey] = "frank" }, gossipAlias: "stale");

        (await HopNodeOf(service)).Alias.Should().Be("frank");
        await clients.DidNotReceive().GetNodeInfo(Arg.Any<Node>(), HopKey, Arg.Any<Lightning.LightningClient?>());
    }

    /// <summary>
    /// GetOrCreateByPubKey stores Name = "" when its own alias lookup failed; GetNamesByPubKeys
    /// drops those, so they arrive here as "unknown" and must still reach the gossip lookup.
    /// </summary>
    [Fact]
    public async Task BuildGraphAsync_PubKeyNotKnownLocally_FallsBackToGossip()
    {
        var (service, _, clients) = BuildService(new Dictionary<string, string>(), gossipAlias: "frank");

        (await HopNodeOf(service)).Alias.Should().Be("frank");
        await clients.Received().GetNodeInfo(Arg.Any<Node>(), HopKey, Arg.Any<Lightning.LightningClient?>());
    }

    [Fact]
    public async Task BuildGraphAsync_PubKeyUnresolvableAnywhere_LeavesAliasNull()
    {
        var (service, _, _) = BuildService(new Dictionary<string, string>(), gossipAlias: null);

        (await HopNodeOf(service)).Alias.Should().BeNull();
    }
}
