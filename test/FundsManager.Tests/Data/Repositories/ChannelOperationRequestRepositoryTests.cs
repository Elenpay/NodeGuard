using FluentAssertions;
using FundsManager.Data.Models;
using FundsManager.Data.Repositories.Interfaces;
using Microsoft.EntityFrameworkCore;

namespace FundsManager.Data.Repositories;

public class ChannelOperationRequestRepositoryTests
{
    private readonly Random _random = new();

    private Mock<IDbContextFactory<ApplicationDbContext>> SetupDbContextFactory()
    {
        var dbContextFactory = new Mock<IDbContextFactory<ApplicationDbContext>>();
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(databaseName: "ChannelOperationRequestRepositoryTests" + _random.Next())
            .Options;
        var context = ()=> new ApplicationDbContext(options);
        dbContextFactory.Setup(x => x.CreateDbContext()).Returns(context);
        dbContextFactory.Setup(x => x.CreateDbContextAsync(default)).ReturnsAsync(context);
        return dbContextFactory;
    }

    [Fact]
    public async Task AddAsync_ChannelCloseOperations()
    {
        // Arrange
        var dbContextFactory = SetupDbContextFactory();
        var repository = new Mock<IRepository<ChannelOperationRequest>>();

        repository
            .Setup(x => x.AddAsync(It.IsAny<ChannelOperationRequest>(), It.IsAny<ApplicationDbContext>()))
            .ReturnsAsync((true, null));

        await using var context = await dbContextFactory.Object.CreateDbContextAsync();

        var request1 = new ChannelOperationRequest()
        {
            RequestType = OperationRequestType.Close,
            Status = ChannelOperationRequestStatus.OnChainConfirmationPending
        };
        await context.ChannelOperationRequests.AddAsync(request1);
        await context.SaveChangesAsync();

        var channelOperationRequestRepository = new ChannelOperationRequestRepository(repository.Object, null, dbContextFactory.Object, null, null);

        // Act
        var result = await channelOperationRequestRepository.AddAsync(request1);

        // Assert
        result.Item1.Should().BeTrue();
        result.Item2.Should().BeNull();
    }

    [Fact]
    public async Task AddAsync_ChannelOpenOperations_SimultaneousOpsNotAllowed()
    {
        // Arrange
        Constants.ALLOW_SIMULTANEOUS_CHANNEL_OPENING_OPERATIONS = false;

        var dbContextFactory = SetupDbContextFactory();
        var repository = new Mock<IRepository<ChannelOperationRequest>>();

        repository
            .Setup(x => x.AddAsync(It.IsAny<ChannelOperationRequest>(), It.IsAny<ApplicationDbContext>()))
            .ReturnsAsync((true, null));

        var dbContextFactoryObject = dbContextFactory.Object;
        var context = await dbContextFactoryObject.CreateDbContextAsync();

        var request1 = new ChannelOperationRequest()
        {
            RequestType = OperationRequestType.Open,
            SourceNodeId = 1,
            DestNodeId = 2,
            Status = ChannelOperationRequestStatus.OnChainConfirmationPending
        };

        await context.ChannelOperationRequests.AddAsync(request1);
        await context.SaveChangesAsync();

        var channelOperationRequestRepository = new ChannelOperationRequestRepository(repository.Object, null, dbContextFactoryObject, null, null);

        // Act
        var result = await channelOperationRequestRepository.AddAsync(request1);

        // Assert
        result.Item1.Should().BeFalse();
        result.Item2.Should().Be("Error, a channel operation request with the same source and destination node is in pending status, wait for that request to finalise before submitting a new request");
    }

    [Fact]
    public async Task AddAsync_ChannelOpenOperations_SimultaneousOpsAllowed()
    {
        // Arrange
        Constants.ALLOW_SIMULTANEOUS_CHANNEL_OPENING_OPERATIONS = true;

        var dbContextFactory = SetupDbContextFactory();
        var repository = new Mock<IRepository<ChannelOperationRequest>>();

        repository
            .Setup(x => x.AddAsync(It.IsAny<ChannelOperationRequest>(), It.IsAny<ApplicationDbContext>()))
            .ReturnsAsync((true, null));

        var dbContextFactoryObject = dbContextFactory.Object;
        var context = await dbContextFactoryObject.CreateDbContextAsync();

        var request1 = new ChannelOperationRequest()
        {
            RequestType = OperationRequestType.Open,
            SourceNodeId = 1,
            DestNodeId = 2,
            Status = ChannelOperationRequestStatus.OnChainConfirmationPending
        };

        await context.ChannelOperationRequests.AddAsync(request1);
        await context.SaveChangesAsync();

        var channelOperationRequestRepository = new ChannelOperationRequestRepository(repository.Object, null, dbContextFactoryObject, null, null);

        // Act
        var result = await channelOperationRequestRepository.AddAsync(request1);

        // Assert
        result.Item1.Should().BeTrue();
        result.Item2.Should().BeNull();
    }
}