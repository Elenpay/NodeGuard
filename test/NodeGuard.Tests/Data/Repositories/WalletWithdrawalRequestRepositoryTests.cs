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

using AutoMapper;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NodeGuard.Automapper;
using NodeGuard.Data.Models;
using NodeGuard.Services;
using NSubstitute;

namespace NodeGuard.Data.Repositories;

public class WalletWithdrawalRequestRepositoryTests
{
    private const int WalletId = 3;
    private const string TxId = "4f7c3a1b2d5e6f708192a3b4c5d6e7f8091a2b3c4d5e6f708192a3b4c5d6e7f8";

    private readonly DbContextOptions<ApplicationDbContext> _options = new DbContextOptionsBuilder<ApplicationDbContext>()
        .UseInMemoryDatabase("WalletWithdrawalRequestRepositoryTests" + Guid.NewGuid())
        .Options;
    private readonly IWithdrawalEventPublisher _publisher = Substitute.For<IWithdrawalEventPublisher>();
    private readonly WalletWithdrawalRequestRepository _repository;

    public WalletWithdrawalRequestRepositoryTests()
    {
        var factory = Substitute.For<IDbContextFactory<ApplicationDbContext>>();
        factory.CreateDbContext().Returns(_ => new ApplicationDbContext(_options));
        factory.CreateDbContextAsync(Arg.Any<CancellationToken>()).Returns(_ => new ApplicationDbContext(_options));
        var mapper = new MapperConfiguration(config => { config.AddProfile<MapperProfile>(); }).CreateMapper();

        _repository = new WalletWithdrawalRequestRepository(
            new Repository<WalletWithdrawalRequest>(NullLogger<WalletWithdrawalRequest>.Instance),
            NullLogger<WalletWithdrawalRequestRepository>.Instance,
            factory,
            mapper,
            notificationService: null!,
            Substitute.For<INBXplorerService>(),
            _publisher);
    }

    private WalletWithdrawalRequest Seed(WalletWithdrawalRequestStatus status, string? txId = null)
    {
        using var context = new ApplicationDbContext(_options);
        var request = new WalletWithdrawalRequest
        {
            Description = "event diff",
            WalletId = WalletId,
            Status = status,
            TxId = txId,
        };
        context.WalletWithdrawalRequests.Add(request);
        context.SaveChanges();
        context.Entry(request).State = EntityState.Detached;
        return request;
    }

    [Fact]
    public void Update_WatchedWallet_PublishesTheTransitionFromTheStoredState()
    {
        _publisher.IsWatched(WalletId).Returns(true);
        var request = Seed(WalletWithdrawalRequestStatus.FinalizingPSBT);

        request.Status = WalletWithdrawalRequestStatus.OnChainConfirmationPending;
        request.TxId = TxId;
        var (ok, _) = _repository.Update(request);

        ok.Should().BeTrue();
        _publisher.Received(1).PublishTransition(WalletId, request.Id,
            WalletWithdrawalRequestStatus.FinalizingPSBT, null,
            WalletWithdrawalRequestStatus.OnChainConfirmationPending, TxId);
    }

    [Fact]
    public void Update_UnwatchedWallet_DoesNotReadOrPublish()
    {
        _publisher.IsWatched(WalletId).Returns(false);
        var request = Seed(WalletWithdrawalRequestStatus.OnChainConfirmationPending, TxId);

        request.Status = WalletWithdrawalRequestStatus.OnChainConfirmed;
        _repository.Update(request).Item1.Should().BeTrue();

        _publisher.DidNotReceiveWithAnyArgs().PublishTransition(default, default, default, default, default, default);
    }

    [Fact]
    public void Update_UnsavedRequest_PublishesNothing()
    {
        _publisher.IsWatched(WalletId).Returns(true);

        _repository.Update(new WalletWithdrawalRequest
        {
            WalletId = WalletId,
            Status = WalletWithdrawalRequestStatus.Cancelled
        });

        _publisher.DidNotReceiveWithAnyArgs().PublishTransition(default, default, default, default, default, default);
    }
}
