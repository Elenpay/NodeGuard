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
using NodeGuard.Data.Models;
using NodeGuard.Data.Repositories.Interfaces;
using NodeGuard.TestHelpers;
using Microsoft.Extensions.Logging;
using NBitcoin;
using NBXplorer.DerivationStrategy;
using NBXplorer.Models;

namespace NodeGuard.Services;

public class CoinSelectionServiceTests
{
    private readonly ILogger<BitcoinService> _logger = new Mock<ILogger<BitcoinService>>().Object;
    private readonly InternalWallet _internalWallet = CreateWallet.CreateInternalWallet();

    private static UTXO CreateUtxo(uint index, long satoshis)
    {
        return new UTXO
        {
            Outpoint = new OutPoint(new uint256(index), 0),
            Value = new Money(satoshis)
        };
    }

    private CoinSelectionService CreateCoinSelectionService(List<UTXO> confirmedUtxos, List<FMUTXO>? lockedUtxos = null)
    {
        var fmutxoRepository = new Mock<IFMUTXORepository>();
        var nbXplorerService = new Mock<INBXplorerService>();
        var utxoTagRepository = new Mock<IUTXOTagRepository>();
        var mapper = new Mock<IMapper>();

        nbXplorerService
            .Setup(x => x.GetUTXOsAsync(It.IsAny<DerivationStrategyBase>(), default))
            .ReturnsAsync(new UTXOChanges()
            {
                Confirmed = new UTXOChange()
                {
                    UTXOs = confirmedUtxos
                }
            });
        fmutxoRepository
            .Setup(x => x.GetLockedUTXOs(null, null))
            .ReturnsAsync(lockedUtxos ?? new List<FMUTXO>());
        utxoTagRepository
            .Setup(x => x.GetByKeyValue(It.IsAny<string>(), It.IsAny<string>()))
            .ReturnsAsync(new List<UTXOTag>());

        return new CoinSelectionService(_logger, mapper.Object, fmutxoRepository.Object, nbXplorerService.Object, null, null, utxoTagRepository.Object);
    }

    [Fact]
    public async Task GetAvailableUTXOsAsync_ExcludesDustUTXOs()
    {
        // Arrange
        var derivationStrategy = CreateWallet.SingleSig(_internalWallet).GetDerivationStrategy();
        var coinSelectionService = CreateCoinSelectionService(new List<UTXO>()
        {
            CreateUtxo(1, 100),
            CreateUtxo(2, Constants.MINIMUM_UTXO_VALUE_SATS), // 546, boundary: excluded (inclusive comparison)
            CreateUtxo(3, Constants.MINIMUM_UTXO_VALUE_SATS + 1),
            CreateUtxo(4, 10_000)
        });

        // Act
        var availableUTXOs = await coinSelectionService.GetAvailableUTXOsAsync(derivationStrategy);

        // Assert
        availableUTXOs
            .Select(utxo => ((Money)utxo.Value).Satoshi)
            .Should()
            .BeEquivalentTo(new[] { Constants.MINIMUM_UTXO_VALUE_SATS + 1, 10_000L });
    }

    [Theory]
    [InlineData(CoinSelectionStrategy.SmallestFirst)]
    [InlineData(CoinSelectionStrategy.BiggestFirst)]
    [InlineData(CoinSelectionStrategy.ClosestToTargetFirst)]
    [InlineData(CoinSelectionStrategy.UpToAmount)]
    public async Task GetAvailableUTXOsAsync_WithStrategy_ExcludesDustUTXOs(CoinSelectionStrategy strategy)
    {
        // Arrange
        var derivationStrategy = CreateWallet.SingleSig(_internalWallet).GetDerivationStrategy();
        var dustUtxo = CreateUtxo(1, Constants.MINIMUM_UTXO_VALUE_SATS);
        var availableUtxo = CreateUtxo(2, 10_000);
        var coinSelectionService = CreateCoinSelectionService(new List<UTXO>() { dustUtxo, availableUtxo });

        // Act
        var availableUTXOs = await coinSelectionService.GetAvailableUTXOsAsync(
            derivationStrategy, strategy, 0, 10_000, Constants.MINIMUM_UTXO_VALUE_SATS);

        // Assert
        availableUTXOs.Should().ContainSingle();
        availableUTXOs[0].Outpoint.Should().Be(availableUtxo.Outpoint);
    }

