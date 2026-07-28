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

        await sut.UpsertByChannelId(new ChannelFeeState { ChannelId = 7, LastAppliedOutboundPpm = 1234 });

        var deleted = await sut.DeleteByChannelId(7);

        deleted.Should().BeTrue();
        (await sut.GetByChannelId(7)).Should().BeNull();
    }

    [Fact]
    public async Task DeleteByChannelId_ReturnsFalse_WhenAbsent()
    {
        var (factory, _) = SetupDb();
        var sut = new ChannelFeeStateRepository(factory.Object);

        (await sut.DeleteByChannelId(999)).Should().BeFalse();
    }

    [Fact]
    public async Task DeleteByManagedNodePubKey_RemovesOnlyThatNodesFeeStates_ResolvedViaRoutingState()
    {
        var (factory, options) = SetupDb();
        var sut = new ChannelFeeStateRepository(factory.Object);

        // Fee states for three channels.
        await sut.UpsertByChannelId(new ChannelFeeState { ChannelId = 1, LastAppliedOutboundPpm = 10 });
        await sut.UpsertByChannelId(new ChannelFeeState { ChannelId = 2, LastAppliedOutboundPpm = 20 });
        await sut.UpsertByChannelId(new ChannelFeeState { ChannelId = 3, LastAppliedOutboundPpm = 30 });

        // Ownership lives on ChannelRoutingState: channels 1 & 2 belong to node A, channel 3 to node B.
        await using (var seed = new ApplicationDbContext(options))
        {
            seed.ChannelRoutingStates.AddRange(
                new ChannelRoutingState { ChannelId = 1, ManagedNodePubKey = "02a", LastEvaluatedAt = DateTimeOffset.UtcNow },
                new ChannelRoutingState { ChannelId = 2, ManagedNodePubKey = "02a", LastEvaluatedAt = DateTimeOffset.UtcNow },
                new ChannelRoutingState { ChannelId = 3, ManagedNodePubKey = "02b", LastEvaluatedAt = DateTimeOffset.UtcNow });
            await seed.SaveChangesAsync();
        }

        var deleted = await sut.DeleteByManagedNodePubKey("02a");

        deleted.Should().Be(2);
        (await sut.GetByChannelId(1)).Should().BeNull();
        (await sut.GetByChannelId(2)).Should().BeNull();
        // Node B's channel is untouched.
        (await sut.GetByChannelId(3)).Should().NotBeNull();
    }
}
