// NodeGuard
// Copyright (C) 2025  Elenpay
//
// This program is free software: you can redistribute it and/or modify
// it under the terms of the GNU Affero General Public License as published by
// the Free Software Foundation, either version 3 of the License, or
// (at your option) any later version.
//
// This program is distributed in the hope that it will be useful,
// but WITHOUT ANY WARRANTY, without even the implied warranty of
// MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
// GNU Affero General Public License for more details.
//
// You should have received a copy of the GNU Affero General Public License
// along with this program.  If not, see http://www.gnu.org/licenses/.

using FluentAssertions;
using Lnrpc;
using Microsoft.EntityFrameworkCore;
using NodeGuard.Data.Models;
using NodeGuard.Data.Repositories.Interfaces;
using NodeGuard.Services;

namespace NodeGuard.Data.Repositories;

public class NodeRepositoryTests
{
    private readonly Random _random = new();

    private Mock<IDbContextFactory<ApplicationDbContext>> SetupDbContextFactory()
    {
        var dbContextFactory = new Mock<IDbContextFactory<ApplicationDbContext>>();
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(databaseName: "NodeRepositoryTests" + _random.Next())
            .Options;
        var context = () => new ApplicationDbContext(options);
        dbContextFactory.Setup(x => x.CreateDbContext()).Returns(context);
        dbContextFactory.Setup(x => x.CreateDbContextAsync(default)).ReturnsAsync(context);
        return dbContextFactory;
    }

    [Fact]
    public async Task AddsNewNode_WhenRemoteNodeNotFound()
    {
        // Arrange
        var dbContextFactory = SetupDbContextFactory();
        var lightningServiceMock = new Mock<ILightningService>();
        var repositoryMock = new Mock<IRepository<Node>>();

        var node = new LightningNode() { Alias = "TestAlias", PubKey = "TestPubKey" };
        lightningServiceMock.Setup(service => service.GetNodeInfo(It.IsAny<string>()))
            .ReturnsAsync(node);

        repositoryMock.Setup(repository => repository.AddAsync(It.IsAny<Node>(), It.IsAny<ApplicationDbContext>()))
            .ReturnsAsync((true, null));

        var nodeRepository = new NodeRepository(repositoryMock.Object, null, dbContextFactory.Object, null);

        // Act
        var result = await nodeRepository.GetOrCreateByPubKey(node.PubKey, lightningServiceMock.Object);

        // Assert
        result.Name.Should().Be("TestAlias");
        result.PubKey.Should().Be("TestPubKey");
    }
}
