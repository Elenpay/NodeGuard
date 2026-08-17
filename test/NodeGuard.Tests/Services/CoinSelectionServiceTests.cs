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
using NodeGuard.Helpers;
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
    public async Task GetAvailableUTXOsAsync_WithCustomBackend_IgnoresLockedFrozenAndDustServerSide()
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

            // Locked, frozen and dust UTXOs must all be ignored server-side so the backend does
            // not count them towards the requested amount and return a short selection
            ignoredOutpoints.Should().NotBeNull();
            ignoredOutpoints.Should().Contain(dustUtxo.Outpoint.ToString());
            ignoredOutpoints.Should().Contain($"{lockedUtxo.Outpoint.Hash}-{lockedUtxo.Outpoint.N}");
            ignoredOutpoints.Should().Contain(frozenUtxo.Outpoint.ToString());
            ignoredOutpoints.Should().NotContain(availableUtxo.Outpoint.ToString());
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

    private CoinSelectionService CreateServiceForLockUTXOs(
        out Mock<IWalletWithdrawalRequestRepository> walletWithdrawalRequestRepository,
        List<FMUTXO>? lockedUtxos = null)
    {
        var fmutxoRepository = new Mock<IFMUTXORepository>();
        fmutxoRepository.Setup(x => x.GetLockedUTXOs(It.IsAny<int?>(), It.IsAny<int?>()))
            .ReturnsAsync(lockedUtxos ?? new List<FMUTXO>());
        var utxoTagRepository = new Mock<IUTXOTagRepository>();
        utxoTagRepository.Setup(x => x.GetByKeyValue(It.IsAny<string>(), It.IsAny<string>()))
            .ReturnsAsync(new List<UTXOTag>());
        walletWithdrawalRequestRepository = new Mock<IWalletWithdrawalRequestRepository>();
        walletWithdrawalRequestRepository
            .Setup(x => x.AddUTXOs(It.IsAny<IBitcoinRequest>(), It.IsAny<List<FMUTXO>>()))
            .ReturnsAsync((true, (string?)null));
        var mapper = new Mock<IMapper>();
        mapper.Setup(m => m.Map<UTXO, FMUTXO>(It.IsAny<UTXO>())).Returns(new FMUTXO());

        return new CoinSelectionService(_logger, mapper.Object, fmutxoRepository.Object,
            new Mock<INBXplorerService>().Object, new Mock<IChannelOperationRequestRepository>().Object,
            walletWithdrawalRequestRepository.Object, utxoTagRepository.Object);
    }

    private CoinSelectionService CreateServiceForSelectAndLock(
        List<UTXO> confirmedUtxos,
        out Mock<IWalletWithdrawalRequestRepository> walletWithdrawalRequestRepository,
        List<FMUTXO>? alreadyLockedToThisRequest = null)
    {
        var fmutxoRepository = new Mock<IFMUTXORepository>();
        fmutxoRepository.Setup(x => x.GetLockedUTXOs(It.IsAny<int?>(), It.IsAny<int?>()))
            .ReturnsAsync(new List<FMUTXO>());
        var utxoTagRepository = new Mock<IUTXOTagRepository>();
        utxoTagRepository.Setup(x => x.GetByKeyValue(It.IsAny<string>(), It.IsAny<string>()))
            .ReturnsAsync(new List<UTXOTag>());

        walletWithdrawalRequestRepository = new Mock<IWalletWithdrawalRequestRepository>();
        walletWithdrawalRequestRepository
            .Setup(x => x.GetUTXOs(It.IsAny<IBitcoinRequest>()))
            .ReturnsAsync((true, alreadyLockedToThisRequest ?? new List<FMUTXO>()));
        walletWithdrawalRequestRepository
            .Setup(x => x.AddUTXOs(It.IsAny<IBitcoinRequest>(), It.IsAny<List<FMUTXO>>()))
            .ReturnsAsync((true, (string?)null));

        var nbXplorerService = new Mock<INBXplorerService>();
        nbXplorerService
            .Setup(x => x.GetUTXOsAsync(It.IsAny<DerivationStrategyBase>(), default))
            .ReturnsAsync(new UTXOChanges { Confirmed = new UTXOChange { UTXOs = confirmedUtxos } });

        var mapper = new Mock<IMapper>();
        mapper.Setup(m => m.Map<UTXO, FMUTXO>(It.IsAny<UTXO>())).Returns(new FMUTXO());

        return new CoinSelectionService(_logger, mapper.Object, fmutxoRepository.Object,
            nbXplorerService.Object, new Mock<IChannelOperationRequestRepository>().Object,
            walletWithdrawalRequestRepository.Object, utxoTagRepository.Object);
    }

    [Fact]
    public async Task SelectAndLockUTXOsAsync_ReleasesTheWalletLockBeforeReturning()
    {
        // The point of doing select+lock as one owned step is that the per-wallet lock is held only
        // for that, and is already released by the time the caller goes on to build its PSBT. If it
        // were still held, the caller's next call for the same wallet (or its own nested locking)
        // would block forever - which is exactly the deadlock this design removes.
        var wallet = CreateWallet.SingleSig(_internalWallet);
        var service = CreateServiceForSelectAndLock(new List<UTXO> { CreateUtxo(1, 100_000) }, out _);
        var request = new WalletWithdrawalRequest
        {
            Id = 99, WalletId = wallet.Id, Wallet = wallet,
            WalletWithdrawalRequestDestinations = new List<WalletWithdrawalRequestDestination>
            {
                new() { Address = "1", Amount = 0.0001m }
            }
        };

        await service.SelectAndLockUTXOsAsync(request, BitcoinRequestType.WalletWithdrawal,
            wallet.GetDerivationStrategy());

        // A second call for the same wallet can only complete promptly if the first one released.
        var again = service.SelectAndLockUTXOsAsync(request, BitcoinRequestType.WalletWithdrawal,
            wallet.GetDerivationStrategy());
        var winner = await Task.WhenAny(again, Task.Delay(TimeSpan.FromSeconds(5)));
        winner.Should().Be(again,
            "the wallet lock must be released before returning, so the PSBT build never holds it");
        await again;
    }

    [Fact]
    public async Task SelectAndLockUTXOsAsync_WhenRequestAlreadyOwnsUTXOs_DoesNotLockASecondSet()
    {
        // A retried/resumed request must reuse the UTXOs already locked to it rather than selecting
        // and locking a second, different set.
        var wallet = CreateWallet.SingleSig(_internalWallet);
        var utxo = CreateUtxo(1, 100_000);
        var alreadyLocked = new List<FMUTXO>
        {
            new() { TxId = utxo.Outpoint.Hash.ToString(), OutputIndex = utxo.Outpoint.N, SatsAmount = 100_000 }
        };
        var service = CreateServiceForSelectAndLock(new List<UTXO> { utxo },
            out var walletWithdrawalRequestRepository, alreadyLocked);
        var request = new WalletWithdrawalRequest
        {
            Id = 99, WalletId = wallet.Id, Wallet = wallet,
            WalletWithdrawalRequestDestinations = new List<WalletWithdrawalRequestDestination>
            {
                new() { Address = "1", Amount = 0.0001m }
            }
        };

        await service.SelectAndLockUTXOsAsync(request, BitcoinRequestType.WalletWithdrawal,
            wallet.GetDerivationStrategy());

        walletWithdrawalRequestRepository.Verify(
            x => x.AddUTXOs(It.IsAny<IBitcoinRequest>(), It.IsAny<List<FMUTXO>>()), Times.Never);
    }

    [Fact]
    public async Task ConcurrentSelectAndLockForTheSameWallet_NeverOverlap()
    {
        // The whole point of the lock: read-available -> select -> lock must be indivisible, so two
        // concurrent requests for the same wallet can never both be inside it (and therefore can
        // never both select the same UTXO). Observed by counting how many callers are inside the
        // critical section at once, via a deliberately slow repository read.
        var wallet = CreateWallet.SingleSig(_internalWallet);
        var inFlight = 0;
        var maxObservedInFlight = 0;

        var fmutxoRepository = new Mock<IFMUTXORepository>();
        fmutxoRepository.Setup(x => x.GetLockedUTXOs(It.IsAny<int?>(), It.IsAny<int?>()))
            .ReturnsAsync(new List<FMUTXO>());
        var utxoTagRepository = new Mock<IUTXOTagRepository>();
        utxoTagRepository.Setup(x => x.GetByKeyValue(It.IsAny<string>(), It.IsAny<string>()))
            .ReturnsAsync(new List<UTXOTag>());

        var walletWithdrawalRequestRepository = new Mock<IWalletWithdrawalRequestRepository>();
        walletWithdrawalRequestRepository
            .Setup(x => x.GetUTXOs(It.IsAny<IBitcoinRequest>()))
            .Returns(async () =>
            {
                maxObservedInFlight = Math.Max(maxObservedInFlight, Interlocked.Increment(ref inFlight));
                await Task.Delay(200);
                Interlocked.Decrement(ref inFlight);
                return (true, new List<FMUTXO>());
            });
        walletWithdrawalRequestRepository
            .Setup(x => x.AddUTXOs(It.IsAny<IBitcoinRequest>(), It.IsAny<List<FMUTXO>>()))
            .ReturnsAsync((true, (string?)null));

        var nbXplorerService = new Mock<INBXplorerService>();
        nbXplorerService
            .Setup(x => x.GetUTXOsAsync(It.IsAny<DerivationStrategyBase>(), default))
            .ReturnsAsync(new UTXOChanges
            {
                Confirmed = new UTXOChange { UTXOs = new List<UTXO> { CreateUtxo(1, 100_000) } }
            });

        var mapper = new Mock<IMapper>();
        mapper.Setup(m => m.Map<UTXO, FMUTXO>(It.IsAny<UTXO>())).Returns(new FMUTXO());

        var service = new CoinSelectionService(_logger, mapper.Object, fmutxoRepository.Object,
            nbXplorerService.Object, new Mock<IChannelOperationRequestRepository>().Object,
            walletWithdrawalRequestRepository.Object, utxoTagRepository.Object);

        WalletWithdrawalRequest NewRequest(int id) => new()
        {
            Id = id, WalletId = wallet.Id, Wallet = wallet,
            WalletWithdrawalRequestDestinations = new List<WalletWithdrawalRequestDestination>
            {
                new() { Address = "1", Amount = 0.0001m }
            }
        };

        await Task.WhenAll(
            service.SelectAndLockUTXOsAsync(NewRequest(1), BitcoinRequestType.WalletWithdrawal, wallet.GetDerivationStrategy()),
            service.SelectAndLockUTXOsAsync(NewRequest(2), BitcoinRequestType.WalletWithdrawal, wallet.GetDerivationStrategy()));

        maxObservedInFlight.Should().Be(1,
            "the per-wallet lock must keep the select+lock sequences from overlapping");
    }

    [Fact]
    public async Task LockUTXOs_ConflictingOutpoint_ThrowsUtxoAlreadyLockedException()
    {
        // Arrange
        var utxo = CreateUtxo(1, 10_000);
        var fmutxoRepository = new Mock<IFMUTXORepository>();
        fmutxoRepository
            .Setup(x => x.GetLockedUTXOs(null, null))
            .ReturnsAsync(new List<FMUTXO>
            {
                new() { TxId = utxo.Outpoint.Hash.ToString(), OutputIndex = utxo.Outpoint.N }
            });
        var utxoTagRepository = new Mock<IUTXOTagRepository>();
        utxoTagRepository.Setup(x => x.GetByKeyValue(It.IsAny<string>(), It.IsAny<string>()))
            .ReturnsAsync(new List<UTXOTag>());
        var walletWithdrawalRequestRepository = new Mock<IWalletWithdrawalRequestRepository>();
        var mapper = new Mock<IMapper>();

        var coinSelectionService = new CoinSelectionService(_logger, mapper.Object, fmutxoRepository.Object,
            new Mock<INBXplorerService>().Object, new Mock<IChannelOperationRequestRepository>().Object,
            walletWithdrawalRequestRepository.Object, utxoTagRepository.Object);

        var withdrawalRequest = new WalletWithdrawalRequest { Id = 99, Wallet = new Wallet { Id = 1 } };

        // Act
        var act = () => coinSelectionService.LockUTXOs(new List<UTXO> { utxo }, withdrawalRequest,
            BitcoinRequestType.WalletWithdrawal);

        // Assert
        await act.Should().ThrowAsync<UtxoAlreadyLockedException>();
        walletWithdrawalRequestRepository.Verify(
            x => x.AddUTXOs(It.IsAny<IBitcoinRequest>(), It.IsAny<List<FMUTXO>>()), Times.Never);
    }

    [Fact]
    public async Task LockUTXOs_NoConflict_LocksTheUtxo()
    {
        // Arrange
        var utxo = CreateUtxo(1, 10_000);
        var fmutxoRepository = new Mock<IFMUTXORepository>();
        fmutxoRepository
            .Setup(x => x.GetLockedUTXOs(null, null))
            .ReturnsAsync(new List<FMUTXO>());
        var utxoTagRepository = new Mock<IUTXOTagRepository>();
        utxoTagRepository.Setup(x => x.GetByKeyValue(It.IsAny<string>(), It.IsAny<string>()))
            .ReturnsAsync(new List<UTXOTag>());
        var walletWithdrawalRequestRepository = new Mock<IWalletWithdrawalRequestRepository>();
        walletWithdrawalRequestRepository
            .Setup(x => x.AddUTXOs(It.IsAny<IBitcoinRequest>(), It.IsAny<List<FMUTXO>>()))
            .ReturnsAsync((true, (string?)null));
        var mapper = new Mock<IMapper>();
        mapper.Setup(m => m.Map<UTXO, FMUTXO>(It.IsAny<UTXO>())).Returns(new FMUTXO());

        var coinSelectionService = new CoinSelectionService(_logger, mapper.Object, fmutxoRepository.Object,
            new Mock<INBXplorerService>().Object, new Mock<IChannelOperationRequestRepository>().Object,
            walletWithdrawalRequestRepository.Object, utxoTagRepository.Object);

        var withdrawalRequest = new WalletWithdrawalRequest { Id = 99, Wallet = new Wallet { Id = 1 } };

        // Act
        await coinSelectionService.LockUTXOs(new List<UTXO> { utxo }, withdrawalRequest,
            BitcoinRequestType.WalletWithdrawal);

        // Assert
        walletWithdrawalRequestRepository.Verify(
            x => x.AddUTXOs(withdrawalRequest, It.IsAny<List<FMUTXO>>()), Times.Once);
    }

    [Fact]
    public async Task LockUTXOs_PreviousRequestIdAllowedToShareUtxos_DoesNotConflictWithBumpedRequest()
    {
        // Arrange: the UTXO is locked by request 42, which is the specific request being bumped,
        // so it must be allowed through rather than treated as a conflict.
        var utxo = CreateUtxo(1, 10_000);
        var fmutxoRepository = new Mock<IFMUTXORepository>();
        fmutxoRepository
            .Setup(x => x.GetLockedUTXOs(42, null))
            .ReturnsAsync(new List<FMUTXO>());
        var utxoTagRepository = new Mock<IUTXOTagRepository>();
        utxoTagRepository.Setup(x => x.GetByKeyValue(It.IsAny<string>(), It.IsAny<string>()))
            .ReturnsAsync(new List<UTXOTag>());
        var walletWithdrawalRequestRepository = new Mock<IWalletWithdrawalRequestRepository>();
        walletWithdrawalRequestRepository
            .Setup(x => x.AddUTXOs(It.IsAny<IBitcoinRequest>(), It.IsAny<List<FMUTXO>>()))
            .ReturnsAsync((true, (string?)null));
        var mapper = new Mock<IMapper>();
        mapper.Setup(m => m.Map<UTXO, FMUTXO>(It.IsAny<UTXO>())).Returns(new FMUTXO());

        var coinSelectionService = new CoinSelectionService(_logger, mapper.Object, fmutxoRepository.Object,
            new Mock<INBXplorerService>().Object, new Mock<IChannelOperationRequestRepository>().Object,
            walletWithdrawalRequestRepository.Object, utxoTagRepository.Object);

        var bumpRequest = new WalletWithdrawalRequest { Id = 100, Wallet = new Wallet { Id = 1 } };

        // Act
        await coinSelectionService.LockUTXOs(new List<UTXO> { utxo }, bumpRequest,
            BitcoinRequestType.WalletWithdrawal, previousRequestIdAllowedToShareUtxos: 42);

        // Assert
        fmutxoRepository.Verify(x => x.GetLockedUTXOs(42, null), Times.Once);
        walletWithdrawalRequestRepository.Verify(
            x => x.AddUTXOs(bumpRequest, It.IsAny<List<FMUTXO>>()), Times.Once);
    }
}