    [Fact]
    public async Task GetAvailableUTXOsAsync_WithCustomBackend_IgnoresLockedAndFrozenServerSide()
    {
        var previousCustomBackend = Constants.NBXPLORER_ENABLE_CUSTOM_BACKEND;
        Constants.NBXPLORER_ENABLE_CUSTOM_BACKEND = true;
        try
        {
            // Arrange
            var derivationStrategy = CreateWallet.SingleSig(_internalWallet).GetDerivationStrategy();
            var dustUtxo = CreateUtxo(1, Constants.MINIMUM_UTXO_VALUE_SATS);
            var lockedUtxo = CreateUtxo(2, 20_000);
            var frozenUtxo = CreateUtxo(3, 30_000);
            var availableUtxo = CreateUtxo(4, 40_000);

            var fmutxoRepository = new Mock<IFMUTXORepository>();
            fmutxoRepository
                .Setup(x => x.GetLockedUTXOs(null, null))
                .ReturnsAsync(new List<FMUTXO>()
                {
                    new()
                    {
                        TxId = lockedUtxo.Outpoint.Hash.ToString(),
                        OutputIndex = lockedUtxo.Outpoint.N,
                        SatsAmount = 20_000
                    }
                });

            var utxoTagRepository = new Mock<IUTXOTagRepository>();
            utxoTagRepository
                .Setup(x => x.GetByKeyValue(Constants.IsFrozenTag, "true"))
                .ReturnsAsync(new List<UTXOTag>() { new() { Outpoint = frozenUtxo.Outpoint.ToString() } });
            utxoTagRepository
                .Setup(x => x.GetByKeyValue(Constants.IsManuallyFrozenTag, It.IsAny<string>()))
                .ReturnsAsync(new List<UTXOTag>());

            var nbXplorerService = new Mock<INBXplorerService>();
            nbXplorerService
                .Setup(x => x.GetUTXOsAsync(It.IsAny<DerivationStrategyBase>(), default))
                .ReturnsAsync(new UTXOChanges()
                {
                    Confirmed = new UTXOChange()
                    {
                        UTXOs = new List<UTXO>() { dustUtxo, lockedUtxo, frozenUtxo, availableUtxo }
                    }
                });
            List<string>? ignoredOutpoints = null;
            nbXplorerService
                .Setup(x => x.GetUTXOsByLimitAsync(It.IsAny<DerivationStrategyBase>(),
                    It.IsAny<CoinSelectionStrategy>(), It.IsAny<int>(), It.IsAny<long>(), It.IsAny<long>(),
                    It.IsAny<List<string>>(), default))
                .Callback<DerivationStrategyBase, CoinSelectionStrategy, int, long, long, List<string>?,
                    CancellationToken>((_, _, _, _, _, ignore, _) => ignoredOutpoints = ignore)
                .ReturnsAsync(new UTXOChanges()
                {
                    Confirmed = new UTXOChange() { UTXOs = new List<UTXO>() { availableUtxo } }
                });

            var mapper = new Mock<IMapper>();
            var coinSelectionService = new CoinSelectionService(_logger, mapper.Object, fmutxoRepository.Object,
                nbXplorerService.Object, null, null, utxoTagRepository.Object);

            // Act
            var availableUTXOs = await coinSelectionService.GetAvailableUTXOsAsync(
                derivationStrategy, CoinSelectionStrategy.SmallestFirst, 0, 40_000, 0);

            // Assert
            availableUTXOs.Should().ContainSingle();
            availableUTXOs[0].Outpoint.Should().Be(availableUtxo.Outpoint);

            // Locked and frozen UTXOs must be ignored server-side so the backend does not count
            // them towards the requested amount and return a short selection
            ignoredOutpoints.Should().NotBeNull();
            ignoredOutpoints.Should().Contain($"{lockedUtxo.Outpoint.Hash}-{lockedUtxo.Outpoint.N}");
            ignoredOutpoints.Should().Contain(frozenUtxo.Outpoint.ToString());
            ignoredOutpoints.Should().NotContain(availableUtxo.Outpoint.ToString());

            // Dust is dropped by the backend on value, via the minimumValue parameter. Listing it
            // here as well would spend one query parameter per dust UTXO to say the same thing,
            // and that is what used to push the request line past 8KB and earn a 414
            ignoredOutpoints.Should().NotContain(dustUtxo.Outpoint.ToString());
        }
        finally
        {
            Constants.NBXPLORER_ENABLE_CUSTOM_BACKEND = previousCustomBackend;
        }
    }

