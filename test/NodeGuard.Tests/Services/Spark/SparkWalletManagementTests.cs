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
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NBitcoin;
using NBXplorer.DerivationStrategy;
using NBXplorer.Models;
using NodeGuard.Data.Models;
using NodeGuard.Data.Repositories.Interfaces;
using NodeGuard.TestHelpers;
using NSpark.Models;
using NSpark.Services;
using NSpark.Signer;
using NSubstitute;
using NSubstitute.Extensions;

namespace NodeGuard.Services.Spark;

public class SparkSeedProtectorTests
{
    [Fact]
    public void AMnemonic_RoundTrips_AndIsNotStoredInClear()
    {
        var protector = new SparkSeedProtector(new EphemeralDataProtectionProvider());

        var ciphertext = protector.Protect(SparkSignerProviderTests.DevMnemonic);

        ciphertext.Should().NotContain("middle");
        protector.Unprotect(ciphertext).Should().Be(SparkSignerProviderTests.DevMnemonic);
    }

    [Fact]
    public void AnotherKeyRing_CannotDecryptIt()
    {
        var ciphertext = new SparkSeedProtector(new EphemeralDataProtectionProvider()).Protect(SparkSignerProviderTests.DevMnemonic);

        var act = () => new SparkSeedProtector(new EphemeralDataProtectionProvider()).Unprotect(ciphertext);

        act.Should().Throw<SparkUnavailableException>().WithMessage("cannot decrypt its seed*");
    }
}

/// <summary>
/// SparkWalletService's management of the Spark wallet rows. The Spark operations it calls on itself (connect,
/// balance, claim, withdraw all) are stubbed on the same instance, a partial substitute.
/// </summary>
public class SparkWalletManagementTests
{
    private const string Identity = "02aa000000000000000000000000000000000000000000000000000000000000aa";

    private static readonly SparkSettings Settings = new() { Enabled = true, SignerMode = SparkSignerMode.Wallet, MaxBalanceSats = 2_000_000 };

    private readonly ISparkSeedProtector _protector = new SparkSeedProtector(new EphemeralDataProtectionProvider());
    private readonly IWalletRepository _wallets = Substitute.For<IWalletRepository>();
    private readonly INodeRepository _nodes = Substitute.For<INodeRepository>();
    private readonly ISwapOutRepository _swapOuts = Substitute.For<ISwapOutRepository>();
    private readonly INBXplorerService _nbXplorer = Substitute.For<INBXplorerService>();
    private readonly IAuditService _audit = Substitute.For<IAuditService>();
    private readonly SparkWalletService _spark;
    private readonly Wallet _destination;

    public SparkWalletManagementTests()
    {
        _spark = Partial(Settings);
        _spark.Configure().EnsureReadyAsync(Arg.Any<SparkWalletRef>(), Arg.Any<CancellationToken>())
            .Returns(new SparkWalletStatus(SparkWalletState.Ready, Identity));
        _spark.Configure().ClaimPendingAsync(Arg.Any<SparkWalletRef>(), Arg.Any<CancellationToken>())
            .Returns(new PendingTransferClaim([], []));

        _wallets.AddAsync(Arg.Any<Wallet>()).Returns((true, null));
        _wallets.Update(Arg.Any<Wallet>()).Returns((true, null));
        _wallets.GetById(5).Returns(_ => SparkWallet());
        _nodes.GetAll().Returns(new List<Node>());
        _swapOuts.GetAllPending().Returns(new List<SwapOut>());
        _swapOuts.GetSparkSwapsByIdentityAsync(Arg.Any<string>()).Returns(new List<SwapOut>());
        Holds(0);

        _destination = CreateWallet.SingleSig(CreateWallet.CreateInternalWallet());
        _destination.Id = 3;
        _destination.IsFinalised = true;
        _wallets.GetById(3).Returns(_destination);
        _nbXplorer.GetUnusedAsync(Arg.Any<DerivationStrategyBase>(), DerivationFeature.Deposit, 0, true, Arg.Any<CancellationToken>())
            .Returns(new KeyPathInformation { Address = new NBitcoin.Key().PubKey.GetAddress(ScriptPubKeyType.Segwit, Network.RegTest) });
    }

