using FluentAssertions;
using FundsManager.Data.Models;
using FundsManager.Data.Repositories.Interfaces;
using FundsManager.Services;
using Lnrpc;
using Microsoft.EntityFrameworkCore;

namespace FundsManager.Data.Repositories;

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

        lightningServiceMock.Setup(service => service.GetNodeInfo(It.IsAny<string>()))
            .ReturnsAsync(new LightningNode() { Alias = "TestAlias", PubKey = "TestPubKey" });

        repositoryMock.Setup(repository => repository.AddAsync(It.IsAny<Node>(), It.IsAny<ApplicationDbContext>()))
            .ReturnsAsync((true, null));

        var nodeRepository = new NodeRepository(repositoryMock.Object, null, dbContextFactory.Object, null);

        // Act
        var result = await nodeRepository.GetOrCreateByPubKey("abc", lightningServiceMock.Object);

        // Assert
        result.Name.Should().Be("TestAlias");
        result.PubKey.Should().Be("TestPubKey");
    }
}