using FluentAssertions;
using NodeGuard.Data.Models;
using NodeGuard.Data.Repositories.Interfaces;
using Microsoft.EntityFrameworkCore;

namespace NodeGuard.Data.Repositories;

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

    private static ChannelOperationRequest Committed(
        int sourceNodeId, long satsAmount, ChannelOperationRequestStatus status,
        DateTimeOffset createdAt, OperationRequestType type = OperationRequestType.Open)
        => new()
        {
            SourceNodeId = sourceNodeId,
            DestNodeId = 99,
            SatsAmount = satsAmount,
            Status = status,
            RequestType = type,
            CreationDatetime = createdAt,
        };

    private static ChannelOperationRequestRepository BudgetSut(
        Mock<IDbContextFactory<ApplicationDbContext>> dbContextFactory)
        => new(Mock.Of<IRepository<ChannelOperationRequest>>(), null, dbContextFactory.Object, null, null);

    [Fact]
    public async Task GetOpenSatsCommittedSince_SumsOnlyLiveOpensOfThatSourceNodeInThePeriod()
    {
        // Arrange
        var dbContextFactory = SetupDbContextFactory();
        await using var context = await dbContextFactory.Object.CreateDbContextAsync();
        var now = DateTimeOffset.UtcNow;
        var since = now.AddDays(-1);

        await context.ChannelOperationRequests.AddRangeAsync(
            Committed(1, 100, ChannelOperationRequestStatus.Pending, now.AddHours(-2)),
            // The period start is inclusive, otherwise the first open of a budget period escapes it.
            Committed(1, 200, ChannelOperationRequestStatus.OnChainConfirmed, since),
            Committed(1, 400, ChannelOperationRequestStatus.Approved, now),
            // Sats that will never leave the wallet must not keep eating the budget.
            Committed(1, 800, ChannelOperationRequestStatus.Cancelled, now),
            Committed(1, 1600, ChannelOperationRequestStatus.Rejected, now),
            Committed(1, 3200, ChannelOperationRequestStatus.Failed, now),
            Committed(1, 6400, ChannelOperationRequestStatus.Pending, since.AddSeconds(-1)),
            Committed(2, 12800, ChannelOperationRequestStatus.Pending, now),
            // A close returns funds rather than committing them.
            Committed(1, 25600, ChannelOperationRequestStatus.Pending, now, OperationRequestType.Close));
        await context.SaveChangesAsync();

        // Act
        var result = await BudgetSut(dbContextFactory).GetOpenSatsCommittedSince(1, since);

        // Assert
        result.Should().Be(700);
    }

    [Fact]
    public async Task GetOpenSatsCommittedSince_ReturnsZero_WhenNothingMatches()
    {
        // Arrange
        var dbContextFactory = SetupDbContextFactory();

        // Act
        // A node in its first budget period has no rows at all, and the job would divide by a null
        // budget rather than skip the node if this threw.
        var result = await BudgetSut(dbContextFactory).GetOpenSatsCommittedSince(1, DateTimeOffset.UtcNow.AddDays(-1));

        // Assert
        result.Should().Be(0);
    }
}