    [Fact]
    public async Task GetAvailableUTXOsAsync_WithCustomBackend_SkipsOutpointsOutsideTheWallet()
    {
        var previousCustomBackend = Constants.NBXPLORER_ENABLE_CUSTOM_BACKEND;
        Constants.NBXPLORER_ENABLE_CUSTOM_BACKEND = true;
        try
        {
            // Arrange
            var derivationStrategy = CreateWallet.SingleSig(_internalWallet).GetDerivationStrategy();
            var availableUtxo = CreateUtxo(1, 40_000);
            var otherWalletLockedUtxo = CreateUtxo(2, 20_000);
            var otherWalletFrozenUtxo = CreateUtxo(3, 30_000);

            // Neither lookup is scoped to a wallet, so both return rows belonging to other wallets
            var fmutxoRepository = new Mock<IFMUTXORepository>();
            fmutxoRepository
                .Setup(x => x.GetLockedUTXOs(null, null))
                .ReturnsAsync(new List<FMUTXO>()
                {
                    new()
                    {
                        TxId = otherWalletLockedUtxo.Outpoint.Hash.ToString(),
                        OutputIndex = otherWalletLockedUtxo.Outpoint.N,
                        SatsAmount = 20_000
                    }
                });

            var utxoTagRepository = new Mock<IUTXOTagRepository>();
            utxoTagRepository
                .Setup(x => x.GetByKeyValue(Constants.IsFrozenTag, "true"))
                .ReturnsAsync(new List<UTXOTag>() { new() { Outpoint = otherWalletFrozenUtxo.Outpoint.ToString() } });
            utxoTagRepository
                .Setup(x => x.GetByKeyValue(Constants.IsManuallyFrozenTag, It.IsAny<string>()))
                .ReturnsAsync(new List<UTXOTag>());

            var nbXplorerService = new Mock<INBXplorerService>();
            nbXplorerService
                .Setup(x => x.GetUTXOsAsync(It.IsAny<DerivationStrategyBase>(), default))
                .ReturnsAsync(new UTXOChanges()
                {
                    Confirmed = new UTXOChange() { UTXOs = new List<UTXO>() { availableUtxo } }
                });
            List<string>? ignoredOutpoints = null;
            nbXplorerService
                .Setup(x => x.GetUTXOsByLimitAsync(It.IsAny<DerivationStrategyBase>(),
                    It.IsAny<CoinSelectionStrategy>(), It.IsAny<int>(), It.IsAny<long>(), It.IsAny<long>(),
                    It.IsAny<List<string>>(), default))
                .Callback<DerivationStrategyBase, CoinSelectionStrategy, int, long, long, List<string>?,
                    CancellationToken>((_, _, _, _, _, ignore, _) => ignoredOutpoints = ignore)
                .ReturnsAsync(new UTXOChanges()
                {
                    Confirmed = new UTXOChange() { UTXOs = new List<UTXO>() { availableUtxo } }
                });

            var mapper = new Mock<IMapper>();
            var coinSelectionService = new CoinSelectionService(_logger, mapper.Object, fmutxoRepository.Object,
                nbXplorerService.Object, null, null, utxoTagRepository.Object);

            // Act
            var availableUTXOs = await coinSelectionService.GetAvailableUTXOsAsync(
                derivationStrategy, CoinSelectionStrategy.SmallestFirst, 0, 40_000, 0);

            // Assert
            availableUTXOs.Should().ContainSingle();

            // The backend can only ever return UTXOs from the queried wallet, so telling it to skip
            // another wallet's outpoints changes nothing and only lengthens the request line
            ignoredOutpoints.Should().BeEmpty();
        }
        finally
        {
            Constants.NBXPLORER_ENABLE_CUSTOM_BACKEND = previousCustomBackend;
        }
    }

