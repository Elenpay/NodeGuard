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
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NBitcoin;
using NodeGuard.Data.Models;
using NodeGuard.Data.Repositories.Interfaces;
using NodeGuard.Helpers;

namespace NodeGuard.Data.Repositories;

/// <summary>
/// Template handling in the PSBT repository. The unique index itself cannot be exercised here (the EF
/// InMemory provider ignores IsUnique and HasFilter); that lives in the E2E suite. What is covered is
/// everything around it: templates skip the approval validator and are inserted directly, the template
/// reader, and the validator refusing to anchor an approval when two templates exist.
/// </summary>
public class WalletWithdrawalRequestPsbtRepositoryTests
{
    private readonly Random _random = new();

    private Mock<IDbContextFactory<ApplicationDbContext>> SetupDbContextFactory()
    {
        var dbContextFactory = new Mock<IDbContextFactory<ApplicationDbContext>>();
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(databaseName: "WalletWithdrawalRequestPsbtRepositoryTests" + _random.Next())
            .Options;
        var context = () => new ApplicationDbContext(options);
        dbContextFactory.Setup(x => x.CreateDbContext()).Returns(context);
        dbContextFactory.Setup(x => x.CreateDbContextAsync(default)).ReturnsAsync(context);
        return dbContextFactory;
    }

    private static WalletWithdrawalRequestPsbtRepository CreateRepository(
        Mock<IDbContextFactory<ApplicationDbContext>> factory, Mock<IRepository<WalletWithdrawalRequestPSBT>>? generic = null)
        => new((generic ?? new Mock<IRepository<WalletWithdrawalRequestPSBT>>()).Object,
            NullLogger<WalletWithdrawalRequestPsbtRepository>.Instance, factory.Object);

    private static string UnsignedPsbt(int seed)
    {
        var network = CurrentNetworkHelper.GetCurrentNetwork();
        var tx = network.CreateTransaction();
        tx.Inputs.Add(new TxIn(new OutPoint(uint256.Parse(seed.ToString("x").PadLeft(64, '0')), 0)));
        tx.Outputs.Add(new TxOut(Money.Coins(0.01m),
            BitcoinAddress.Create("bcrt1q8k3av6q5yp83rn332lx8a90k6kukhg28hs5qw7krdq95t629hgsqk6ztmf", network)));
        return PSBT.FromTransaction(tx, network).ToBase64();
    }

    private static async Task<int> SeedRequestAsync(ApplicationDbContext context, params WalletWithdrawalRequestPSBT[] psbts)
    {
        var request = new WalletWithdrawalRequest
        {
            Description = "psbt repository test",
            Status = WalletWithdrawalRequestStatus.Pending,
            WalletWithdrawalRequestPSBTs = psbts.ToList(),
        };
        await context.WalletWithdrawalRequests.AddAsync(request);
        await context.SaveChangesAsync();
        return request.Id;
    }

    [Fact]
    public async Task AddAsync_Template_IsInsertedWithoutRunningTheApprovalValidator()
    {
        var factory = SetupDbContextFactory();
        var generic = new Mock<IRepository<WalletWithdrawalRequestPSBT>>();
        int requestId;
        await using (var context = await factory.Object.CreateDbContextAsync())
        {
            requestId = await SeedRequestAsync(context);
        }

        var repository = CreateRepository(factory, generic);

        var result = await repository.AddAsync(new WalletWithdrawalRequestPSBT
        {
            WalletWithdrawalRequestId = requestId,
            IsTemplatePSBT = true,
            PSBT = UnsignedPsbt(1),
        });

        result.Item1.Should().BeTrue();
        result.Item2.Should().BeNull();

        // Direct insert: the generic repository (which would swallow a unique violation) is not involved.
        generic.Verify(x => x.AddAsync(It.IsAny<WalletWithdrawalRequestPSBT>(), It.IsAny<ApplicationDbContext>()), Times.Never);

        await using var verify = await factory.Object.CreateDbContextAsync();
        var stored = await verify.WalletWithdrawalRequestPSBTs.Where(x => x.WalletWithdrawalRequestId == requestId).ToListAsync();
        stored.Should().ContainSingle(x => x.IsTemplatePSBT);

        // A template never moves the request to PSBTSignaturesPending; only a human approval does.
        (await verify.WalletWithdrawalRequests.SingleAsync(x => x.Id == requestId)).Status
            .Should().Be(WalletWithdrawalRequestStatus.Pending);
    }

