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

public class ReservedChannelOpenPeerRepositoryTests
{
    private const int Loop = 10;
    private const int Boltz = 11;

    private readonly Random _random = new();

    private ReservedChannelOpenPeerRepository Repository()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(databaseName: "ReservedChannelOpenPeer" + _random.Next())
            .Options;
        var factory = new Mock<IDbContextFactory<ApplicationDbContext>>();
        factory.Setup(x => x.CreateDbContextAsync(default)).ReturnsAsync(() => new ApplicationDbContext(options));

        using var context = new ApplicationDbContext(options);
        context.Nodes.AddRange(
            new Node { Id = 1, Name = "alice", PubKey = "alice" },
            new Node { Id = 2, Name = "bob", PubKey = "bob" },
            new Node { Id = Loop, Name = "loop", PubKey = "loop" },
            new Node { Id = Boltz, Name = "boltz", PubKey = "boltz" });
        context.SaveChanges();

        return new ReservedChannelOpenPeerRepository(factory.Object, Mock.Of<ILogger<ReservedChannelOpenPeerRepository>>());
    }

    private static ReservedChannelOpenPeer Reserved(int peerNodeId, int pct)
        => new() { PeerNodeId = peerNodeId, BudgetPct = pct };

    [Fact]
    public async Task ReplaceForNode_ReplacesTheWholeListOfThatNodeOnly()
    {
        var repository = Repository();
        await repository.ReplaceForNode(1, [Reserved(Loop, 20), Reserved(Boltz, 10)]);
        await repository.ReplaceForNode(2, [Reserved(Loop, 50)]);

        // Re-adding a peer that is already saved must not trip the unique index.
        var (ok, error) = await repository.ReplaceForNode(1, [Reserved(Loop, 30)]);

        ok.Should().BeTrue(error);
        (await repository.GetByNode(1)).Should().ContainSingle()
            .Which.Should().BeEquivalentTo(new { PeerNodeId = Loop, BudgetPct = 30, PeerNode = new { PubKey = "loop" } });
        (await repository.GetByNode(2)).Should().ContainSingle().Which.BudgetPct.Should().Be(50);
    }

    [Fact]
    public async Task ReplaceForNode_EmptyList_ClearsTheNode()
    {
        var repository = Repository();
        await repository.ReplaceForNode(1, [Reserved(Loop, 20)]);

        await repository.ReplaceForNode(1, []);

        (await repository.GetByNode(1)).Should().BeEmpty();
    }
}