    [Fact]
    public async Task GetAvailableUTXOsAsync_WithCustomBackend_DeduplicatesIgnoredOutpoints()
    {
        var previousCustomBackend = Constants.NBXPLORER_ENABLE_CUSTOM_BACKEND;
        Constants.NBXPLORER_ENABLE_CUSTOM_BACKEND = true;
        try
        {
            // Arrange: one UTXO that is both locked and frozen
            var derivationStrategy = CreateWallet.SingleSig(_internalWallet).GetDerivationStrategy();
            var lockedAndFrozenUtxo = CreateUtxo(1, 20_000);
            var availableUtxo = CreateUtxo(2, 40_000);

            var fmutxoRepository = new Mock<IFMUTXORepository>();
            fmutxoRepository
                .Setup(x => x.GetLockedUTXOs(null, null))
                .ReturnsAsync(new List<FMUTXO>()
                {
                    new()
                    {
                        TxId = lockedAndFrozenUtxo.Outpoint.Hash.ToString(),
                        OutputIndex = lockedAndFrozenUtxo.Outpoint.N,
                        SatsAmount = 20_000
                    }
                });

            var utxoTagRepository = new Mock<IUTXOTagRepository>();
            utxoTagRepository
                .Setup(x => x.GetByKeyValue(Constants.IsFrozenTag, "true"))
                .ReturnsAsync(new List<UTXOTag>() { new() { Outpoint = lockedAndFrozenUtxo.Outpoint.ToString() } });
            utxoTagRepository
                .Setup(x => x.GetByKeyValue(Constants.IsManuallyFrozenTag, It.IsAny<string>()))
                .ReturnsAsync(new List<UTXOTag>());

            var nbXplorerService = new Mock<INBXplorerService>();
            nbXplorerService
                .Setup(x => x.GetUTXOsAsync(It.IsAny<DerivationStrategyBase>(), default))
                .ReturnsAsync(new UTXOChanges()
                {
                    Confirmed = new UTXOChange()
                    {
                        UTXOs = new List<UTXO>() { lockedAndFrozenUtxo, availableUtxo }
                    }
                });
            List<string>? ignoredOutpoints = null;
            nbXplorerService
                .Setup(x => x.GetUTXOsByLimitAsync(It.IsAny<DerivationStrategyBase>(),
                    It.IsAny<CoinSelectionStrategy>(), It.IsAny<int>(), It.IsAny<long>(), It.IsAny<long>(),
                    It.IsAny<List<string>>(), default))
                .Callback<DerivationStrategyBase, CoinSelectionStrategy, int, long, long, List<string>?,
                    CancellationToken>((_, _, _, _, _, ignore, _) => ignoredOutpoints = ignore)
                .ReturnsAsync(new UTXOChanges()
                {
                    Confirmed = new UTXOChange() { UTXOs = new List<UTXO>() { availableUtxo } }
                });

            var mapper = new Mock<IMapper>();
            var coinSelectionService = new CoinSelectionService(_logger, mapper.Object, fmutxoRepository.Object,
                nbXplorerService.Object, null, null, utxoTagRepository.Object);

            // Act
            await coinSelectionService.GetAvailableUTXOsAsync(
                derivationStrategy, CoinSelectionStrategy.SmallestFirst, 0, 40_000, 0);

            // Assert: sending it twice would say nothing extra and cost another query parameter
            ignoredOutpoints.Should().ContainSingle()
                .Which.Should().Be(lockedAndFrozenUtxo.Outpoint.ToString());
        }
        finally
        {
            Constants.NBXPLORER_ENABLE_CUSTOM_BACKEND = previousCustomBackend;
        }
    }

