using FluentAssertions;
using NodeGuard.Data.Models;
using NodeGuard.Data.Repositories.Interfaces;
using NodeGuard.Services;
using Lnrpc;
using Microsoft.EntityFrameworkCore;

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
        var context = ()=> new ApplicationDbContext(options);
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

    [Fact]
    public async Task GetAllConfiguredByProvider_ForSpark_ReturnsEveryManagedLndNode()
    {
        // Arrange: Spark is NodeGuard's own wallet, so a node needs no swap daemon to pay into it
        var dbContextFactory = SetupDbContextFactory();
        await using (var context = await dbContextFactory.Object.CreateDbContextAsync())
        {
            context.Nodes.AddRange(
                new Node { Name = "lnd-only", PubKey = "02a1", Endpoint = "alice:10009", ChannelAdminMacaroon = "mac" },
                new Node { Name = "with-loop", PubKey = "02a2", Endpoint = "bob:10009", ChannelAdminMacaroon = "mac", LoopdEndpoint = "bob:11010", LoopdMacaroon = "loop" },
                new Node { Name = "remote", PubKey = "02a3" });
            await context.SaveChangesAsync();
        }

        var nodeRepository = new NodeRepository(new Mock<IRepository<Node>>().Object, null, dbContextFactory.Object, null);

        // Act
        var sparkNodes = await nodeRepository.GetAllConfiguredByProvider(SwapProvider.Spark);
        var loopNodes = await nodeRepository.GetAllConfiguredByProvider(SwapProvider.Loop);

        // Assert
        sparkNodes.Select(n => n.Name).Should().BeEquivalentTo("lnd-only", "with-loop");
        loopNodes.Select(n => n.Name).Should().BeEquivalentTo("with-loop");
    }
}
