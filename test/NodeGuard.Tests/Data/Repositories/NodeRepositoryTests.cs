using AutoMapper;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NodeGuard.Automapper;
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

    private static Node AutoOpenNode(string pubKey, bool enabled = true, int? walletId = 10,
        bool nodeDisabled = false)
        => new()
        {
            PubKey = pubKey,
            Name = pubKey,
            Endpoint = "localhost:10009",
            IsNodeDisabled = nodeDisabled,
            AutoChannelOpenEnabled = enabled,
            AutoChannelOpenWalletId = walletId,
        };

    private static NodeRepository AutoOpenSut(Mock<IDbContextFactory<ApplicationDbContext>> dbContextFactory)
        => new(Mock.Of<IRepository<Node>>(), null, dbContextFactory.Object, null);

    [Fact]
    public async Task GetAllWithAutoChannelOpenEnabled_SkipsDisabledOptedOutAndUnfundedNodes()
    {
        // Arrange
        var dbContextFactory = SetupDbContextFactory();
        await using var context = await dbContextFactory.Object.CreateDbContextAsync();
        context.Wallets.Add(new Wallet { Id = 10, Name = "funding", MofN = 1, Keys = new List<Key>() });
        context.Nodes.AddRange(
            AutoOpenNode("eligible"),
            AutoOpenNode("disabled", nodeDisabled: true),
            AutoOpenNode("optedOut", enabled: false),
            // Without a funding wallet there is nothing to pay for the channel, so the job cannot act.
            AutoOpenNode("unfunded", walletId: null));
        await context.SaveChangesAsync();

        // Act
        var result = await AutoOpenSut(dbContextFactory).GetAllWithAutoChannelOpenEnabled();

        // Assert
        result.Should().ContainSingle().Which.PubKey.Should().Be("eligible");
    }

    [Fact]
    public async Task GetAllWithAutoChannelOpenEnabled_EagerLoadsTheFundingWalletAndItsKeys()
    {
        // Arrange
        var dbContextFactory = SetupDbContextFactory();
        await using var context = await dbContextFactory.Object.CreateDbContextAsync();
        context.Wallets.Add(new Wallet
        {
            Id = 10,
            Name = "funding",
            MofN = 1,
            Keys = new List<Key> { new() { Name = "key", XPUB = "xpub" } }
        });
        context.Nodes.Add(AutoOpenNode("eligible"));
        await context.SaveChangesAsync();

        // Act
        var result = await AutoOpenSut(dbContextFactory).GetAllWithAutoChannelOpenEnabled();

        // Assert
        // The job reads the wallet off the node to get a balance, so losing this eager load would
        // silently stop every node from ever opening a channel instead of failing loudly.
        var node = result.Should().ContainSingle().Subject;
        node.AutoChannelOpenWallet.Should().NotBeNull();
        node.AutoChannelOpenWallet!.Keys.Should().ContainSingle();
    }

    [Fact]
    public async Task Update_NodeLoadedWithItsAutoChannelOpenWallet_PersistsWithoutTouchingTheWallet()
    {
        // Arrange
        var dbContextFactory = SetupDbContextFactory();
        await using (var context = await dbContextFactory.Object.CreateDbContextAsync())
        {
            context.Wallets.Add(new Wallet
            {
                Id = 10,
                Name = "funding",
                MofN = 1,
                Keys = new List<Key> { new() { Name = "key", XPUB = "xpub" } }
            });
            context.Nodes.Add(AutoOpenNode("eligible"));
            await context.SaveChangesAsync();
        }

        var mapper = new MapperConfiguration(config => config.AddProfile<MapperProfile>()).CreateMapper();
        var sut = new NodeRepository(new Repository<Node>(NullLogger<Node>.Instance), null, dbContextFactory.Object, mapper);
        var node = (await sut.GetAllWithAutoChannelOpenEnabled()).Single();
        var budgetStart = DateTimeOffset.UtcNow;
        node.AutoChannelOpenBudgetStartDatetime = budgetStart;

        // Act
        var (updated, _) = sut.Update(node);

        // Assert
        // Attaching the eagerly loaded wallet re-inserts its KeyWallet join rows, so the save fails
        updated.Should().BeTrue();
        await using var verify = await dbContextFactory.Object.CreateDbContextAsync();
        var persisted = await verify.Nodes.AsNoTracking().SingleAsync(x => x.PubKey == "eligible");
        persisted.AutoChannelOpenBudgetStartDatetime.Should().Be(budgetStart);
    }
}
