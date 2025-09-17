// NodeGuard
// Copyright (C) 2025  Elenpay
//
// This program is free software: you can redistribute it and/or modify
// it under the terms of the GNU Affero General Public License as published by
// the Free Software Foundation, either version 3 of the License, or
// (at your option) any later version.
//
// This program is distributed in the hope that it will be useful,
// but WITHOUT ANY WARRANTY, without even the implied warranty of
// MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
// GNU Affero General Public License for more details.
//
// You should have received a copy of the GNU Affero General Public License
// along with this program.  If not, see http://www.gnu.org/licenses/.

using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using NodeGuard.Data.Models;

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
        var context = () => new ApplicationDbContext(options);
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
            UTXOs = new List<FMUTXO> { new() { TxId = "1" } }
        });
        context.ChannelOperationRequests.Add(new ChannelOperationRequest
        {
            Status = ChannelOperationRequestStatus.Pending,
            Utxos = new List<FMUTXO> { new() { TxId = "2" } }
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
            UTXOs = new List<FMUTXO> { new() { TxId = "1" } }
        });
        context.ChannelOperationRequests.Add(new ChannelOperationRequest
        {
            Id = 2,
            Status = ChannelOperationRequestStatus.Pending,
            Utxos = new List<FMUTXO> { new() { TxId = "2" } }
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
            UTXOs = new List<FMUTXO> { new() { TxId = "1" } }
        });
        context.ChannelOperationRequests.Add(new ChannelOperationRequest
        {
            Id = 2,
            Status = ChannelOperationRequestStatus.Pending,
            Utxos = new List<FMUTXO> { new() { TxId = "2" } }
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
            UTXOs = new List<FMUTXO> { new() { TxId = "1" } }
        });
        context.ChannelOperationRequests.Add(new ChannelOperationRequest
        {
            Id = 2,
            Status = ChannelOperationRequestStatus.Pending,
            Utxos = new List<FMUTXO> { new() { TxId = "2" } }
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
            UTXOs = new List<FMUTXO> { new() { TxId = "1" } }
        });
        context.ChannelOperationRequests.Add(new ChannelOperationRequest
        {
            Id = 2,
            Status = ChannelOperationRequestStatus.Failed,
            Utxos = new List<FMUTXO> { new() { TxId = "2" } }
        });
        await context.SaveChangesAsync();

        var result = await futxoRepository.GetLockedUTXOs();
        result.Should().HaveCount(1);
        result[0].Id.Should().Be(1);
    }
}
