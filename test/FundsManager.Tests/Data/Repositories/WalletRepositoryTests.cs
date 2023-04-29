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
using FundsManager.Data.Models;
using FundsManager.Data.Repositories.Interfaces;
using FundsManager.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using NBXplorer.DerivationStrategy;
using FundsManager.TestHelpers;


namespace FundsManager.Data.Repositories;

public class WalletRepositoryTests
{
    private readonly Random _random = new();

    private Mock<IDbContextFactory<ApplicationDbContext>> SetupDbContextFactory()
    {
        var dbContextFactory = new Mock<IDbContextFactory<ApplicationDbContext>>();
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(databaseName: "WalletRepositoryTest" + _random.Next())
            .Options;
        var context = ()=> new ApplicationDbContext(options);
        dbContextFactory.Setup(x => x.CreateDbContext()).Returns(context);
        dbContextFactory.Setup(x => x.CreateDbContextAsync(default)).ReturnsAsync(context);
        return dbContextFactory;
    }

    [Fact]
    public async Task GetNextSubderivationPath_ReturnsDefault()
    {
        var dbContextFactory = SetupDbContextFactory();
        var context = dbContextFactory.Object.CreateDbContext();

        context.InternalWallets.Add(new InternalWallet()
        {
            DerivationPath = Constants.DEFAULT_DERIVATION_PATH
        });
        context.SaveChanges();

        var walletRepository = new WalletRepository(null, null, dbContextFactory.Object, null, null, null);
        var result = await walletRepository.GetNextSubderivationPath();
        result.Should().Be("0");
   }

    [Fact]
    public async Task GetNextSubderivationPath_ReturnNextIfNoPrevSubDerivPath()
    {
        var dbContextFactory = SetupDbContextFactory();

        var context = dbContextFactory.Object.CreateDbContext();
        context.Wallets.Add(new Wallet()
        {
            Name = "TestWallet",
            IsFinalised = true,
            IsHotWallet = true
        });
        context.SaveChanges();

        var walletRepository = new WalletRepository(null, null, dbContextFactory.Object, null, null, null);
        var result = await walletRepository.GetNextSubderivationPath();
        result.Should().Be("0");
    }

    [Fact]
    public async Task GetNextSubderivationPath_ReturnsNext()
    {
        var dbContextFactory = SetupDbContextFactory();

        var context = dbContextFactory.Object.CreateDbContext();
        context.InternalWallets.Add(new InternalWallet()
        {
            DerivationPath = Constants.DEFAULT_DERIVATION_PATH
        });
        context.Wallets.Add(new Wallet()
        {
            Name = "TestWallet",
            IsFinalised = true,
            IsHotWallet = true,
            InternalWalletSubDerivationPath = "0"
        });
        context.SaveChanges();

        var walletRepository = new WalletRepository(null, null, dbContextFactory.Object, null, null, null);
        var result = await walletRepository.GetNextSubderivationPath();
        result.Should().Be("1");
    }

    [Fact]
    public async Task GetNextSubderivationPath_DoesntReturnNotFinalisedWallet()
    {
        var dbContextFactory = SetupDbContextFactory();

        var context = dbContextFactory.Object.CreateDbContext();
        context.InternalWallets.Add(new InternalWallet()
        {
            DerivationPath = Constants.DEFAULT_DERIVATION_PATH
        });
        context.Wallets.Add(new Wallet()
        {
            Name = "TestWallet",
            IsFinalised = true,
            IsHotWallet = true,
            InternalWalletSubDerivationPath = "0"
        });
        context.Wallets.Add(new Wallet()
        {
            Name = "NotFinalised",
            IsFinalised = false,
            IsHotWallet = true,
            InternalWalletSubDerivationPath = "1"
        });
        context.SaveChanges();

        var walletRepository = new WalletRepository(null, null, dbContextFactory.Object, null, null, null);
        var result = await walletRepository.GetNextSubderivationPath();
        result.Should().Be("1");
    }

    [Fact]
    public async Task GetNextSubderivationPath_ReturnsSubderivedWallet()
    {
        var dbContextFactory = SetupDbContextFactory();

        var context = dbContextFactory.Object.CreateDbContext();
        context.InternalWallets.Add(new InternalWallet()
        {
            DerivationPath = Constants.DEFAULT_DERIVATION_PATH
        });
        context.Wallets.Add(new Wallet()
        {
            Name = "TestWallet1",
            IsFinalised = true,
            IsHotWallet = true,
            IsCompromised = true,
            InternalWalletSubDerivationPath = "0"
        });
        context.Wallets.Add(new Wallet()
        {
            Name = "TestWallet2",
            IsFinalised = true,
            IsHotWallet = true,
            InternalWalletSubDerivationPath = "1"
        });
        context.SaveChanges();

        var walletRepository = new WalletRepository(null, null, dbContextFactory.Object, null, null, null);
        var result = await walletRepository.GetNextSubderivationPath();
        result.Should().Be("2");
    }
    
