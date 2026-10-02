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
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using NodeGuard.Data;
using NodeGuard.Data.Models;

namespace NodeGuard.Data.Repositories;

public class ForwardingHtlcEventRepositoryTests
{
    private readonly Random _random = new();
    private const string Node = "02node";
    private const string OtherNode = "02other";
    private const ulong Chan = 111;
    private const ulong OtherChan = 222;

    private (Mock<IDbContextFactory<ApplicationDbContext>> factory, ApplicationDbContext seedContext) SetupDb()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(databaseName: "ForwardingHtlcEvent" + _random.Next())
            .Options;
        var factory = new Mock<IDbContextFactory<ApplicationDbContext>>();
        factory.Setup(x => x.CreateDbContext()).Returns(() => new ApplicationDbContext(options));
        factory.Setup(x => x.CreateDbContextAsync(default)).ReturnsAsync(() => new ApplicationDbContext(options));
        return (factory, new ApplicationDbContext(options));
    }

    private static ForwardingHtlcEventRepository Sut(Mock<IDbContextFactory<ApplicationDbContext>> factory)
        => new(factory.Object, Mock.Of<ILogger<ForwardingHtlcEventRepository>>());

    private static ForwardingHtlcEvent Event(
        string node, ulong incomingChan, ulong outgoingChan, ForwardingOutcome outcome,
        DateTimeOffset ts, ulong incomingAmt = 0, ulong outgoingAmt = 0, long fee = 0,
        ulong inHtlc = 0, ulong outHtlc = 0)
        => new()
        {
            ManagedNodePubKey = node,
            IncomingChannelId = incomingChan,
            OutgoingChannelId = outgoingChan,
            IncomingHtlcId = inHtlc,
            OutgoingHtlcId = outHtlc,
            Outcome = outcome,
            EventTimestamp = ts,
            IncomingAmountMsat = incomingAmt,
            OutgoingAmountMsat = outgoingAmt,
            FeeMsat = fee,
        };

    [Fact]
    public async Task Getters_SumOnlySettledInWindowForThisNodeAndChannel()
    {
        var (factory, seed) = SetupDb();
        var now = DateTimeOffset.UtcNow;
        var since = now.AddDays(-1);

        seed.ForwardingHtlcEvents.AddRange(
            // Settled, in-window, outgoing on our channel — counted.
            Event(Node, 900, Chan, ForwardingOutcome.Settled, now, outgoingAmt: 1000, fee: 10, inHtlc: 1, outHtlc: 1),
            Event(Node, 901, Chan, ForwardingOutcome.Settled, now, outgoingAmt: 500, fee: 5, inHtlc: 2, outHtlc: 2),
            // Settled, in-window, incoming on our channel — counted for incoming only.
            Event(Node, Chan, 902, ForwardingOutcome.Settled, now, incomingAmt: 2000, inHtlc: 3, outHtlc: 3),
            // Out of window — excluded.
            Event(Node, 903, Chan, ForwardingOutcome.Settled, now.AddDays(-2), outgoingAmt: 9999, fee: 99, inHtlc: 4, outHtlc: 4),
            // Not settled — excluded.
            Event(Node, 904, Chan, ForwardingOutcome.Failed, now, outgoingAmt: 7777, fee: 77, inHtlc: 5, outHtlc: 5),
            Event(Node, Chan, 905, ForwardingOutcome.Unknown, now, incomingAmt: 4444, inHtlc: 6, outHtlc: 6),
            // Different node — excluded.
            Event(OtherNode, 906, Chan, ForwardingOutcome.Settled, now, outgoingAmt: 6666, fee: 66, inHtlc: 7, outHtlc: 7),
            // Different channel — excluded from Chan sums.
            Event(Node, 907, OtherChan, ForwardingOutcome.Settled, now, outgoingAmt: 3333, fee: 33, inHtlc: 8, outHtlc: 8)
        );
        await seed.SaveChangesAsync();

        var sut = Sut(factory);

        (await sut.GetOutgoingAmountMsat(Node, Chan, since)).Should().Be(1500);
        (await sut.GetIncomingAmountMsat(Node, Chan, since)).Should().Be(2000);
    }

    [Fact]
    public async Task Getters_ReturnZero_WhenNoMatchingRows()
    {
        var (factory, _) = SetupDb();
        var sut = Sut(factory);

        (await sut.GetOutgoingAmountMsat(Node, Chan, DateTimeOffset.UtcNow.AddDays(-1))).Should().Be(0);
        (await sut.GetIncomingAmountMsat(Node, Chan, DateTimeOffset.UtcNow.AddDays(-1))).Should().Be(0);
    }

    /// <summary>A refused forward, with every field the insufficient-balance filter looks at explicit.</summary>
    private static ForwardingHtlcEvent Failure(
        string node, ulong incomingChan, ulong outgoingChan, DateTimeOffset ts,
        int failureDetail = (int)Routerrpc.FailureDetail.InsufficientBalance,
        HtlcEventType eventType = HtlcEventType.Forward,
        HtlcEventCase eventCase = HtlcEventCase.LinkFailEvent,
        ulong outgoingAmt = 0, long routingFeePpm = 0, string? outgoingAlias = null,
        ulong inHtlc = 0, ulong outHtlc = 0)
        => new()
        {
            ManagedNodePubKey = node,
            IncomingChannelId = incomingChan,
            OutgoingChannelId = outgoingChan,
            IncomingHtlcId = inHtlc,
            OutgoingHtlcId = outHtlc,
            EventTimestamp = ts,
            EventType = eventType,
            EventCase = eventCase,
            Outcome = ForwardingOutcome.Failed,
            FailureDetail = failureDetail,
            OutgoingAmountMsat = outgoingAmt,
            RoutingFeePpm = routingFeePpm,
            OutgoingPeerAlias = outgoingAlias,
        };

    [Fact]
    public async Task GetInsufficientBalanceFailures_ReturnsOnlyOutboundShortagesOfThisNodeInWindow()
    {
        var (factory, seed) = SetupDb();
        var now = DateTimeOffset.UtcNow;
        var since = now.AddDays(-1);

        seed.ForwardingHtlcEvents.AddRange(
            Failure(Node, 900, Chan, now.AddHours(-2), inHtlc: 1, outHtlc: 1),
            // The window is inclusive: a refusal landing exactly on the boundary is still evidence.
            Failure(Node, 901, Chan, since, inHtlc: 2, outHtlc: 2),
            Failure(Node, 902, Chan, since.AddSeconds(-1), inHtlc: 3, outHtlc: 3),
            Failure(OtherNode, 903, Chan, now, inHtlc: 4, outHtlc: 4),
            // A local send or receive is not routing demand, so no channel would have earned the fee.
            Failure(Node, 904, Chan, now, eventType: HtlcEventType.Send, inHtlc: 5, outHtlc: 5),
            // A downstream failure says nothing about our own outgoing balance.
            Failure(Node, 905, Chan, now, eventCase: HtlcEventCase.ForwardFailEvent, inHtlc: 6, outHtlc: 6),
            // Our own max-HTLC ceiling, not a shortage — more capacity would not have let this through.
            Failure(Node, 906, Chan, now, failureDetail: (int)Routerrpc.FailureDetail.HtlcExceedsMax,
                inHtlc: 7, outHtlc: 7)
        );
        await seed.SaveChangesAsync();

        var result = await Sut(factory).GetInsufficientBalanceFailures(Node, since);

        result.Select(x => x.EventTimestamp).Should().BeEquivalentTo(new[] { now.AddHours(-2), since });
    }

    [Fact]
    public async Task GetInsufficientBalanceFailures_OrdersByEventTimestampAscending()
    {
        var (factory, seed) = SetupDb();
        var now = DateTimeOffset.UtcNow;

        // Burst collapsing walks the rows in time order, so chronology is part of the contract here
        // and not an incidental property of how the rows happened to be stored.
        seed.ForwardingHtlcEvents.AddRange(
            Failure(Node, 900, Chan, now.AddHours(-1), inHtlc: 1, outHtlc: 1),
            Failure(Node, 901, Chan, now.AddHours(-5), inHtlc: 2, outHtlc: 2),
            Failure(Node, 902, Chan, now.AddHours(-3), inHtlc: 3, outHtlc: 3)
        );
        await seed.SaveChangesAsync();

        var result = await Sut(factory).GetInsufficientBalanceFailures(Node, now.AddDays(-1));

        result.Select(x => x.EventTimestamp).Should()
            .ContainInOrder(now.AddHours(-5), now.AddHours(-3), now.AddHours(-1));
    }

    [Fact]
    public async Task GetInsufficientBalanceFailures_ProjectsTheEvidenceSizingAndDisplayNeed()
    {
        var (factory, seed) = SetupDb();
        var now = DateTimeOffset.UtcNow;
        var ts = now.AddHours(-2);

        seed.ForwardingHtlcEvents.Add(Failure(Node, 900, Chan, ts,
            outgoingAmt: 1_500_000, routingFeePpm: 250, outgoingAlias: "acinq", inHtlc: 1, outHtlc: 1));
        await seed.SaveChangesAsync();

        var result = await Sut(factory).GetInsufficientBalanceFailures(Node, now.AddDays(-1));

        // Capacity and missed fee are derived from these five fields alone, so a dropped column would
        // silently size every recommendation wrong rather than fail.
        result.Should().ContainSingle().Which.Should().Be(
            new ForwardingHtlcFailure(Chan, ts, 1_500_000, 250, "acinq"));
    }
}