    [Fact]
    public async Task GetAvailableUTXOsAsync_WithCustomBackendFailing_FallsBackToPlainListingAndLocalFilter()
    {
        var previousCustomBackend = Constants.NBXPLORER_ENABLE_CUSTOM_BACKEND;
        Constants.NBXPLORER_ENABLE_CUSTOM_BACKEND = true;
        try
        {
            // Arrange
            var derivationStrategy = CreateWallet.SingleSig(_internalWallet).GetDerivationStrategy();
            var dustUtxo = CreateUtxo(1, Constants.MINIMUM_UTXO_VALUE_SATS);
            var lockedUtxo = CreateUtxo(2, 20_000);
            var availableUtxo = CreateUtxo(3, 30_000);

            var fmutxoRepository = new Mock<IFMUTXORepository>();
            fmutxoRepository
                .Setup(x => x.GetLockedUTXOs(null, null))
                .ReturnsAsync(new List<FMUTXO>()
                {
                    new()
                    {
                        TxId = lockedUtxo.Outpoint.Hash.ToString(),
                        OutputIndex = lockedUtxo.Outpoint.N,
                        SatsAmount = 20_000
                    }
                });

            var utxoTagRepository = new Mock<IUTXOTagRepository>();
            utxoTagRepository
                .Setup(x => x.GetByKeyValue(It.IsAny<string>(), It.IsAny<string>()))
                .ReturnsAsync(new List<UTXOTag>());

            var nbXplorerService = new Mock<INBXplorerService>();
            nbXplorerService
                .Setup(x => x.GetUTXOsAsync(It.IsAny<DerivationStrategyBase>(), default))
                .ReturnsAsync(new UTXOChanges()
                {
                    Confirmed = new UTXOChange()
                    {
                        UTXOs = new List<UTXO>() { dustUtxo, lockedUtxo, availableUtxo }
                    }
                });
            nbXplorerService
                .Setup(x => x.GetUTXOsByLimitAsync(It.IsAny<DerivationStrategyBase>(),
                    It.IsAny<CoinSelectionStrategy>(), It.IsAny<int>(), It.IsAny<long>(), It.IsAny<long>(),
                    It.IsAny<List<string>>(), default))
                .ThrowsAsync(new HttpRequestException("custom backend unavailable"));

            var mapper = new Mock<IMapper>();
            var coinSelectionService = new CoinSelectionService(_logger, mapper.Object, fmutxoRepository.Object,
                nbXplorerService.Object, null, null, utxoTagRepository.Object);

            // Act
            var availableUTXOs = await coinSelectionService.GetAvailableUTXOsAsync(
                derivationStrategy, CoinSelectionStrategy.SmallestFirst, 0, 30_000, 0);

            // Assert: the plain listing is used and locked/dust UTXOs are still filtered locally
            availableUTXOs.Should().ContainSingle();
            availableUTXOs[0].Outpoint.Should().Be(availableUtxo.Outpoint);
        }
        finally
        {
            Constants.NBXPLORER_ENABLE_CUSTOM_BACKEND = previousCustomBackend;
        }
    }

    [Fact]
    public async Task GetAvailableUTXOsAsync_ExcludesDustAndLockedUTXOs()
    {
        // Arrange
        var derivationStrategy = CreateWallet.SingleSig(_internalWallet).GetDerivationStrategy();
        var dustUtxo = CreateUtxo(1, 546);
        var lockedUtxo = CreateUtxo(2, 20_000);
        var availableUtxo = CreateUtxo(3, 30_000);
        var lockedFmutxo = new FMUTXO()
        {
            TxId = lockedUtxo.Outpoint.Hash.ToString(),
            OutputIndex = lockedUtxo.Outpoint.N,
            SatsAmount = 20_000
        };
        var coinSelectionService = CreateCoinSelectionService(
            new List<UTXO>() { dustUtxo, lockedUtxo, availableUtxo },
            new List<FMUTXO>() { lockedFmutxo });

        // Act
        var availableUTXOs = await coinSelectionService.GetAvailableUTXOsAsync(derivationStrategy);

        // Assert
        availableUTXOs.Should().ContainSingle();
        availableUTXOs[0].Outpoint.Should().Be(availableUtxo.Outpoint);
    }