    private WalletRepository SetupTestClassForImportBIP39Wallet()
    {
        var keyRepositoryMock = new Mock<IKeyRepository>();
        var loggerMock = new Mock<ILogger<WalletRepository>>();
        var nbXplorerServiceMock = new Mock<INBXplorerService>();

        MockHelpers.GetMockRepository<Wallet>();

        var setupDbContextFactory = SetupDbContextFactory();

        keyRepositoryMock.Setup(x => x.AddAsync(It.IsAny<Key>())).ReturnsAsync((true, (string?)null));
        keyRepositoryMock.Setup(x => x.GetCurrentInternalWalletKey(It.IsAny<string>())).ReturnsAsync(new Key { XPUB = "tpubDFjD2KhvH1qGE99v1UgQebupcJEPHxjBRkxjmWxesPPz5jP38GBBHCqimqHtiidrVo5P8PxusC38VT1FLEQGxerdLnvpJHrv6nWeZ62M1kF", Name = "123" });
        nbXplorerServiceMock.Setup(x => x.TrackAsync(It.IsAny<DerivationStrategyBase>(), default)).Returns(Task.CompletedTask);
        nbXplorerServiceMock.Setup(x => x.ScanUTXOSetAsync(It.IsAny<DerivationStrategyBase>(), 1000, 30000, null, default)).Returns(Task.CompletedTask);

        var internalWalletRepositoryMock = new Mock<IInternalWalletRepository>();
        internalWalletRepositoryMock.Setup(x => x.GetCurrentInternalWallet()).ReturnsAsync(new InternalWallet
        {
            Id = 0,
            CreationDatetime = default,
            UpdateDatetime = default,
            DerivationPath = "m/44'/0'/0'",
            MnemonicString = "pistol maple assume music globe junk fury gasp crack bless eager donate",
        });

        return new WalletRepository(new Repository<Wallet>(new Mock<ILogger<Wallet>>().Object), loggerMock.Object, setupDbContextFactory.Object, internalWalletRepositoryMock.Object, keyRepositoryMock.Object, nbXplorerServiceMock.Object);
    }

    
    [Fact]
    public async Task ImportBIP39Wallet_WhenValidInput_ShouldReturnSuccess()
    {
        // Arrange
        var seedphrase = "pistol maple assume music globe junk fury gasp crack bless eager donate";
        var derivationPath = "m/44'/0'/0'";
        var userId = "testUser";
        
        var testClass = SetupTestClassForImportBIP39Wallet();

        // Act
        var (result, errorMessage) = await testClass.ImportBIP39Wallet("name", "description", seedphrase, derivationPath, userId);

        // Assert
        result.Should().BeTrue();
        errorMessage.Should().BeNull();
    }

    
    [Fact]
    public async Task ImportBIP39Wallet_WhenSeedPhraseIsEmpty_ShouldReturnError()
    {
        // Arrange
        var seedphrase = "";
        var derivationPath = "m/44'/0'/0'";
        var userId = "testUser";

        var testClass = SetupTestClassForImportBIP39Wallet();

        // Act
        var (result, errorMessage) = await testClass.ImportBIP39Wallet("name", "description", seedphrase, derivationPath, userId);

        // Assert
        result.Should().BeFalse();
        errorMessage.Should().Be("Seedphrase is empty");
    }
    
    [Fact]
    public async Task ImportBIP39Wallet_WhenDerivationPathIsEmpty_ShouldReturnError()
    {
        // Arrange
        var seedphrase = "pistol maple assume music globe junk fury gasp crack bless eager donate";
        var derivationPath = "";
        var userId = "testUser";

        var testClass = SetupTestClassForImportBIP39Wallet();

        // Act
        var (result, errorMessage) = await testClass.ImportBIP39Wallet("name", "description", seedphrase, derivationPath, userId);

        // Assert
        result.Should().BeFalse();
        errorMessage.Should().Be("Derivation path is empty");
    }
    
    [Fact]
    public async Task ImportBIP39Wallet_WhenSeedPhraseIsInvalid_ShouldReturnError()
    {
        // Arrange
        var seedphrase = "invalid seed phrase with wrong words";
        var derivationPath = "m/44'/0'/0'";
        var userId = "testUser";

        var testClass = SetupTestClassForImportBIP39Wallet();

        // Act
        var (result, errorMessage) = await testClass.ImportBIP39Wallet("name", "description", seedphrase, derivationPath, userId);

        // Assert
        result.Should().BeFalse();
    }



}