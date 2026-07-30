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

namespace NodeGuard.Data.Repositories;

public class ChannelRoutingStateRepositoryTests
{
    private readonly Random _random = new();

    private (Mock<IDbContextFactory<ApplicationDbContext>> factory, DbContextOptions<ApplicationDbContext> options) SetupDb()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(databaseName: "RoutingState" + _random.Next())
            .Options;
        var factory = new Mock<IDbContextFactory<ApplicationDbContext>>();
        factory.Setup(x => x.CreateDbContext()).Returns(() => new ApplicationDbContext(options));
        factory.Setup(x => x.CreateDbContextAsync(default)).ReturnsAsync(() => new ApplicationDbContext(options));
        return (factory, options);
    }

    [Fact]
    public async Task UpsertByChannelId_InsertsThenUpdatesInPlace_PreservingSmoothedFields()
    {
        var (factory, options) = SetupDb();
        var sut = new ChannelRoutingStateRepository(factory.Object);

        // First call inserts.
        await sut.UpsertByChannelId(new ChannelRoutingState
        {
            ChannelId = 42,
            ManagedNodePubKey = "02node",
            EmaLocalRatio = 0.70,
            TargetLocalRatio = 0.60,
            PeerFlowCategory = PeerFlowCategory.Sink,
            LastEvaluatedAt = DateTimeOffset.UtcNow,
        });

        var afterInsert = await sut.GetByChannelId(42);
        afterInsert.Should().NotBeNull();
        afterInsert!.EmaLocalRatio.Should().Be(0.70);
        afterInsert.PeerFlowCategory.Should().Be(PeerFlowCategory.Sink);

        // Second call with the same ChannelId updates in place.
        await sut.UpsertByChannelId(new ChannelRoutingState
        {
            ChannelId = 42,
            ManagedNodePubKey = "02node",
            EmaLocalRatio = 0.75,
            TargetLocalRatio = 0.62,
            PeerFlowCategory = PeerFlowCategory.Bidirectional,
            LastEvaluatedAt = DateTimeOffset.UtcNow,
        });

        var afterUpdate = await sut.GetByChannelId(42);
        afterUpdate!.EmaLocalRatio.Should().Be(0.75);
        afterUpdate.TargetLocalRatio.Should().Be(0.62);
        afterUpdate.PeerFlowCategory.Should().Be(PeerFlowCategory.Bidirectional);

        // Exactly one row — the second call updated, it did not insert a duplicate.
        await using var verify = new ApplicationDbContext(options);
        (await verify.ChannelRoutingStates.CountAsync(x => x.ChannelId == 42)).Should().Be(1);
    }

    [Fact]
    public async Task GetByManagedNodePubKey_ReturnsOnlyThatNodesRows()
    {
        var (factory, _) = SetupDb();
        var sut = new ChannelRoutingStateRepository(factory.Object);

        await sut.UpsertByChannelId(new ChannelRoutingState { ChannelId = 1, ManagedNodePubKey = "02a", LastEvaluatedAt = DateTimeOffset.UtcNow });
        await sut.UpsertByChannelId(new ChannelRoutingState { ChannelId = 2, ManagedNodePubKey = "02a", LastEvaluatedAt = DateTimeOffset.UtcNow });
        await sut.UpsertByChannelId(new ChannelRoutingState { ChannelId = 3, ManagedNodePubKey = "02b", LastEvaluatedAt = DateTimeOffset.UtcNow });

        var forA = await sut.GetByManagedNodePubKey("02a");
        forA.Should().HaveCount(2);
        forA.Select(x => x.ChannelId).Should().BeEquivalentTo(new[] { 1, 2 });
    }
}
