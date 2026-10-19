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
/// RBF replacements point at the request they replace through BumpingWalletWithdrawalRequestId, and a request's status
/// only describes its own transaction. These reads answer the questions built on that link: every request a reference
/// id maps to (the whole chain, each reporting its own status), whether a request already has a live replacement, and
/// which rows the monitor's reconciliation has to look at.
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

    private async Task<WalletWithdrawalRequestRepository> SeedAsync(params WalletWithdrawalRequest[] requests)
    {
        var factory = SetupDbContextFactory();
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

    // ---- GetByReferenceIds ---------------------------------------------------------------------------------------

    [Fact]
    public async Task GetByReferenceIds_ReturnsEveryRequestOfTheChainOldestFirst()
    {
        var repository = await SeedAsync(
            Request(3, WalletWithdrawalRequestStatus.Failed, replaces: 2),
            Request(1, WalletWithdrawalRequestStatus.OnChainConfirmed),
            Request(2, WalletWithdrawalRequestStatus.Failed, replaces: 1),
            Request(4, WalletWithdrawalRequestStatus.OnChainConfirmationPending, reference: "other"));

        var result = await repository.GetByReferenceIds(new List<string> { Reference });

        result.Select(r => r.Id).Should().Equal(new[] { 1, 2, 3 },
            "the original and each of its replacements carry the reference id; the caller sees what happened to each");
    }

    [Fact]
    public async Task GetByReferenceIds_SeveralReferences_ReturnsAllOfEach()
    {
        var repository = await SeedAsync(
            Request(1, WalletWithdrawalRequestStatus.OnChainConfirmed, reference: "a"),
            Request(2, WalletWithdrawalRequestStatus.Failed, replaces: 1, reference: "a"),
            Request(3, WalletWithdrawalRequestStatus.OnChainConfirmationPending, reference: "b"),
            Request(4, WalletWithdrawalRequestStatus.Pending, reference: "c"));

        var result = await repository.GetByReferenceIds(new List<string> { "a", "b" });

        result.Select(r => r.Id).Should().Equal(new[] { 1, 2, 3 });
    }

    [Fact]
    public async Task GetByReferenceIds_UnknownReference_ReturnsNothing()
    {
        var repository = await SeedAsync(Request(1, WalletWithdrawalRequestStatus.OnChainConfirmed));

        var result = await repository.GetByReferenceIds(new List<string> { "missing" });

        result.Should().BeEmpty();
    }

    [Fact]
    public async Task GetByReferenceIds_LoadsTheReplacements()
    {
        var repository = await SeedAsync(
            Request(1, WalletWithdrawalRequestStatus.OnChainConfirmationPending),
            Request(2, WalletWithdrawalRequestStatus.OnChainConfirmationPending, replaces: 1));

        var result = await repository.GetByReferenceIds(new List<string> { Reference });

        result.First().Replacements.Select(r => r.Id).Should().Equal(new[] { 2 },
            "the API derives the reported status of a replaced request from them");
    }

    // ---- replacement lookups --------------------------------------------------------------------------------------

    [Fact]
    public async Task GetByIds_LoadsTheReplacements()
    {
        var repository = await SeedAsync(
            Request(1, WalletWithdrawalRequestStatus.OnChainConfirmationPending),
            Request(2, WalletWithdrawalRequestStatus.OnChainConfirmationPending, replaces: 1));

        var result = await repository.GetByIds(new List<int> { 1 });

        result.Single().Replacements.Select(r => r.Id).Should().Equal(new[] { 2 });
    }

    [Theory]
    [InlineData(WalletWithdrawalRequestStatus.Pending, true)]
    [InlineData(WalletWithdrawalRequestStatus.PSBTSignaturesPending, true)]
    [InlineData(WalletWithdrawalRequestStatus.FinalizingPSBT, true)]
    [InlineData(WalletWithdrawalRequestStatus.OnChainConfirmationPending, true)]
    [InlineData(WalletWithdrawalRequestStatus.OnChainConfirmed, false)]
    [InlineData(WalletWithdrawalRequestStatus.Failed, false)]
    [InlineData(WalletWithdrawalRequestStatus.Cancelled, false)]
    [InlineData(WalletWithdrawalRequestStatus.Rejected, false)]
    [InlineData(WalletWithdrawalRequestStatus.Bumped, false)]
    public async Task HasLiveReplacementAsync_CountsOnlyLiveStatuses(WalletWithdrawalRequestStatus replacementStatus, bool expected)
    {
        var repository = await SeedAsync(
            Request(1, WalletWithdrawalRequestStatus.OnChainConfirmationPending),
            Request(2, replacementStatus, replaces: 1));

        (await repository.HasLiveReplacementAsync(1)).Should().Be(expected);
    }

    [Fact]
    public async Task HasLiveReplacementAsync_UnrelatedLiveRequest_DoesNotCount()
    {
        var repository = await SeedAsync(
            Request(1, WalletWithdrawalRequestStatus.OnChainConfirmationPending),
            Request(2, WalletWithdrawalRequestStatus.OnChainConfirmationPending));

        (await repository.HasLiveReplacementAsync(1)).Should().BeFalse();
    }

    [Fact]
    public async Task GetRbfChainAsync_ReturnsTheWholeChainFromAnyMember()
    {
        var repository = await SeedAsync(
            Request(1, WalletWithdrawalRequestStatus.OnChainConfirmationPending),
            Request(2, WalletWithdrawalRequestStatus.OnChainConfirmationPending, replaces: 1),
            Request(3, WalletWithdrawalRequestStatus.OnChainConfirmationPending, replaces: 2),
            Request(4, WalletWithdrawalRequestStatus.Failed, replaces: 1),
            Request(5, WalletWithdrawalRequestStatus.OnChainConfirmationPending));

        var chain = await repository.GetRbfChainAsync(2);

        chain.Select(r => r.Id).Should().Equal(new[] { 1, 2, 3, 4 },
            "walking up finds what 2 replaced, walking down finds every replacement, including the abandoned sibling");
    }

    [Fact]
    public async Task GetRbfChainAsync_UnlinkedRequest_ReturnsItself()
    {
        var repository = await SeedAsync(
            Request(1, WalletWithdrawalRequestStatus.OnChainConfirmationPending),
            Request(2, WalletWithdrawalRequestStatus.OnChainConfirmationPending));

        var chain = await repository.GetRbfChainAsync(1);

        chain.Select(r => r.Id).Should().Equal(new[] { 1 });
    }

    [Fact]
    public async Task GetLiveRbfLinkedRequestsAsync_ReturnsLiveRowsWithALinkOrReplacements_NotFinalizing()
    {
        var repository = await SeedAsync(
            Request(1, WalletWithdrawalRequestStatus.OnChainConfirmationPending),
            Request(2, WalletWithdrawalRequestStatus.FinalizingPSBT, replaces: 1),
            Request(3, WalletWithdrawalRequestStatus.Pending),
            Request(4, WalletWithdrawalRequestStatus.Failed, replaces: 1),
            Request(5, WalletWithdrawalRequestStatus.OnChainConfirmed),
            Request(6, WalletWithdrawalRequestStatus.PSBTSignaturesPending, replaces: 5));

        var rows = await repository.GetLiveRbfLinkedRequestsAsync();

        rows.Select(r => r.Id).Should().BeEquivalentTo(new[] { 1, 6 },
            "1 is replaced, 6 replaces; 2 is being broadcast, 3 is unlinked, 4 and 5 are not live");
    }
}
