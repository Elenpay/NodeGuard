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
using NodeGuard.Data.Models;

namespace NodeGuard.Data.Repositories;

public class PaymentRouteRepositoryTests
{
    private readonly Random _random = new();
    private const string Hash = "abc123";
    private const string Origin = "02origin";

    private (PaymentRouteRepository sut, DbContextOptions<ApplicationDbContext> options) SetupDb()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(databaseName: "PaymentRoutes" + _random.Next())
            .Options;
        var factory = new Mock<IDbContextFactory<ApplicationDbContext>>();
        factory.Setup(x => x.CreateDbContext()).Returns(() => new ApplicationDbContext(options));
        factory.Setup(x => x.CreateDbContextAsync(default)).ReturnsAsync(() => new ApplicationDbContext(options));

        return (new PaymentRouteRepository(factory.Object,
            new Mock<ILogger<PaymentRouteRepository>>().Object), options);
    }

    private static PaymentRoute Route(PaymentRouteStatus status, params PaymentRouteHop[] hops) => new()
    {
        PaymentHash = Hash,
        OriginNodePubKey = Origin,
        Status = status,
        CreatedAt = DateTimeOffset.UtcNow,
        AmountMsat = 1000,
        Destination = hops.LastOrDefault()?.ToNode,
        Hops = hops.ToList()
    };

    private static PaymentRouteHop Hop(int attemptIndex, int seq, string toNode,
        PaymentRouteAttemptStatus status) => new()
    {
        PaymentHash = Hash,
        AttemptIndex = attemptIndex,
        HopSequence = seq,
        ChannelId = 111,
        FromNode = Origin,
        ToNode = toNode,
        AttemptStatus = status
    };

    [Fact]
    public async Task UpsertAsync_NewPayment_IsInsertedWithItsHops()
    {
        var (sut, options) = SetupDb();

        var (inserted, error) = await sut.UpsertAsync(
            Route(PaymentRouteStatus.Failed, Hop(0, 0, "02aaa", PaymentRouteAttemptStatus.Failed)));

        inserted.Should().BeTrue();
        error.Should().BeNull();

        await using var db = new ApplicationDbContext(options);
        db.PaymentRoutes.Single().Status.Should().Be(PaymentRouteStatus.Failed);
        db.PaymentRouteHops.Single().ToNode.Should().Be("02aaa");
    }

    /// <summary>
    /// LND lets a failed payment hash be retried, so the same hash can reach a terminal state
    /// twice — FAILED, then SUCCEEDED on the retry. An insert-only write left the payment recorded
    /// as failed forever, which is the false-failure this replaces.
    /// </summary>
    [Fact]
    public async Task UpsertAsync_PaymentRetriedAndSettled_RefreshesStatusAndReplacesHops()
    {
        var (sut, options) = SetupDb();

        await sut.UpsertAsync(Route(PaymentRouteStatus.Failed,
            Hop(0, 0, "02aaa", PaymentRouteAttemptStatus.Failed)));

        var (inserted, error) = await sut.UpsertAsync(Route(PaymentRouteStatus.Success,
            Hop(0, 0, "02aaa", PaymentRouteAttemptStatus.Failed),
            Hop(1, 0, "02bbb", PaymentRouteAttemptStatus.Succeeded)));

        inserted.Should().BeFalse();
        error.Should().BeNull();

        await using var db = new ApplicationDbContext(options);
        var stored = db.PaymentRoutes.Include(p => p.Hops).Single();
        stored.Status.Should().Be(PaymentRouteStatus.Success);
        stored.Destination.Should().Be("02bbb");
        // Replaced, not accumulated: two hops in the snapshot means two rows, not three.
        stored.Hops.Should().HaveCount(2);
        stored.Hops.Select(h => h.ToNode).Should().BeEquivalentTo(["02aaa", "02bbb"]);
    }

    /// <summary>
    /// The load-bearing guard. LND deletes failed HTLC attempts once a payment is terminal, so a
    /// later read of the same payment honestly comes back with no attempts at all. Replacing the
    /// stored hops with that empty set would destroy exactly the failed routes this feature exists
    /// to show.
    /// </summary>
    [Fact]
    public async Task UpsertAsync_SnapshotWithNoHops_DoesNotEraseHopsAlreadyCaptured()
    {
        var (sut, options) = SetupDb();

        await sut.UpsertAsync(Route(PaymentRouteStatus.Failed,
            Hop(0, 0, "02aaa", PaymentRouteAttemptStatus.Failed),
            Hop(0, 1, "02bbb", PaymentRouteAttemptStatus.Failed)));

        // Same payment, but LND has since pruned its attempts.
        await sut.UpsertAsync(Route(PaymentRouteStatus.Failed));

        await using var db = new ApplicationDbContext(options);
        var stored = db.PaymentRoutes.Include(p => p.Hops).Single();
        stored.Hops.Should().HaveCount(2);
        stored.Hops.Select(h => h.ToNode).Should().BeEquivalentTo(["02aaa", "02bbb"]);
    }
}
