using FluentAssertions;
using NodeGuard.Data.Models;
using Microsoft.EntityFrameworkCore;

namespace NodeGuard.Data.Repositories;

public class FUTXORepositoryTests
{
    private readonly Random _random = new();

    private Mock<IDbContextFactory<ApplicationDbContext>> SetupDbContextFactory()
        {
            var dbContextFactory = new Mock<IDbContextFactory<ApplicationDbContext>>();
            var options = new DbContextOptionsBuilder<ApplicationDbContext>()
                .UseInMemoryDatabase(databaseName: "FUTXORepositoryTest" + _random.Next())
                .Options;
            var context = ()=> new ApplicationDbContext(options);
            dbContextFactory.Setup(x => x.CreateDbContext()).Returns(context);
            dbContextFactory.Setup(x => x.CreateDbContextAsync(default)).ReturnsAsync(context);
            return dbContextFactory;
        }

    [Fact]
    public async Task GetLockedUTXOs_emptyArgs()
    {
        var dbContextFactory = SetupDbContextFactory();
        var futxoRepository = new FUTXORepository(null!, null!, dbContextFactory.Object);

        var context = await dbContextFactory.Object.CreateDbContextAsync();

        context.WalletWithdrawalRequests.Add(new WalletWithdrawalRequest
        {
            Description = "1",
            Status = WalletWithdrawalRequestStatus.Pending,
            WalletWithdrawalRequestDestinations = new List<WalletWithdrawalRequestDestination>
            {
                new WalletWithdrawalRequestDestination
                {
                    Address = "1",
                    Amount = 0.01m
                }
            },
            UTXOs = new List<FMUTXO> { new () { TxId = "1"} }
        });
        context.ChannelOperationRequests.Add(new ChannelOperationRequest
        {
            Status = ChannelOperationRequestStatus.Pending,
            Utxos = new List<FMUTXO> { new () { TxId = "2"} }
        });
        await context.SaveChangesAsync();

        var result = await futxoRepository.GetLockedUTXOs();
        result.Should().HaveCount(2);
    }

    [Fact]
    public async Task GetLockedUTXOs_ignoreWithdrawals()
    {
        var dbContextFactory = SetupDbContextFactory();
        var futxoRepository = new FUTXORepository(null!, null!, dbContextFactory.Object);

        var context = dbContextFactory.Object.CreateDbContext();

        context.WalletWithdrawalRequests.Add(new WalletWithdrawalRequest
        {
            Id = 1,
            Description = "1",
            Status = WalletWithdrawalRequestStatus.Pending,
            WalletWithdrawalRequestDestinations = new List<WalletWithdrawalRequestDestination>
            {
                new WalletWithdrawalRequestDestination
                {
                    Address = "1",
                    Amount = 0.01m
                }
            },
            UTXOs = new List<FMUTXO> { new () { TxId = "1"} }
        });
        context.ChannelOperationRequests.Add(new ChannelOperationRequest
        {
            Id = 2,
            Status = ChannelOperationRequestStatus.Pending,
            Utxos = new List<FMUTXO> { new () { TxId = "2"} }
        });
        await context.SaveChangesAsync();

        var result = await futxoRepository.GetLockedUTXOs(1);
        result.Should().HaveCount(1);
        result[0].Id.Should().Be(2);
    }

    [Fact]
    public async Task GetLockedUTXOs_ignoreChannels()
    {
        var dbContextFactory = SetupDbContextFactory();
        var futxoRepository = new FUTXORepository(null!, null!, dbContextFactory.Object);

        var context = dbContextFactory.Object.CreateDbContext();

        context.WalletWithdrawalRequests.Add(new WalletWithdrawalRequest
        {
            Id = 1,
            Description = "1",
            Status = WalletWithdrawalRequestStatus.Pending,
            WalletWithdrawalRequestDestinations = new List<WalletWithdrawalRequestDestination>
            {
                new WalletWithdrawalRequestDestination
                {
                    Address = "1",
                    Amount = 0.01m
                }
            },
            UTXOs = new List<FMUTXO> { new () { TxId = "1"} }
        });
        context.ChannelOperationRequests.Add(new ChannelOperationRequest
        {
            Id = 2,
            Status = ChannelOperationRequestStatus.Pending,
            Utxos = new List<FMUTXO> { new () { TxId = "2"} }
        });
        await context.SaveChangesAsync();

        var result = await futxoRepository.GetLockedUTXOs( null, 2);
        result.Should().HaveCount(1);
        result[0].Id.Should().Be(1);
    }

