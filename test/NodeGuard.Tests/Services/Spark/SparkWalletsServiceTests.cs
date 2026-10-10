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
using Microsoft.Extensions.Logging.Abstractions;
using NBitcoin;
using NBXplorer.DerivationStrategy;
using NBXplorer.Models;
using NodeGuard.Data.Models;
using NodeGuard.Data.Repositories.Interfaces;
using NodeGuard.TestHelpers;
using NSpark.Models;
using NSpark.Signer;
using NSubstitute;

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

public class SparkWalletsServiceTests
{
    private const string Identity = "02aa000000000000000000000000000000000000000000000000000000000000aa";

    private static readonly SparkSettings Settings = new() { Enabled = true, SignerMode = SparkSignerMode.Wallet, MaxBalanceSats = 2_000_000 };

    private readonly ISparkWalletService _spark = Substitute.For<ISparkWalletService>();
    private readonly ISparkSeedProtector _protector = new SparkSeedProtector(new EphemeralDataProtectionProvider());
    private readonly IWalletRepository _wallets = Substitute.For<IWalletRepository>();
    private readonly INodeRepository _nodes = Substitute.For<INodeRepository>();
    private readonly ISwapOutRepository _swapOuts = Substitute.For<ISwapOutRepository>();
    private readonly INBXplorerService _nbXplorer = Substitute.For<INBXplorerService>();
    private readonly IAuditService _audit = Substitute.For<IAuditService>();
    private readonly Wallet _destination;

    public SparkWalletsServiceTests()
    {
        _wallets.AddAsync(Arg.Any<Wallet>()).Returns((true, null));
        _wallets.Update(Arg.Any<Wallet>()).Returns((true, null));
        _wallets.GetById(5).Returns(_ => SparkWallet());
        _nodes.GetAll().Returns(new List<Node>());
        _swapOuts.GetAllPending().Returns(new List<SwapOut>());
        Holds(0);

        _destination = CreateWallet.SingleSig(CreateWallet.CreateInternalWallet());
        _destination.Id = 3;
        _destination.IsFinalised = true;
        _wallets.GetById(3).Returns(_destination);
        _nbXplorer.GetUnusedAsync(Arg.Any<DerivationStrategyBase>(), DerivationFeature.Deposit, 0, true, Arg.Any<CancellationToken>())
            .Returns(new KeyPathInformation { Address = new NBitcoin.Key().PubKey.GetAddress(ScriptPubKeyType.Segwit, Network.RegTest) });
    }

    private SparkWalletsService Service(SparkSettings? settings = null) => new(settings ?? Settings, _spark, _protector, _wallets, _nodes,
        _swapOuts, _nbXplorer, _audit, NullLogger<SparkWalletsService>.Instance);

    private static Wallet SparkWallet() => new()
    {
        Id = 5, Name = "transit", Kind = WalletKind.Spark, IsFinalised = true, SparkIdentityPublicKey = Identity, SparkAccount = 0, Keys = []
    };

    private void Holds(long owned, long incoming = 0) =>
        _spark.GetBalanceAsync(new SparkWalletRef(5), Arg.Any<CancellationToken>())
            .Returns(new WalletBalance(new SatsBalance(owned, owned, incoming), [], []));

    // ── Creation ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Create_StoresOnlyTheEncryptedMnemonic_AndShowsTheWordsOnce()
    {
        Wallet? saved = null;
        _wallets.AddAsync(Arg.Do<Wallet>(w => saved = w)).Returns((true, null));

        var (wallet, mnemonic) = await Service().CreateAsync(" transit ", account: 2, maxBalanceSats: 1_500_000);

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
        var act = () => Service(new SparkSettings { Enabled = true, SignerMode = SparkSignerMode.Remote }).CreateAsync("transit");

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*SPARK_SIGNER=wallet*");
        await _wallets.DidNotReceiveWithAnyArgs().AddAsync(default!);
    }

    [Theory]
    [InlineData("", 0, null)]
    [InlineData("transit", -1, null)]
    [InlineData("transit", 0, 0L)]
    public async Task Create_WithInvalidSettings_IsRefused(string name, int account, long? maxBalanceSats)
    {
        var act = () => Service().CreateAsync(name, account, maxBalanceSats);

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

        var wallet = (await Service().ListAsync()).Should().ContainSingle().Subject;

        wallet.NodeIds.Should().Equal(1, 3);
        wallet.MaxBalanceSats.Should().BeNull();
        wallet.EffectiveMaxBalanceSats.Should().Be(2_000_000);
    }

    // ── Withdraw all ─────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task WithdrawAll_ExitsEverythingToANewAddressOfTheOnChainWallet()
    {
        _spark.WithdrawAllAsync(new SparkWalletRef(5), Arg.Any<string>(), Settings.MaxExitFeeSats, Arg.Any<CancellationToken>())
            .Returns(new WithdrawAllResult("exit-tx", 300_000, 297_000, 0, 0, 0, 0));

        var exit = await Service().WithdrawAllAsync(5, 3);

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

        var act = () => Service().WithdrawAllAsync(5, 3);

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
        _spark.WithdrawAllAsync(new SparkWalletRef(5), Arg.Any<string>(), Arg.Any<long>(), Arg.Any<CancellationToken>())
            .Returns(new WithdrawAllResult("exit-tx", 300_000, 297_000, 0, 0, 0, 0));

        (await Service().WithdrawAllAsync(5, 3)).Txid.Should().Be("exit-tx");
    }

    [Fact]
    public async Task WithdrawAll_ToAnotherSparkWallet_IsRefused()
    {
        _wallets.GetById(6).Returns(new Wallet { Id = 6, Name = "other transit", Kind = WalletKind.Spark, IsFinalised = true, Keys = [] });

        var act = () => Service().WithdrawAllAsync(5, 6);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*not an available on-chain wallet*");
    }

    // ── Archive ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Archive_AnEmptyWalletNoNodeUses_ArchivesItAndDropsItsConnection()
    {
        await Service().ArchiveAsync(5);

        _wallets.Received(1).Update(Arg.Is<Wallet>(w => w.Id == 5 && w.IsArchived));
        _spark.Received(1).Forget(new SparkWalletRef(5));
    }

    [Fact]
    public async Task Archive_AWalletANodeUses_IsRefused()
    {
        _nodes.GetAll().Returns([new Node { Id = 1, Name = "alice", SparkWalletId = 5 }]);

        var act = () => Service().ArchiveAsync(5);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*Spark wallet of alice*");
        _wallets.DidNotReceiveWithAnyArgs().Update(default!);
    }

    [Fact]
    public async Task Archive_AWalletWithSats_IsRefused()
    {
        Holds(0, incoming: 1_000);

        var act = () => Service().ArchiveAsync(5);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*still holds 1000 sats*");
        _wallets.DidNotReceiveWithAnyArgs().Update(default!);
    }

    [Fact]
    public async Task AnOnChainWallet_IsNotManagedHere()
    {
        await Service().Invoking(s => s.ArchiveAsync(3)).Should().ThrowAsync<ArgumentException>().WithMessage("*not a Spark wallet*");
    }
}