    /// <summary>A UTXO complete enough for LightningHelper.SelectCoins to turn it into a coin.</summary>
    private static UTXO CreateSpendableUtxo(Wallet wallet, uint index, long satoshis, int confirmations = 1)
    {
        var keyPath = KeyPath.Parse($"0/{index}");

        return new UTXO
        {
            Outpoint = new OutPoint(new uint256(index), 0),
            Value = new Money(satoshis),
            Confirmations = confirmations,
            KeyPath = keyPath,
            ScriptPubKey = (wallet.GetDerivationStrategy() as StandardDerivationStrategyBase)!
                .GetDerivation(keyPath).ScriptPubKey
        };
    }

    private static WalletWithdrawalRequest CreateRequest(Wallet wallet, decimal amountBtc)
    {
        return new WalletWithdrawalRequest
        {
            Id = 1,
            Wallet = wallet,
            WalletWithdrawalRequestDestinations = new List<WalletWithdrawalRequestDestination>
            {
                new() { Address = "bcrt1q590shaxaf5u08ml8jwlzghz99dup3z9592vxal", Amount = amountBtc }
            }
        };
    }

    [Fact]
    public async Task GetTxInputCoins_WithStrategy_SelectsInTheOrderNBXplorerReturns()
    {
        var previousCustomBackend = Constants.NBXPLORER_ENABLE_CUSTOM_BACKEND;
        Constants.NBXPLORER_ENABLE_CUSTOM_BACKEND = true;
        try
        {
            // Arrange
            var wallet = CreateWallet.SingleSig(_internalWallet);
            var derivationStrategy = wallet.GetDerivationStrategy();

            // SelectUTXOsByOldest pops in ascending confirmations, so it would take the 100k UTXO on its
            // own. Following NBXplorer's order instead takes the two small ones.
            var small = CreateSpendableUtxo(wallet, 1, 6_000, confirmations: 2);
            var smaller = CreateSpendableUtxo(wallet, 2, 5_000, confirmations: 3);
            var big = CreateSpendableUtxo(wallet, 3, 100_000, confirmations: 1);

            var fmutxoRepository = new Mock<IFMUTXORepository>();
            fmutxoRepository.Setup(x => x.GetLockedUTXOs(null, null)).ReturnsAsync(new List<FMUTXO>());

            var utxoTagRepository = new Mock<IUTXOTagRepository>();
            utxoTagRepository.Setup(x => x.GetByKeyValue(It.IsAny<string>(), It.IsAny<string>()))
                .ReturnsAsync(new List<UTXOTag>());

            var nbXplorerService = new Mock<INBXplorerService>();
            nbXplorerService
                .Setup(x => x.GetUTXOsAsync(It.IsAny<DerivationStrategyBase>(), default))
                .ReturnsAsync(new UTXOChanges
                {
                    Confirmed = new UTXOChange { UTXOs = new List<UTXO> { small, smaller, big } }
                });
            nbXplorerService
                .Setup(x => x.GetUTXOsByLimitAsync(It.IsAny<DerivationStrategyBase>(),
                    It.IsAny<CoinSelectionStrategy>(), It.IsAny<int>(), It.IsAny<long>(), It.IsAny<long>(),
                    It.IsAny<List<string>>(), default))
                .ReturnsAsync(new UTXOChanges
                {
                    Confirmed = new UTXOChange { UTXOs = new List<UTXO> { small, smaller, big } }
                });

            var coinSelectionService = new CoinSelectionService(_logger, new Mock<IMapper>().Object,
                fmutxoRepository.Object, nbXplorerService.Object, null, null, utxoTagRepository.Object);

            var request = CreateRequest(wallet, 0.0001m); // 10_000 sats

            // Act
            var (coins, selectedUTXOs) = await coinSelectionService.GetTxInputCoins(
                new List<UTXO>(), request, derivationStrategy, CoinSelectionStrategy.SmallestFirst);

            // Assert
            nbXplorerService.Verify(x => x.GetUTXOsByLimitAsync(derivationStrategy,
                CoinSelectionStrategy.SmallestFirst, 0, 10_000, 10_000, It.IsAny<List<string>>(), default),
                Times.Once);
            selectedUTXOs.Select(x => x.Outpoint).Should()
                .Equal(new[] { small.Outpoint, smaller.Outpoint }, "the order NBXplorer returned is kept");
            coins.Should().HaveCount(2);
        }
        finally
        {
            Constants.NBXPLORER_ENABLE_CUSTOM_BACKEND = previousCustomBackend;
        }
    }

