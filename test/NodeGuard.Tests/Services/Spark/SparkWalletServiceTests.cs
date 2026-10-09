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
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NodeGuard.Data.Models;
using NodeGuard.Data.Repositories.Interfaces;
using NSpark;
using NSpark.Signer;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace NodeGuard.Services.Spark;

public class SparkWalletServiceTests
{
    private static readonly SparkSettings Mainnet = new()
    {
        Enabled = true,
        Network = SparkNetwork.Mainnet,
        Operators = SparkOptions.GetDefaultOperators(SparkNetwork.Mainnet),
        SignerMode = SparkSignerMode.Remote,
        SeedFingerprint = "ed0210c8",
        IdentityPublicKey = SparkSettingsTests.Identity
    };

    private static readonly SparkSettings Wallets = new()
    {
        Enabled = true,
        Network = SparkNetwork.Mainnet,
        Operators = SparkOptions.GetDefaultOperators(SparkNetwork.Mainnet),
        SignerMode = SparkSignerMode.Wallet,
        MaxBalanceSats = 2_000_000
    };

    private readonly ISparkSignerProvider _signers = Substitute.For<ISparkSignerProvider>();
    private readonly IWalletRepository _wallets = Substitute.For<IWalletRepository>();
    private readonly ManualTime _time = new();

    private SparkWalletService Service(SparkSettings settings)
    {
        var services = new ServiceCollection().AddSingleton(_wallets).BuildServiceProvider();
        return new(settings, _signers, services.GetRequiredService<IServiceScopeFactory>(), NullLogger<SparkWalletService>.Instance,
            NullLoggerFactory.Instance, _time);
    }

    [Fact]
    public async Task WhenDisabled_SparkReportsItAndRefusesOperations()
    {
        var service = Service(SparkSettings.Disabled);

        (await service.EnsureReadyAsync(SparkWalletRef.RemoteSigner)).State.Should().Be(SparkWalletState.Disabled);
        (await service.GetWalletsAsync()).Should().BeEmpty();
        await service.Invoking(s => s.CreateInvoiceAsync(SparkWalletRef.RemoteSigner, 1000, "swap", 3600)).Should()
            .ThrowAsync<SparkUnavailableException>().WithMessage("*SPARK_ENABLED*");
        await _signers.DidNotReceiveWithAnyArgs().GetSignerAsync(default, default!, default);
    }

    [Fact]
    public async Task ARefusedSigner_MakesTheWalletUnavailableWithTheReason_AndIsRetriedAfterAMinute()
    {
        _signers.GetSignerAsync(Arg.Any<SparkWalletRef>(), Arg.Any<SparkOptions>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new SparkUnavailableException("the signer's Spark identity is wrong"));
        var service = Service(Mainnet);

        var status = await service.EnsureReadyAsync(SparkWalletRef.RemoteSigner);

        status.State.Should().Be(SparkWalletState.Unavailable);
        status.Reason.Should().Be("the signer's Spark identity is wrong");
        service.GetStatus(SparkWalletRef.RemoteSigner).Should().Be(status);
        await service.Invoking(s => s.GetBalanceAsync(SparkWalletRef.RemoteSigner)).Should()
            .ThrowAsync<SparkUnavailableException>().WithMessage("the signer's Spark identity is wrong");
        await _signers.ReceivedWithAnyArgs(1).GetSignerAsync(default, default!, default);

        _time.Advance(TimeSpan.FromMinutes(1));
        await service.EnsureReadyAsync(SparkWalletRef.RemoteSigner);

        await _signers.ReceivedWithAnyArgs(2).GetSignerAsync(default, default!, default);
    }

    [Fact]
    public async Task EachWallet_HasItsOwnStatus()
    {
        _signers.GetSignerAsync(new SparkWalletRef(1), Arg.Any<SparkOptions>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new SparkUnavailableException("cannot decrypt its seed"));
        _signers.GetSignerAsync(new SparkWalletRef(2), Arg.Any<SparkOptions>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new SparkUnavailableException("the operators are unreachable"));
        var service = Service(Wallets);

        await service.EnsureReadyAsync(new SparkWalletRef(1));

        service.GetStatus(new SparkWalletRef(1)).Reason.Should().Be("cannot decrypt its seed");
        service.GetStatus(new SparkWalletRef(2)).State.Should().Be(SparkWalletState.NotConnected);
        (await service.EnsureReadyAsync(new SparkWalletRef(2))).Reason.Should().Be("the operators are unreachable");
    }

    [Fact]
    public async Task TheSignerIsAskedWithNodeGuardsOperatorsAndSsp()
    {
        SparkOptions? asked = null;
        _signers.GetSignerAsync(Arg.Any<SparkWalletRef>(), Arg.Do<SparkOptions>(o => asked = o), Arg.Any<CancellationToken>())
            .ThrowsAsync(new SparkUnavailableException("stop here"));

        await Service(Mainnet).EnsureReadyAsync(SparkWalletRef.RemoteSigner);

        asked!.SigningOperators.Should().BeEquivalentTo(SparkOptions.GetDefaultOperators(SparkNetwork.Mainnet));
        asked.EffectiveSspIdentityPublicKeyHex.Should().Be(SparkOptions.GetSspIdentityPublicKey(SparkNetwork.Mainnet));
    }

    [Fact]
    public async Task TheWallets_AreTheSparkWalletRows_WithTheirMaxBalance()
    {
        _wallets.GetSparkWallets(false).Returns([
            new Wallet { Id = 1, Name = "transit-a", Kind = WalletKind.Spark, SparkIdentityPublicKey = "02aa" },
            new Wallet { Id = 2, Name = "transit-b", Kind = WalletKind.Spark, SparkIdentityPublicKey = "02bb", SparkMaxBalanceSats = 500_000 }
        ]);

        var wallets = await Service(Wallets).GetWalletsAsync();

        wallets.Should().BeEquivalentTo([
            new SparkWalletEntry(new SparkWalletRef(1), "transit-a", "02aa", 2_000_000),
            new SparkWalletEntry(new SparkWalletRef(2), "transit-b", "02bb", 500_000)
        ]);
    }

    [Fact]
    public async Task ASwapsWallet_IsFoundByItsIdentity_EvenArchived()
    {
        _wallets.GetSparkWallets(true).Returns([
            new Wallet { Id = 3, Name = "old", Kind = WalletKind.Spark, SparkIdentityPublicKey = "02cc", IsArchived = true }
        ]);
        var service = Service(Wallets);

        (await service.FindByIdentityAsync("02CC"))!.Ref.Should().Be(new SparkWalletRef(3));
        (await service.FindByIdentityAsync("02dd")).Should().BeNull();
    }

    [Fact]
    public async Task WithTheRemoteSigner_ItsWalletIsTheOnlyOne()
    {
        var service = Service(Mainnet);

        (await service.GetWalletsAsync()).Should().ContainSingle().Which.Ref.Should().Be(SparkWalletRef.RemoteSigner);
        (await service.FindByIdentityAsync(SparkSettingsTests.Identity))!.Ref.Should().Be(SparkWalletRef.RemoteSigner);
        (await service.GetWalletAsync(new SparkWalletRef(1))).Should().BeNull();
        await _wallets.DidNotReceiveWithAnyArgs().GetSparkWallets(default);
    }

    private sealed class ManualTime : TimeProvider
    {
        private DateTimeOffset _now = DateTimeOffset.UnixEpoch;

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan by) => _now += by;
    }
}
