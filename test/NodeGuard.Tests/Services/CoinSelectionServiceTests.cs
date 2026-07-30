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
}
