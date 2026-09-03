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

    [Fact]
    public async Task GetTxInputCoins_WithPreserveOrder_TakesTheHeadOfTheListInsteadOfSortingByConfirmations()
    {
        // Arrange
        var wallet = CreateWallet.SingleSig(_internalWallet);
        var derivationStrategy = (wallet.GetDerivationStrategy() as StandardDerivationStrategyBase)!;
        var scriptPubKey = derivationStrategy.GetDerivation(KeyPath.Parse("0/0")).ScriptPubKey;

        // The head of the list is what NBXplorer picked as closest to the amount. It also has the most
        // confirmations, so the two selectors disagree: SelectUTXOsByOldest takes the
        // fewest-confirmation UTXO first despite its name, which is the UTXO at the tail here
        var closest = new UTXO
        {
            Outpoint = new OutPoint(new uint256(1), 0),
            Value = new Money(60_000L),
            ScriptPubKey = scriptPubKey,
            KeyPath = KeyPath.Parse("0/0"),
            Confirmations = 500
        };
        var fewestConfirmations = new UTXO
        {
            Outpoint = new OutPoint(new uint256(2), 0),
            Value = new Money(70_000L),
            ScriptPubKey = scriptPubKey,
            KeyPath = KeyPath.Parse("0/0"),
            Confirmations = 1
        };
        var availableUTXOs = new List<UTXO> { closest, fewestConfirmations };

        var request = new ChannelOperationRequest { Id = 1, Wallet = wallet, SatsAmount = 50_000 };
        var coinSelectionService = CreateCoinSelectionService(availableUTXOs);

        // Act
        var (orderedCoins, orderedSelection) = await coinSelectionService.GetTxInputCoins(
            availableUTXOs, request, derivationStrategy, preserveOrder: true);
        var (_, defaultSelection) = await coinSelectionService.GetTxInputCoins(
            availableUTXOs, request, derivationStrategy);

        // Assert
        orderedSelection.Should().ContainSingle();
        orderedSelection[0].Outpoint.Should().Be(closest.Outpoint);

        // The coins keep the same order as the UTXOs. AddDerivationData pairs the two lists by
        // position, so they have to line up
        orderedCoins.Select(x => x.Outpoint).Should().ContainInOrder(orderedSelection.Select(x => x.Outpoint));

        // Without preserveOrder the list is re-sorted by confirmations, so a different UTXO wins
        defaultSelection.Should().ContainSingle();
        defaultSelection[0].Outpoint.Should().Be(fewestConfirmations.Outpoint);
    }
}
