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
using Microsoft.EntityFrameworkCore;
using Moq;

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
        var context = new ApplicationDbContext(options);
        dbContextFactory.Setup(x => x.CreateDbContext()).Returns(context);
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
}