    [Fact]
    public async Task GetTemplateByRequestId_ReturnsTheTemplateRowOnly()
    {
        var factory = SetupDbContextFactory();
        int requestId;
        await using (var context = await factory.Object.CreateDbContextAsync())
        {
            requestId = await SeedRequestAsync(context,
                new WalletWithdrawalRequestPSBT { PSBT = UnsignedPsbt(1) },
                new WalletWithdrawalRequestPSBT { IsTemplatePSBT = true, PSBT = UnsignedPsbt(2) },
                new WalletWithdrawalRequestPSBT { IsInternalWalletPSBT = true, PSBT = UnsignedPsbt(3) });
        }

        var template = await CreateRepository(factory).GetTemplateByRequestId(requestId);

        template.Should().NotBeNull();
        template!.IsTemplatePSBT.Should().BeTrue();
        template.PSBT.Should().Be(UnsignedPsbt(2));

        (await CreateRepository(factory).GetTemplateByRequestId(requestId + 1000)).Should().BeNull();
    }

    [Fact]
    public async Task GetTemplateByRequestId_TwoTemplates_Throws()
    {
        var factory = SetupDbContextFactory();
        int requestId;
        await using (var context = await factory.Object.CreateDbContextAsync())
        {
            requestId = await SeedRequestAsync(context,
                new WalletWithdrawalRequestPSBT { IsTemplatePSBT = true, PSBT = UnsignedPsbt(1) },
                new WalletWithdrawalRequestPSBT { IsTemplatePSBT = true, PSBT = UnsignedPsbt(2) });
        }

        await CreateRepository(factory).Invoking(r => r.GetTemplateByRequestId(requestId))
            .Should().ThrowAsync<InvalidOperationException>();
    }

    /// <summary>
    /// With two templates the approver may have signed either; the repository must not pick one and
    /// validate against it. Guarding what the unique index already forbids costs nothing and keeps the
    /// failure explicit if the index is ever dropped.
    /// </summary>
    [Fact]
    public async Task AddAsync_Approval_WhenRequestHasTwoTemplates_IsRejected()
    {
        var factory = SetupDbContextFactory();
        var generic = new Mock<IRepository<WalletWithdrawalRequestPSBT>>();
        int requestId;
        await using (var context = await factory.Object.CreateDbContextAsync())
        {
            requestId = await SeedRequestAsync(context,
                new WalletWithdrawalRequestPSBT { IsTemplatePSBT = true, PSBT = UnsignedPsbt(1) },
                new WalletWithdrawalRequestPSBT { IsTemplatePSBT = true, PSBT = UnsignedPsbt(2) });
        }

        var result = await CreateRepository(factory, generic).AddAsync(new WalletWithdrawalRequestPSBT
        {
            WalletWithdrawalRequestId = requestId,
            PSBT = UnsignedPsbt(1),
            SignerId = "someone",
        });

        result.Item1.Should().BeFalse();
        result.Item2.Should().Contain("more than one template");
        generic.Verify(x => x.AddAsync(It.IsAny<WalletWithdrawalRequestPSBT>(), It.IsAny<ApplicationDbContext>()), Times.Never);

        await using var verify = await factory.Object.CreateDbContextAsync();
        (await verify.WalletWithdrawalRequests.SingleAsync(x => x.Id == requestId)).Status
            .Should().Be(WalletWithdrawalRequestStatus.Pending, "a rejected approval must not advance the request");
    }
}
