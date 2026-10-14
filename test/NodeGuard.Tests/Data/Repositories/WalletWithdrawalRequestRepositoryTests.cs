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
using NodeGuard.Data.Models;
using NodeGuard.Data.Repositories.Interfaces;
using NodeGuard.Services;

namespace NodeGuard.Data.Repositories;

/// <summary>
/// GetByReferenceIds answers "what happened to this external withdrawal". RBF replacements inherit the reference id of
/// the request they replace, so it has to pick the request of the chain that tells the truth: the latest one, unless a
/// request it replaced was mined instead.
/// </summary>
public class WalletWithdrawalRequestRepositoryTests
{
    private const string Reference = "store-settlement-1";

    private readonly Random _random = new();

    private Mock<IDbContextFactory<ApplicationDbContext>> SetupDbContextFactory()
    {
        var dbContextFactory = new Mock<IDbContextFactory<ApplicationDbContext>>();
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(databaseName: "WalletWithdrawalRequestRepositoryTests" + _random.Next())
            .Options;
        var context = () => new ApplicationDbContext(options);
        dbContextFactory.Setup(x => x.CreateDbContext()).Returns(context);
        dbContextFactory.Setup(x => x.CreateDbContextAsync(default)).ReturnsAsync(context);
        return dbContextFactory;
    }

    // NotificationService is not used by the reads under test.
    private static WalletWithdrawalRequestRepository CreateRepository(Mock<IDbContextFactory<ApplicationDbContext>> factory)
        => new(new Mock<IRepository<WalletWithdrawalRequest>>().Object,
            NullLogger<WalletWithdrawalRequestRepository>.Instance,
            factory.Object,
            new Mock<IMapper>().Object,
            null!,
            new Mock<INBXplorerService>().Object);

    private static async Task<WalletWithdrawalRequestRepository> SeedAsync(
        Mock<IDbContextFactory<ApplicationDbContext>> factory, params WalletWithdrawalRequest[] requests)
    {
        await using var context = await factory.Object.CreateDbContextAsync();
        await context.WalletWithdrawalRequests.AddRangeAsync(requests);
        await context.SaveChangesAsync();
        return CreateRepository(factory);
    }

    private static WalletWithdrawalRequest Request(int id, WalletWithdrawalRequestStatus status, int? replaces = null,
        string reference = Reference) => new()
    {
        Id = id,
        Description = "reference id test",
        ReferenceId = reference,
        Status = status,
        BumpingWalletWithdrawalRequestId = replaces,
    };

    [Fact]
    public async Task GetByReferenceIds_ReplacementInFlight_ReturnsTheLatestRequest()
    {
        var repository = await SeedAsync(SetupDbContextFactory(),
            Request(1, WalletWithdrawalRequestStatus.Bumped),
            Request(2, WalletWithdrawalRequestStatus.OnChainConfirmationPending, replaces: 1));

        var result = await repository.GetByReferenceIds(new List<string> { Reference });

        result.Should().ContainSingle().Which.Id.Should().Be(2);
    }

    [Fact]
    public async Task GetByReferenceIds_ReplacementConfirmed_ReturnsTheReplacement()
    {
        var repository = await SeedAsync(SetupDbContextFactory(),
            Request(1, WalletWithdrawalRequestStatus.Bumped),
            Request(2, WalletWithdrawalRequestStatus.OnChainConfirmed, replaces: 1));

        var result = await repository.GetByReferenceIds(new List<string> { Reference });

        result.Should().ContainSingle().Which.Id.Should().Be(2);
    }

    [Fact]
    public async Task GetByReferenceIds_OriginalMinedInstead_ReturnsTheSettledOriginal()
    {
        var repository = await SeedAsync(SetupDbContextFactory(),
            Request(1, WalletWithdrawalRequestStatus.OnChainConfirmed),
            Request(2, WalletWithdrawalRequestStatus.Failed, replaces: 1));

        var result = await repository.GetByReferenceIds(new List<string> { Reference });

        result.Should().ContainSingle().Which.Id.Should().Be(1,
            "the failed replacement lost the race: reporting it would tell the caller a paid withdrawal failed");
    }

    [Fact]
    public async Task GetByReferenceIds_BumpOfABumpWithTheFirstMined_ReturnsTheFirstRequest()
    {
        var repository = await SeedAsync(SetupDbContextFactory(),
            Request(1, WalletWithdrawalRequestStatus.OnChainConfirmed),
            Request(2, WalletWithdrawalRequestStatus.Bumped, replaces: 1),
            Request(3, WalletWithdrawalRequestStatus.Failed, replaces: 2));

        var result = await repository.GetByReferenceIds(new List<string> { Reference });

        result.Should().ContainSingle().Which.Id.Should().Be(1);
    }

    [Fact]
    public async Task GetByReferenceIds_ReusedReferenceId_DoesNotReportAnUnrelatedEarlierWithdrawal()
    {
        var repository = await SeedAsync(SetupDbContextFactory(),
            Request(1, WalletWithdrawalRequestStatus.OnChainConfirmed),
            Request(2, WalletWithdrawalRequestStatus.Failed));

        var result = await repository.GetByReferenceIds(new List<string> { Reference });

        result.Should().ContainSingle().Which.Id.Should().Be(2,
            "reference ids are not unique: only requests the latest one replaced can stand in for it");
    }

    [Fact]
    public async Task GetByReferenceIds_SeveralReferences_ResolvesEachOnItsOwn()
    {
        var repository = await SeedAsync(SetupDbContextFactory(),
            Request(1, WalletWithdrawalRequestStatus.OnChainConfirmed, reference: "a"),
            Request(2, WalletWithdrawalRequestStatus.Failed, replaces: 1, reference: "a"),
            Request(3, WalletWithdrawalRequestStatus.OnChainConfirmationPending, reference: "b"));

        var result = await repository.GetByReferenceIds(new List<string> { "a", "b" });

        result.Select(r => r.Id).Should().BeEquivalentTo(new[] { 1, 3 });
    }
}