    [Fact]
    public async Task GetTxInputCoins_WithoutStrategy_KeepsTheOldestFirstSelection()
    {
        // Arrange
        var wallet = CreateWallet.SingleSig(_internalWallet);
        var derivationStrategy = wallet.GetDerivationStrategy();
        var utxo = CreateSpendableUtxo(wallet, 1, 100_000);

        var nbXplorerService = new Mock<INBXplorerService>();
        var coinSelectionService = new CoinSelectionService(_logger, new Mock<IMapper>().Object,
            new Mock<IFMUTXORepository>().Object, nbXplorerService.Object, null, null,
            new Mock<IUTXOTagRepository>().Object);

        var request = CreateRequest(wallet, 0.0001m); // 10_000 sats

        // Act
        var (coins, selectedUTXOs) = await coinSelectionService.GetTxInputCoins(
            new List<UTXO> { utxo }, request, derivationStrategy);

        // Assert
        selectedUTXOs.Should().ContainSingle().Which.Outpoint.Should().Be(utxo.Outpoint);
        coins.Should().ContainSingle();
        nbXplorerService.Verify(x => x.GetUTXOsByLimitAsync(It.IsAny<DerivationStrategyBase>(),
            It.IsAny<CoinSelectionStrategy>(), It.IsAny<int>(), It.IsAny<long>(), It.IsAny<long>(),
            It.IsAny<List<string>>(), default), Times.Never);
    }

    [Fact]
    public async Task GetTxInputCoins_WithStrategy_ReturnsNothingWhenTheSetIsShort()
    {
        var previousCustomBackend = Constants.NBXPLORER_ENABLE_CUSTOM_BACKEND;
        Constants.NBXPLORER_ENABLE_CUSTOM_BACKEND = true;
        try
        {
            // Arrange
            var wallet = CreateWallet.SingleSig(_internalWallet);
            var derivationStrategy = wallet.GetDerivationStrategy();
            var utxo = CreateSpendableUtxo(wallet, 1, 1_000);

            var fmutxoRepository = new Mock<IFMUTXORepository>();
            fmutxoRepository.Setup(x => x.GetLockedUTXOs(null, null)).ReturnsAsync(new List<FMUTXO>());

            var utxoTagRepository = new Mock<IUTXOTagRepository>();
            utxoTagRepository.Setup(x => x.GetByKeyValue(It.IsAny<string>(), It.IsAny<string>()))
                .ReturnsAsync(new List<UTXOTag>());

            var nbXplorerService = new Mock<INBXplorerService>();
            nbXplorerService
                .Setup(x => x.GetUTXOsAsync(It.IsAny<DerivationStrategyBase>(), default))
                .ReturnsAsync(new UTXOChanges { Confirmed = new UTXOChange { UTXOs = new List<UTXO> { utxo } } });
            nbXplorerService
                .Setup(x => x.GetUTXOsByLimitAsync(It.IsAny<DerivationStrategyBase>(),
                    It.IsAny<CoinSelectionStrategy>(), It.IsAny<int>(), It.IsAny<long>(), It.IsAny<long>(),
                    It.IsAny<List<string>>(), default))
                .ReturnsAsync(new UTXOChanges { Confirmed = new UTXOChange { UTXOs = new List<UTXO> { utxo } } });

            var coinSelectionService = new CoinSelectionService(_logger, new Mock<IMapper>().Object,
                fmutxoRepository.Object, nbXplorerService.Object, null, null, utxoTagRepository.Object);

            var request = CreateRequest(wallet, 0.0001m); // 10_000 sats, more than the wallet holds

            // Act
            var (coins, selectedUTXOs) = await coinSelectionService.GetTxInputCoins(
                new List<UTXO>(), request, derivationStrategy, CoinSelectionStrategy.BiggestFirst);

            // Assert: GenerateTemplatePSBT turns an empty selection into NoUTXOsAvailableException
            selectedUTXOs.Should().BeEmpty();
            coins.Should().BeEmpty();
        }
        finally
        {
            Constants.NBXPLORER_ENABLE_CUSTOM_BACKEND = previousCustomBackend;
        }
    }
}