    [Theory]
    [InlineData(WalletWithdrawalRequestStatus.Pending)]
    [InlineData(WalletWithdrawalRequestStatus.PSBTSignaturesPending)]
    [InlineData(WalletWithdrawalRequestStatus.FinalizingPSBT)]
    [InlineData(WalletWithdrawalRequestStatus.OnChainConfirmationPending)]
    public async Task GetLockedUTXOs_ignoreWithdrawals_StillIgnoredForEveryActiveStatus(WalletWithdrawalRequestStatus status)
    {
        // Regression test: the ignoredWalletWithdrawalRequestId branch must exclude the ignored
        // request regardless of its (active) status, not just when it happens to be Pending (an
        // operator precedence bug used to make the exclusion apply only to the Pending case).
        var dbContextFactory = SetupDbContextFactory();
        var futxoRepository = new FUTXORepository(null!, null!, dbContextFactory.Object);

        var context = dbContextFactory.Object.CreateDbContext();

        context.WalletWithdrawalRequests.Add(new WalletWithdrawalRequest
        {
            Id = 1,
            Description = "1",
            Status = status,
            WalletWithdrawalRequestDestinations = new List<WalletWithdrawalRequestDestination>
            {
                new WalletWithdrawalRequestDestination
                {
                    Address = "1",
                    Amount = 0.01m
                }
            },
            UTXOs = new List<FMUTXO> { new () { TxId = "1"} }
        });
        context.ChannelOperationRequests.Add(new ChannelOperationRequest
        {
            Id = 2,
            Status = ChannelOperationRequestStatus.Pending,
            Utxos = new List<FMUTXO> { new () { TxId = "2"} }
        });
        await context.SaveChangesAsync();

        var result = await futxoRepository.GetLockedUTXOs(1);
        result.Should().HaveCount(1);
        result[0].Id.Should().Be(2);
    }

    [Theory]
    [InlineData(ChannelOperationRequestStatus.Pending)]
    [InlineData(ChannelOperationRequestStatus.PSBTSignaturesPending)]
    [InlineData(ChannelOperationRequestStatus.FinalizingPSBT)]
    [InlineData(ChannelOperationRequestStatus.OnChainConfirmationPending)]
    public async Task GetLockedUTXOs_ignoreChannels_StillIgnoredForEveryActiveStatus(ChannelOperationRequestStatus status)
    {
        // Regression test: same operator precedence bug as above, for the
        // ignoredChannelOperationRequestId branch.
        var dbContextFactory = SetupDbContextFactory();
        var futxoRepository = new FUTXORepository(null!, null!, dbContextFactory.Object);

        var context = dbContextFactory.Object.CreateDbContext();

        context.WalletWithdrawalRequests.Add(new WalletWithdrawalRequest
        {
            Id = 1,
            Description = "1",
            Status = WalletWithdrawalRequestStatus.Pending,
            WalletWithdrawalRequestDestinations = new List<WalletWithdrawalRequestDestination>
            {
                new WalletWithdrawalRequestDestination
                {
                    Address = "1",
                    Amount = 0.01m
                }
            },
            UTXOs = new List<FMUTXO> { new () { TxId = "1"} }
        });
        context.ChannelOperationRequests.Add(new ChannelOperationRequest
        {
            Id = 2,
            Status = status,
            Utxos = new List<FMUTXO> { new () { TxId = "2"} }
        });
        await context.SaveChangesAsync();

        var result = await futxoRepository.GetLockedUTXOs(null, 2);
        result.Should().HaveCount(1);
        result[0].Id.Should().Be(1);
    }

    [Fact]
    public async Task GetLockedUTXOs_failedChannels()
    {
        var dbContextFactory = SetupDbContextFactory();
        var futxoRepository = new FUTXORepository(null!, null!, dbContextFactory.Object);

        var context = dbContextFactory.Object.CreateDbContext();

        context.WalletWithdrawalRequests.Add(new WalletWithdrawalRequest
        {
            Id = 1,
            Description = "1",
            Status = WalletWithdrawalRequestStatus.Failed,
            WalletWithdrawalRequestDestinations = new List<WalletWithdrawalRequestDestination>
            {
                new WalletWithdrawalRequestDestination
                {
                    Address = "1",
                    Amount = 0.01m
                }
            },
            UTXOs = new List<FMUTXO> { new () { TxId = "1"} }
        });
        context.ChannelOperationRequests.Add(new ChannelOperationRequest
        {
            Id = 2,
            Status = ChannelOperationRequestStatus.Pending,
            Utxos = new List<FMUTXO> { new () { TxId = "2"} }
        });
        await context.SaveChangesAsync();

        var result = await futxoRepository.GetLockedUTXOs();
        result.Should().HaveCount(1);
        result[0].Id.Should().Be(2);
    }

    [Fact]
    public async Task GetLockedUTXOs_failedCWithdrawals()
    {
        var dbContextFactory = SetupDbContextFactory();
        var futxoRepository = new FUTXORepository(null, null, dbContextFactory.Object);

        var context = dbContextFactory.Object.CreateDbContext();

        context.WalletWithdrawalRequests.Add(new WalletWithdrawalRequest
        {
            Id = 1,
            Description = "1",
            Status = WalletWithdrawalRequestStatus.Pending,
            WalletWithdrawalRequestDestinations = new List<WalletWithdrawalRequestDestination>
            {
                new WalletWithdrawalRequestDestination
                {
                    Address = "1",
                    Amount = 0.01m
                }
            },
            UTXOs = new List<FMUTXO> { new () { TxId = "1"} }
        });
        context.ChannelOperationRequests.Add(new ChannelOperationRequest
        {
            Id = 2,
            Status = ChannelOperationRequestStatus.Failed,
            Utxos = new List<FMUTXO> { new () { TxId = "2"} }
        });
        await context.SaveChangesAsync();

        var result = await futxoRepository.GetLockedUTXOs();
        result.Should().HaveCount(1);
        result[0].Id.Should().Be(1);
    }
}