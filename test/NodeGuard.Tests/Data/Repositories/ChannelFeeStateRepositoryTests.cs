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

public class ChannelFeeStateRepositoryTests
{
    private readonly Random _random = new();

    private (Mock<IDbContextFactory<ApplicationDbContext>> factory, DbContextOptions<ApplicationDbContext> options) SetupDb()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(databaseName: "FeeState" + _random.Next())
            .Options;
        var factory = new Mock<IDbContextFactory<ApplicationDbContext>>();
        factory.Setup(x => x.CreateDbContext()).Returns(() => new ApplicationDbContext(options));
        factory.Setup(x => x.CreateDbContextAsync(default)).ReturnsAsync(() => new ApplicationDbContext(options));
        return (factory, options);
    }

    [Fact]
    public async Task DeleteByChannelId_RemovesRow_AndReturnsTrue()
    {
        var (factory, _) = SetupDb();
        var sut = new ChannelFeeStateRepository(factory.Object);

        await sut.UpsertByChannelAndNode(new ChannelFeeState { ChannelId = 7, ManagedNodePubKey = "02a", LastAppliedOutboundPpm = 1234 });

        var deleted = await sut.DeleteByChannelId(7);

        deleted.Should().BeTrue();
        (await sut.GetByChannelIdAndNode(7, "02a")).Should().BeNull();
    }

    [Fact]
    public async Task DeleteByChannelId_RemovesEverySideOfTheChannel()
    {
        var (factory, _) = SetupDb();
        var sut = new ChannelFeeStateRepository(factory.Object);

        // IsDynamicFeeEnabled is channel-level, so opting out drops both managed sides' state.
        await sut.UpsertByChannelAndNode(new ChannelFeeState { ChannelId = 7, ManagedNodePubKey = "02a", LastAppliedOutboundPpm = 10 });
        await sut.UpsertByChannelAndNode(new ChannelFeeState { ChannelId = 7, ManagedNodePubKey = "02b", LastAppliedOutboundPpm = 20 });

        (await sut.DeleteByChannelId(7)).Should().BeTrue();

        (await sut.GetByChannelIdAndNode(7, "02a")).Should().BeNull();
        (await sut.GetByChannelIdAndNode(7, "02b")).Should().BeNull();
    }

    [Fact]
    public async Task DeleteByChannelId_ReturnsFalse_WhenAbsent()
    {
        var (factory, _) = SetupDb();
        var sut = new ChannelFeeStateRepository(factory.Object);

        (await sut.DeleteByChannelId(999)).Should().BeFalse();
    }

    [Fact]
    public async Task UpsertByChannelAndNode_KeepsOneRowPerManagedSideOfTheSameChannel()
    {
        var (factory, options) = SetupDb();
        var sut = new ChannelFeeStateRepository(factory.Object);

        // Each side of a channel sets its own outbound policy, so each keeps its own fee state.
        await sut.UpsertByChannelAndNode(new ChannelFeeState { ChannelId = 7, ManagedNodePubKey = "02a", LastAppliedOutboundPpm = 100 });
        await sut.UpsertByChannelAndNode(new ChannelFeeState { ChannelId = 7, ManagedNodePubKey = "02b", LastAppliedOutboundPpm = 900 });

        await using var verify = new ApplicationDbContext(options);
        (await verify.ChannelFeeStates.CountAsync(x => x.ChannelId == 7)).Should().Be(2);

        // Updating one side leaves the other untouched.
        await sut.UpsertByChannelAndNode(new ChannelFeeState { ChannelId = 7, ManagedNodePubKey = "02a", LastAppliedOutboundPpm = 150 });

        (await sut.GetByChannelIdAndNode(7, "02a"))!.LastAppliedOutboundPpm.Should().Be(150);
        (await sut.GetByChannelIdAndNode(7, "02b"))!.LastAppliedOutboundPpm.Should().Be(900);
    }

    [Fact]
    public async Task GetByManagedNodePubKey_ReturnsOnlyThatNodesRows()
    {
        var (factory, _) = SetupDb();
        var sut = new ChannelFeeStateRepository(factory.Object);

        await sut.UpsertByChannelAndNode(new ChannelFeeState { ChannelId = 1, ManagedNodePubKey = "02a", LastAppliedOutboundPpm = 10 });
        await sut.UpsertByChannelAndNode(new ChannelFeeState { ChannelId = 2, ManagedNodePubKey = "02a", LastAppliedOutboundPpm = 20 });
        await sut.UpsertByChannelAndNode(new ChannelFeeState { ChannelId = 2, ManagedNodePubKey = "02b", LastAppliedOutboundPpm = 30 });

        var forA = await sut.GetByManagedNodePubKey("02a");

        forA.Should().HaveCount(2);
        forA.Select(x => x.ChannelId).Should().BeEquivalentTo(new[] { 1, 2 });
    }

    [Fact]
    public async Task DeleteByManagedNodePubKey_RemovesOnlyThatNodesFeeStates()
    {
        var (factory, _) = SetupDb();
        var sut = new ChannelFeeStateRepository(factory.Object);

        await sut.UpsertByChannelAndNode(new ChannelFeeState { ChannelId = 1, ManagedNodePubKey = "02a", LastAppliedOutboundPpm = 10 });
        await sut.UpsertByChannelAndNode(new ChannelFeeState { ChannelId = 2, ManagedNodePubKey = "02a", LastAppliedOutboundPpm = 20 });
        // Same channel, other managed side — must survive.
        await sut.UpsertByChannelAndNode(new ChannelFeeState { ChannelId = 2, ManagedNodePubKey = "02b", LastAppliedOutboundPpm = 30 });

        var deleted = await sut.DeleteByManagedNodePubKey("02a");

        deleted.Should().BeTrue();
        (await sut.GetByChannelIdAndNode(1, "02a")).Should().BeNull();
        (await sut.GetByChannelIdAndNode(2, "02a")).Should().BeNull();
        (await sut.GetByChannelIdAndNode(2, "02b")).Should().NotBeNull();
    }

    [Fact]
    public async Task DeleteByManagedNodePubKey_ReturnsFalse_WhenAbsent()
    {
        var (factory, _) = SetupDb();
        var sut = new ChannelFeeStateRepository(factory.Object);

        (await sut.DeleteByManagedNodePubKey("02a")).Should().BeFalse();
    }
}
