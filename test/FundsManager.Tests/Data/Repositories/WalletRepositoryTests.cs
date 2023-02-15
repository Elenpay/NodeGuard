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
    private Mock<IDbContextFactory<ApplicationDbContext>> SetupDbContextFactory()
    {
        var dbContextFactory = new Mock<IDbContextFactory<ApplicationDbContext>>();
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(databaseName: "WalletRepositoryTest")
            .Options;
        var context = new ApplicationDbContext(options);
        dbContextFactory.Setup(x => x.CreateDbContext()).Returns(context);
        return dbContextFactory;
    }
    
    [Fact]
    public async void GetNextSubderivationPath_ReturnsDefault()
    {
        var dbContextFactory = SetupDbContextFactory();
        var context = dbContextFactory.Object.CreateDbContext();
        
        context.InternalWallets.Add(new InternalWallet()
        {
            DerivationPath = "m/48'/1'"
        });
        context.SaveChanges();
        
        var walletRepository = new WalletRepository(null, null, dbContextFactory.Object, null, null);
        var result = await walletRepository.GetNextSubderivationPath();
        result.Should().Be("m/48'/1'/0");
   }
    
    [Fact]
    public async void GetNextSubderivationPath_InconsistentDbState()
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
        
        var walletRepository = new WalletRepository(null, null, dbContextFactory.Object, null, null);
        var act = () => walletRepository.GetNextSubderivationPath();
        await act
            .Should()
            .ThrowAsync<InvalidOperationException>()
            .WithMessage("A finalized hot wallet has no subderivation path");
    }
    
    [Fact]
    public async void GetNextSubderivationPath_ReturnsNext()
    {
        var dbContextFactory = SetupDbContextFactory();
        
        var context = dbContextFactory.Object.CreateDbContext();
        context.Wallets.Add(new Wallet()
        {
            Name = "TestWallet",
            IsFinalised = true, 
            IsHotWallet = true,
            InternalWalletSubDerivationPath = "m/48'/1'/0"
        });
        context.SaveChanges();
        
        var walletRepository = new WalletRepository(null, null, dbContextFactory.Object, null, null);
        var result = await walletRepository.GetNextSubderivationPath();
        result.Should().Be("m/48'/1'/1");
    }
}