    private SparkWalletService Partial(SparkSettings settings)
    {
        var scopes = new ServiceCollection().AddSingleton(_wallets).AddSingleton(_nodes).AddSingleton(_swapOuts)
            .AddSingleton(_nbXplorer).AddSingleton(_audit).BuildServiceProvider().GetRequiredService<IServiceScopeFactory>();
        return Substitute.ForPartsOf<SparkWalletService>(settings, Substitute.For<ISparkSignerProvider>(), scopes, _protector,
            NullLogger<SparkWalletService>.Instance, NullLoggerFactory.Instance, TimeProvider.System);
    }

    private SparkWalletService Service(SparkSettings? settings = null) => settings is null ? _spark : Partial(settings);

    private static Wallet SparkWallet() => new()
    {
        Id = 5, Name = "transit", Kind = WalletKind.Spark, IsFinalised = true, SparkIdentityPublicKey = Identity, SparkAccount = 0, Keys = []
    };

    private void Holds(long owned, long incoming = 0) =>
        _spark.Configure().GetBalanceAsync(new SparkWalletRef(5), Arg.Any<CancellationToken>())
            .Returns(new WalletBalance(new SatsBalance(owned, owned, incoming), [], []));

    // ── Creation ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Create_StoresOnlyTheEncryptedMnemonic_AndShowsTheWordsOnce()
    {
        Wallet? saved = null;
        _wallets.AddAsync(Arg.Do<Wallet>(w => saved = w)).Returns((true, null));

        var (wallet, mnemonic) = await Service().CreateWalletAsync(" transit ", account: 2, maxBalanceSats: 1_500_000);

        new Mnemonic(mnemonic).Words.Should().HaveCount(24);
        saved.Should().BeSameAs(wallet);
        wallet.Should().BeEquivalentTo(new
        {
            Name = "transit", Kind = WalletKind.Spark, IsFinalised = true, IsHotWallet = true, MofN = 1, IsBIP39Imported = false,
            BIP39Seedphrase = (string?)null, SparkAccount = 2, SparkMaxBalanceSats = 1_500_000L
        });
        wallet.SparkEncryptedMnemonic.Should().NotContain(mnemonic.Split(' ')[0]);
        _protector.Unprotect(wallet.SparkEncryptedMnemonic!).Should().Be(mnemonic);
        wallet.SparkIdentityPublicKey.Should().Be(
            Convert.ToHexString(await SparkSigner.FromMnemonic(mnemonic, 2).GetIdentityPublicKeyAsync()).ToLowerInvariant());
        await _audit.Received(1).LogAsync(AuditActionType.Create, AuditEventType.Success, AuditObjectType.Wallet, Arg.Any<string?>(),
            Arg.Any<object?>());
        await _spark.Received(1).EnsureReadyAsync(new SparkWalletRef(wallet.Id), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Create_WithTheRemoteSigner_IsRefused()
    {
        var act = () => Service(new SparkSettings { Enabled = true, SignerMode = SparkSignerMode.Remote }).CreateWalletAsync("transit");

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*SPARK_SIGNER=wallet*");
        await _wallets.DidNotReceiveWithAnyArgs().AddAsync(default!);
    }

    [Theory]
    [InlineData("", 0, null)]
    [InlineData("transit", -1, null)]
    [InlineData("transit", 0, 0L)]
    public async Task Create_WithInvalidSettings_IsRefused(string name, int account, long? maxBalanceSats)
    {
        var act = () => Service().CreateWalletAsync(name, account, maxBalanceSats);

        await act.Should().ThrowAsync<ArgumentException>();
    }

    // ── Max balance ──────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task TheMaxBalance_CanBeSetOrResetToTheDefault()
    {
        await Service().SetMaxBalanceAsync(5, 750_000);
        _wallets.Received(1).Update(Arg.Is<Wallet>(w => w.SparkMaxBalanceSats == 750_000));

        await Service().SetMaxBalanceAsync(5, null);
        _wallets.Received(1).Update(Arg.Is<Wallet>(w => w.SparkMaxBalanceSats == null));

        await Service().Invoking(s => s.SetMaxBalanceAsync(5, 0)).Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public async Task TheList_ShowsTheNodesUsingEachWallet_AndTheMaxBalanceThatApplies()
    {
        _wallets.GetSparkWallets(false).Returns([SparkWallet()]);
        _nodes.GetAll().Returns([new Node { Id = 1, SparkWalletId = 5 }, new Node { Id = 2 }, new Node { Id = 3, SparkWalletId = 5 }]);

        var wallet = (await Service().ListWalletsAsync()).Should().ContainSingle().Subject;

        wallet.NodeIds.Should().Equal(1, 3);
        wallet.MaxBalanceSats.Should().BeNull();
        wallet.EffectiveMaxBalanceSats.Should().Be(2_000_000);
    }

    // ── Withdraw all ─────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task WithdrawAll_ExitsEverythingToANewAddressOfTheOnChainWallet()
    {
        _spark.Configure().WithdrawAllAsync(new SparkWalletRef(5), Arg.Any<string>(), Settings.MaxExitFeeSats, Arg.Any<CancellationToken>())
            .Returns(new WithdrawAllResult("exit-tx", 300_000, 297_000, 0, 0, 0, 0));

        var exit = await Service().WithdrawAllToWalletAsync(5, 3);

        exit.Txid.Should().Be("exit-tx");
        Received.InOrder(() =>
        {
            _spark.ClaimPendingAsync(new SparkWalletRef(5), Arg.Any<CancellationToken>());
            _nbXplorer.GetUnusedAsync(Arg.Any<DerivationStrategyBase>(), DerivationFeature.Deposit, 0, true, Arg.Any<CancellationToken>());
            _spark.WithdrawAllAsync(new SparkWalletRef(5), Arg.Any<string>(), Settings.MaxExitFeeSats, Arg.Any<CancellationToken>());
        });
        await _audit.Received(1).LogAsync(AuditActionType.Transfer, AuditEventType.Success, AuditObjectType.Wallet, "5", Arg.Any<object?>());
    }

    [Fact]
    public async Task WithdrawAll_WhileTheWalletHasASwapInFlight_IsRefused()
    {
        _swapOuts.GetAllPending().Returns(new List<SwapOut>
        {
            new() { Id = 8, Provider = SwapProvider.Spark, Status = SwapOutStatus.Pending, SparkIdentity = Identity.ToUpperInvariant() }
        });

        var act = () => Service().WithdrawAllToWalletAsync(5, 3);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*swap 8 is in flight*");
        await _spark.DidNotReceiveWithAnyArgs().WithdrawAllAsync(default, default!, default, default);
    }

    [Fact]
    public async Task WithdrawAll_WhileAnotherWalletHasASwapInFlight_GoesAhead()
    {
        _swapOuts.GetAllPending().Returns(new List<SwapOut>
        {
            new() { Id = 8, Provider = SwapProvider.Spark, Status = SwapOutStatus.Pending, SparkIdentity = "02bb" }
        });
        _spark.Configure().WithdrawAllAsync(new SparkWalletRef(5), Arg.Any<string>(), Arg.Any<long>(), Arg.Any<CancellationToken>())
            .Returns(new WithdrawAllResult("exit-tx", 300_000, 297_000, 0, 0, 0, 0));

        (await Service().WithdrawAllToWalletAsync(5, 3)).Txid.Should().Be("exit-tx");
    }

    [Fact]
    public async Task WithdrawAll_ToAnotherSparkWallet_IsRefused()
    {
        _wallets.GetById(6).Returns(new Wallet { Id = 6, Name = "other transit", Kind = WalletKind.Spark, IsFinalised = true, Keys = [] });

        var act = () => Service().WithdrawAllToWalletAsync(5, 6);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*not an available on-chain wallet*");
    }

    // ── Archive ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Archive_AnEmptyWalletNoNodeUses_ArchivesItAndDropsItsConnection()
    {
        await Service().ArchiveWalletAsync(5);

        _wallets.Received(1).Update(Arg.Is<Wallet>(w => w.Id == 5 && w.IsArchived));
        _spark.Received(1).Forget(new SparkWalletRef(5));
    }

    [Fact]
    public async Task Archive_AWalletANodeUses_IsRefused()
    {
        _nodes.GetAll().Returns([new Node { Id = 1, Name = "alice", SparkWalletId = 5 }]);

        var act = () => Service().ArchiveWalletAsync(5);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*Spark wallet of alice*");
        _wallets.DidNotReceiveWithAnyArgs().Update(default!);
    }

    [Fact]
    public async Task Archive_AWalletWithSats_IsRefused()
    {
        Holds(0, incoming: 1_000);

        var act = () => Service().ArchiveWalletAsync(5);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*still holds 1000 sats*");
        _wallets.DidNotReceiveWithAnyArgs().Update(default!);
    }

    // ── Transactions ─────────────────────────────────────────────────────────────────────

    private static SparkTransfer Transfer(string id, string sender, string type, int minutes, params string[] leafIds) =>
        new(id, sender, sender == Identity ? "03ss" : Identity, 100_000, "Completed", DateTimeOffset.UnixEpoch.AddMinutes(minutes), type)
        {
            Leaves = leafIds.Select(l => new SparkLeaf(l, "tree", 50_000, "AVAILABLE")).ToList()
        };

    [Fact]
    public async Task TheTransactions_AreNewestFirst_WithTheirDirectionTypeAndSwap()
    {
        _spark.Configure().GetTransfersAsync(new SparkWalletRef(5), TransferDirection.Both, DateTimeOffset.UnixEpoch, Arg.Any<CancellationToken>())
            .Returns(new List<SparkTransfer>
            {
                Transfer("receive-1", "03ss", "PreimageSwap", minutes: 1, "l1", "l2"),
                Transfer("exit-1", Identity, "CooperativeExit", minutes: 5, "l2"),
                Transfer("stray", "03ss", "Transfer", minutes: 3, "x1"),
                Transfer("odd", "03ss", "UtxoSwap", minutes: 2)
            });
        _swapOuts.GetSparkSwapsByIdentityAsync(Identity).Returns(new List<SwapOut>
        {
            new() { Id = 41, Provider = SwapProvider.Spark, SparkTransferId = "receive-1", SparkLeafIds = "l1,l2" }
        });

        var transfers = await Service().ListTransfersAsync(5);

        transfers.Select(t => t.Id).Should().Equal("exit-1", "stray", "odd", "receive-1");
        transfers.Should().BeEquivalentTo(new[]
        {
            new { Id = "exit-1", IsSent = true, Type = "Exit to on-chain", SwapIds = new[] { 41 }, Date = DateTimeOffset.UnixEpoch.AddMinutes(5) },
            new { Id = "stray", IsSent = false, Type = "Spark transfer", SwapIds = Array.Empty<int>(), Date = DateTimeOffset.UnixEpoch.AddMinutes(3) },
            new { Id = "odd", IsSent = false, Type = "UtxoSwap", SwapIds = Array.Empty<int>(), Date = DateTimeOffset.UnixEpoch.AddMinutes(2) },
            new { Id = "receive-1", IsSent = false, Type = "Lightning payment in", SwapIds = new[] { 41 }, Date = DateTimeOffset.UnixEpoch.AddMinutes(1) }
        }, o => o.WithStrictOrdering());
    }

    [Fact]
    public async Task TheTransactions_ABatchsExit_ListsEverySwapItPaid()
    {
        _spark.Configure().GetTransfersAsync(new SparkWalletRef(5), TransferDirection.Both, DateTimeOffset.UnixEpoch, Arg.Any<CancellationToken>())
            .Returns(new List<SparkTransfer> { Transfer("exit-1", Identity, "CooperativeExit", minutes: 5, "l1", "l3") });
        _swapOuts.GetSparkSwapsByIdentityAsync(Identity).Returns(new List<SwapOut>
        {
            new() { Id = 41, Provider = SwapProvider.Spark, SparkTransferId = "receive-1", SparkLeafIds = "l1,l2" },
            new() { Id = 42, Provider = SwapProvider.Spark, SparkTransferId = "receive-2", SparkLeafIds = "l3" },
            new() { Id = 43, Provider = SwapProvider.Spark, SparkTransferId = "receive-3", SparkLeafIds = "l4" }
        });

        var transfers = await Service().ListTransfersAsync(5);

        transfers.Should().ContainSingle().Which.SwapIds.Should().Equal(41, 42);
    }

    [Fact]
    public async Task TheTransactions_KeepOnlyTheLatest()
    {
        _spark.Configure().GetTransfersAsync(new SparkWalletRef(5), TransferDirection.Both, DateTimeOffset.UnixEpoch, Arg.Any<CancellationToken>())
            .Returns(Enumerable.Range(1, 5).Select(i => Transfer($"t{i}", "03ss", "Transfer", minutes: i)).ToList());

        var transfers = await Service().ListTransfersAsync(5, limit: 2);

        transfers.Select(t => t.Id).Should().Equal("t5", "t4");
    }

    [Fact]
    public async Task TheTransactions_OfAnOnChainWallet_AreRefused()
    {
        await Service().Invoking(s => s.ListTransfersAsync(3)).Should().ThrowAsync<ArgumentException>().WithMessage("*not a Spark wallet*");
    }

    [Fact]
    public async Task AnOnChainWallet_IsNotManagedHere()
    {
        await Service().Invoking(s => s.ArchiveWalletAsync(3)).Should().ThrowAsync<ArgumentException>().WithMessage("*not a Spark wallet*");
    }
}
