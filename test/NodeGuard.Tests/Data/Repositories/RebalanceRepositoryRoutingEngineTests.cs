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
using NodeGuard.Data;
using NodeGuard.Data.Models;
using NodeGuard.Data.Repositories.Interfaces;

namespace NodeGuard.Data.Repositories;

public class RebalanceRepositoryRoutingEngineTests
{
    private readonly Random _random = new();
    private const int NodeId = 1;
    private const int OtherNodeId = 2;

    private (RebalanceRepository sut, ApplicationDbContext seed) SetupDb()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(databaseName: "RebalanceRE" + _random.Next())
            .Options;
        var factory = new Mock<IDbContextFactory<ApplicationDbContext>>();
        factory.Setup(x => x.CreateDbContext()).Returns(() => new ApplicationDbContext(options));
        factory.Setup(x => x.CreateDbContextAsync(default)).ReturnsAsync(() => new ApplicationDbContext(options));
        var repository = new Mock<IRepository<Rebalance>>();
        return (new RebalanceRepository(repository.Object, factory.Object), new ApplicationDbContext(options));
    }

    private static Rebalance Reb(int nodeId, RebalanceStatus status, DateTimeOffset created,
        long? feePaid = null, long? reserved = null)
        => new()
        {
            NodeId = nodeId,
            Status = status,
            CreationDatetime = created,
            UpdateDatetime = created,
            FeePaidSats = feePaid,
            ReservedFeeSats = reserved,
        };

    [Fact]
    public async Task GetInFlightByNode_CountsOnlyPendingAndInFlightForThatNode()
    {
        var (sut, seed) = SetupDb();
        var now = DateTimeOffset.UtcNow;

        seed.Rebalances.AddRange(
            Reb(NodeId, RebalanceStatus.Pending, now),
            Reb(NodeId, RebalanceStatus.InFlight, now),
            Reb(NodeId, RebalanceStatus.Succeeded, now),
            Reb(NodeId, RebalanceStatus.Failed, now),
            Reb(NodeId, RebalanceStatus.NoRoute, now),
            Reb(NodeId, RebalanceStatus.Timeout, now),
            Reb(NodeId, RebalanceStatus.InsufficientBalance, now),
            Reb(NodeId, RebalanceStatus.ExceededFeeLimit, now),
            Reb(OtherNodeId, RebalanceStatus.Pending, now),   // different node — excluded
            Reb(OtherNodeId, RebalanceStatus.InFlight, now)
        );
        await seed.SaveChangesAsync();

        (await sut.GetInFlightByNode(NodeId)).Should().Be(2);
    }

    [Fact]
    public async Task HasInFlightRebalanceBySourceChannel_TrueOnlyForPendingOrInFlightSource()
    {
        var (sut, seed) = SetupDb();
        var now = DateTimeOffset.UtcNow;
        const int chanPending = 10;
        const int chanInFlight = 20;
        const int chanSettled = 30;

        seed.Rebalances.AddRange(
            new Rebalance { NodeId = NodeId, SourceChannelId = chanPending, Status = RebalanceStatus.Pending, CreationDatetime = now, UpdateDatetime = now },
            new Rebalance { NodeId = NodeId, SourceChannelId = chanInFlight, Status = RebalanceStatus.InFlight, CreationDatetime = now, UpdateDatetime = now },
            new Rebalance { NodeId = NodeId, SourceChannelId = chanSettled, Status = RebalanceStatus.Succeeded, CreationDatetime = now, UpdateDatetime = now }
        );
        await seed.SaveChangesAsync();

        (await sut.HasInFlightRebalanceBySourceChannel(chanPending)).Should().BeTrue();
        (await sut.HasInFlightRebalanceBySourceChannel(chanInFlight)).Should().BeTrue();
        (await sut.HasInFlightRebalanceBySourceChannel(chanSettled)).Should().BeFalse();
        (await sut.HasInFlightRebalanceBySourceChannel(999)).Should().BeFalse(); // unknown channel
    }

    [Fact]
    public async Task GetConsumedFeesSince_UsesReservedOrPaidForInFlightAndPaidForSucceeded()
    {
        var (sut, seed) = SetupDb();
        var now = DateTimeOffset.UtcNow;
        var since = now.AddHours(-1);

        seed.Rebalances.AddRange(
            // Pending with only a reservation → counts reserved (100).
            Reb(NodeId, RebalanceStatus.Pending, now, feePaid: null, reserved: 100),
            // InFlight where paid already exceeds reserved → counts MAX (50).
            Reb(NodeId, RebalanceStatus.InFlight, now, feePaid: 50, reserved: 30),
            // Succeeded → counts paid (200).
            Reb(NodeId, RebalanceStatus.Succeeded, now, feePaid: 200, reserved: 999),
            // Failed with a stale reservation → nothing consumed (excluded).
            Reb(NodeId, RebalanceStatus.Failed, now, feePaid: null, reserved: 999),
            // Succeeded but before the window → excluded.
            Reb(NodeId, RebalanceStatus.Succeeded, now.AddHours(-2), feePaid: 777),
            // Different node → excluded.
            Reb(OtherNodeId, RebalanceStatus.Succeeded, now, feePaid: 500)
        );
        await seed.SaveChangesAsync();

        (await sut.GetConsumedFeesSince(NodeId, since)).Should().Be(100 + 50 + 200);
    }
}
