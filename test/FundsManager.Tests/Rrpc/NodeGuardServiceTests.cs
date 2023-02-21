using FluentAssertions;
using FundsManager.Data;
using FundsManager.Data.Models;
using FundsManager.Data.Repositories;
using Microsoft.EntityFrameworkCore;
using Nodeguard;
using Quartz;
using Wallet = FundsManager.Data.Models.Wallet;

namespace FundsManager.Rpc;

public class NodeGuardServiceTests
{
    private readonly Random _random = new();
    
    private (Mock<IDbContextFactory<ApplicationDbContext>>, ApplicationDbContext) SetupDbContextFactory()
    {
        var dbContextFactory = new Mock<IDbContextFactory<ApplicationDbContext>>();
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(databaseName: "WalletRepositoryTest" + _random.Next())
            .Options;
        var context = new ApplicationDbContext(options);
        dbContextFactory.Setup(x => x.CreateDbContext()).Returns(context);
        dbContextFactory.Setup(x => x.CreateDbContextAsync(default)).ReturnsAsync(context);
        return (dbContextFactory, context);
    }
    
    [Fact]
    public async void GetAvailableWallets_ReturnsAllWallets()
    {
        var (dbContextFactory, context) = SetupDbContextFactory();
        var scheduleFactory = new Mock<ISchedulerFactory>();

        var internalWallet = new InternalWallet()
        {
            DerivationPath = ""
        };
        context.Wallets.Add(new Wallet()
        {
            Name = "TestWallet1",
            IsFinalised = true,
            IsHotWallet = true,
            InternalWallet = internalWallet,
            Keys = new List<Key>(),
        });
        context.Wallets.Add(new Wallet()
        {
            Name = "TestWallet2",
            IsFinalised = true,
            IsHotWallet = false,
            InternalWallet = internalWallet,
            Keys = new List<Key>()
        });
        context.SaveChanges();
        
        var walletRepository = new WalletRepository(null, null, dbContextFactory.Object, null, null, null);

        var request = new GetAvailableWalletsRequest();
        var nodeGuardService = new NodeGuardService(null, null,walletRepository,null,null,null,null, scheduleFactory.Object);
        var result = await nodeGuardService.GetAvailableWallets(request, null);
        
        result.Wallets.ToList().Count().Should().Be(2);
    } 
    
    [Fact]
    public async void GetAvailableWallets_ReturnsTypeHot()
    {
        var (dbContextFactory, context) = SetupDbContextFactory();
        var scheduleFactory = new Mock<ISchedulerFactory>();

        var internalWallet = new InternalWallet()
        {
            DerivationPath = ""
        };
        context.Wallets.Add(new Wallet()
        {
            Name = "TestWallet1",
            IsFinalised = true,
            IsHotWallet = true,
            InternalWallet = internalWallet,
            Keys = new List<Key>(),
        });
        context.Wallets.Add(new Wallet()
        {
            Name = "TestWallet2",
            IsFinalised = true,
            IsHotWallet = false,
            InternalWallet = internalWallet,
            Keys = new List<Key>()
        });
        context.SaveChanges();
        
        var walletRepository = new WalletRepository(null, null, dbContextFactory.Object, null, null, null);

        var request = new GetAvailableWalletsRequest()
        {
            WalletType = WALLET_TYPE.Hot
        };
        var nodeGuardService = new NodeGuardService(null, null,walletRepository,null,null,null,null, scheduleFactory.Object);
        var result = await nodeGuardService.GetAvailableWallets(request, null);
        
        result.Wallets.ToList().Count().Should().Be(1);
        result.Wallets.ToList().FirstOrDefault()!.IsHotWallet.Should().Be(true);
    } 
    
    [Fact]
    public async void GetAvailableWallets_ReturnsTypeCold()
    {
        var (dbContextFactory, context) = SetupDbContextFactory();
        var scheduleFactory = new Mock<ISchedulerFactory>();

        var internalWallet = new InternalWallet()
        {
            DerivationPath = ""
        };
        context.Wallets.Add(new Wallet()
        {
            Name = "TestWallet1",
            IsFinalised = true,
            IsHotWallet = true,
            InternalWallet = internalWallet,
            Keys = new List<Key>(),
        });
        context.Wallets.Add(new Wallet()
        {
            Name = "TestWallet2",
            IsFinalised = true,
            IsHotWallet = false,
            InternalWallet = internalWallet,
            Keys = new List<Key>()
        });
        context.SaveChanges();
        
        var walletRepository = new WalletRepository(null, null, dbContextFactory.Object, null, null, null);

        var request = new GetAvailableWalletsRequest()
        {
            WalletType = WALLET_TYPE.Cold
        };
        var nodeGuardService = new NodeGuardService(null, null,walletRepository,null,null,null,null, scheduleFactory.Object);
        var result = await nodeGuardService.GetAvailableWallets(request, null);
        
        result.Wallets.ToList().Count().Should().Be(1);
        result.Wallets.ToList().FirstOrDefault()!.IsHotWallet.Should().Be(false);
    }
    
    [Fact]
    public async void GetAvailableWallets_ReturnsIds()
    {
        var (dbContextFactory, context) = SetupDbContextFactory();
        var scheduleFactory = new Mock<ISchedulerFactory>();

        var internalWallet = new InternalWallet()
        {
            DerivationPath = ""
        };
        context.Wallets.Add(new Wallet()
        {
            Id = 1,
            Name = "TestWallet1",
            IsFinalised = true,
            IsHotWallet = true,
            InternalWallet = internalWallet,
            Keys = new List<Key>(),
        });
        context.Wallets.Add(new Wallet()
        {
            Id = 2,
            Name = "TestWallet2",
            IsFinalised = true,
            IsHotWallet = false,
            InternalWallet = internalWallet,
            Keys = new List<Key>()
        });
        context.Wallets.Add(new Wallet()
        {
            Id = 3,
            Name = "TestWallet3",
            IsFinalised = true,
            IsHotWallet = false,
            InternalWallet = internalWallet,
            Keys = new List<Key>()
        });
        context.SaveChanges();
        
        var walletRepository = new WalletRepository(null, null, dbContextFactory.Object, null, null, null);

        var request = new GetAvailableWalletsRequest()
        {
            Id = { 1, 3 }
        };
        var nodeGuardService = new NodeGuardService(null, null,walletRepository,null,null,null,null, scheduleFactory.Object);
        var result = await nodeGuardService.GetAvailableWallets(request, null);
        
        result.Wallets.ToList().Count().Should().Be(2);
        result.Wallets.ToList().FirstOrDefault()!.Id.Should().Be(1);
        result.Wallets.ToList().LastOrDefault()!.Id.Should().Be(3);
    }
}