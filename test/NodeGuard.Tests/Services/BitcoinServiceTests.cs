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
using NSubstitute.Exceptions;
using Key = NodeGuard.Data.Models.Key;

namespace NodeGuard.Services;

public class BitcoinServiceTests
{
    private ILogger<BitcoinService> _logger = new Mock<ILogger<BitcoinService>>().Object;
    private InternalWallet _internalWallet = CreateWallet.CreateInternalWallet();

    [Fact]
    async Task GenerateTemplatePSBT_NoWithdrawalRequest()
    {
        // Arrange
        var bitcoinService = new BitcoinService(null, null, null, null, null, null, null, null);

        // Act
        var act = () => bitcoinService.GenerateTemplatePSBT(null);

        // Assert
        await act
            .Should()
            .ThrowAsync<ArgumentNullException>()
            .WithMessage("Value cannot be null. (Parameter 'walletWithdrawalRequest')");
    }

    [Theory]
    [InlineData(WalletWithdrawalRequestStatus.Cancelled)]
    [InlineData(WalletWithdrawalRequestStatus.Failed)]
    [InlineData(WalletWithdrawalRequestStatus.Rejected)]
    [InlineData(WalletWithdrawalRequestStatus.OnChainConfirmationPending)]
    [InlineData(WalletWithdrawalRequestStatus.OnChainConfirmed)]
    async Task GenerateTemplatePSBT_RequestNotPending(WalletWithdrawalRequestStatus status)
    {
        // Arrange
        var withdrawalRequest = new WalletWithdrawalRequest()
        {
            Id = 1,
            Status = status
        };
        var walletWithdrawalRequestRepository = new Mock<IWalletWithdrawalRequestRepository>();
        walletWithdrawalRequestRepository
            .Setup((w) => w.GetById(It.IsAny<int>()))
            .ReturnsAsync(withdrawalRequest);

        var bitcoinService = new BitcoinService(_logger, null, walletWithdrawalRequestRepository.Object, null, null, null, null, null);

        // Act
        var act = () => bitcoinService.GenerateTemplatePSBT(withdrawalRequest);

        // Assert
        await act
            .Should()
            .ThrowAsync<InvalidOperationException>()
            .WithMessage("PSBT Generation cancelled, operation is not in pending state");
    }

    [Fact]
    async Task GenerateTemplatePSBT_NBXplorerNotFullySynced()
    {
        // Arrange
        var withdrawalRequest = new WalletWithdrawalRequest()
        {
            Id = 1,
            Status = WalletWithdrawalRequestStatus.Pending
        };
        var walletWithdrawalRequestRepository = new Mock<IWalletWithdrawalRequestRepository>();
        var nbXplorerService = new Mock<INBXplorerService>();
        walletWithdrawalRequestRepository
            .Setup((w) => w.GetById(It.IsAny<int>()))
            .ReturnsAsync(withdrawalRequest);
        nbXplorerService
            .Setup(x => x.GetStatusAsync(default))
            .ReturnsAsync(new StatusResult() { IsFullySynched = false });

        var bitcoinService = new BitcoinService(_logger, null, walletWithdrawalRequestRepository.Object, null, null, null, nbXplorerService.Object, null);

        // Act
        var act = () => bitcoinService.GenerateTemplatePSBT(withdrawalRequest);

        // Assert
        await act
            .Should()
            .ThrowAsync<NBXplorerNotFullySyncedException>()
            .WithMessage("Error, nbxplorer not fully synched");
    }

    [Fact]
    async Task GenerateTemplatePSBT_NoDerivationStrategy()
    {
        // Arrange
        var withdrawalRequest = new WalletWithdrawalRequest()
        {
            Id = 1,
            Status = WalletWithdrawalRequestStatus.Pending,
            Wallet = CreateWallet.MultiSig(_internalWallet)
        };
        withdrawalRequest.Wallet.Keys = new List<Key>();
        var walletWithdrawalRequestRepository = new Mock<IWalletWithdrawalRequestRepository>();
        var nbXplorerService = new Mock<INBXplorerService>();
        walletWithdrawalRequestRepository
            .Setup((w) => w.GetById(It.IsAny<int>()))
            .ReturnsAsync(withdrawalRequest);
        nbXplorerService
            .Setup(x => x.GetStatusAsync(default))
            .ReturnsAsync(new StatusResult() { IsFullySynched = true });

        var bitcoinService = new BitcoinService(_logger, null, walletWithdrawalRequestRepository.Object, null, null, null, nbXplorerService.Object, null);

        // Act
        var act = () => bitcoinService.GenerateTemplatePSBT(withdrawalRequest);

        // Assert
        await act
            .Should()
            .ThrowAsync<ArgumentNotFoundException>()
            .WithMessage("Error while getting the derivation strategy scheme for wallet: 0");
    }

    [Fact]
    async Task GenerateTemplatePSBT_LegacyMultiSigSucceeds()
    {
        // Arrange
        var wallet = CreateWallet.LegacyMultiSig(_internalWallet);
        var withdrawalRequest = new WalletWithdrawalRequest()
        {
            Id = 1,
            Status = WalletWithdrawalRequestStatus.Pending,
            Wallet = wallet,
            WalletWithdrawalRequestPSBTs = new List<WalletWithdrawalRequestPSBT>(),
            WalletWithdrawalRequestDestinations = new List<WalletWithdrawalRequestDestination>
            {
                new WalletWithdrawalRequestDestination
                {
                    Address = "bcrt1q8k3av6q5yp83rn332lx8a90k6kukhg28hs5qw7krdq95t629hgsqk6ztmf",
                    Amount = 0.01m
                }
            }
        };

        var walletWithdrawalRequestRepository = new Mock<IWalletWithdrawalRequestRepository>();
        var walletWithdrawalRequestPsbtRepository = new Mock<IWalletWithdrawalRequestPsbtRepository>();
        var fmutxoRepository = new Mock<IFMUTXORepository>();
        var nbXplorerService = new Mock<INBXplorerService>();
        var utxoTagRepository = new Mock<IUTXOTagRepository>();
        var mapper = new Mock<IMapper>();
        walletWithdrawalRequestRepository
            .Setup((w) => w.GetById(It.IsAny<int>()))
            .ReturnsAsync(withdrawalRequest);
        walletWithdrawalRequestRepository
            .Setup((w) => w.AddUTXOs(It.IsAny<WalletWithdrawalRequest>(), It.IsAny<List<FMUTXO>>()))
            .ReturnsAsync((true, null));
        walletWithdrawalRequestPsbtRepository
            .Setup((w) => w.AddAsync(It.IsAny<WalletWithdrawalRequestPSBT>()))
            .ReturnsAsync((true, null));
        nbXplorerService
            .Setup(x => x.GetStatusAsync(default))
            .ReturnsAsync(new StatusResult() { IsFullySynched = true });
        nbXplorerService
            .Setup(x => x.GetUnusedAsync(It.IsAny<DerivationStrategyBase>(), DerivationFeature.Change, 0, false, default))
            .ReturnsAsync(new KeyPathInformation() { Address = BitcoinAddress.Create("bcrt1q83ml8tve8vh672wsm83getxfzetaquq352jr6t423tdwjvdz3f3qe4r4t7", Network.RegTest) });
        nbXplorerService
            .Setup(x => x.GetUTXOsAsync(It.IsAny<DerivationStrategyBase>(), default))
            .ReturnsAsync(new UTXOChanges()
            {
                Confirmed = new UTXOChange()
                {
                    UTXOs = new List<UTXO>()
                    {
                        new UTXO()
                        {
                            Outpoint = new OutPoint(),
                            Value = new Money((long)10000000),
                            ScriptPubKey = (wallet.GetDerivationStrategy() as StandardDerivationStrategyBase)!.GetDerivation(KeyPath.Parse("0/0")).ScriptPubKey,
                            KeyPath = KeyPath.Parse("0/0")
                        }
                    }
                }
            });
        fmutxoRepository
            .Setup(x => x.GetLockedUTXOs(null, null))
            .ReturnsAsync(new List<FMUTXO>());
        utxoTagRepository
            .Setup(x => x.GetByKeyValue(It.IsAny<string>(), It.IsAny<string>()))
            .ReturnsAsync(new List<UTXOTag>());
        var coinSelectionService = new CoinSelectionService(_logger, mapper.Object, fmutxoRepository.Object, nbXplorerService.Object, null, walletWithdrawalRequestRepository.Object, utxoTagRepository.Object);

        var bitcoinService = new BitcoinService(_logger, mapper.Object, walletWithdrawalRequestRepository.Object, walletWithdrawalRequestPsbtRepository.Object, null, null, nbXplorerService.Object, coinSelectionService);

        // Act
        var result = await bitcoinService.GenerateTemplatePSBT(withdrawalRequest);

        // Assert
        var psbt = PSBT.Parse("cHNidP8BAIkBAAAAAQAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA/////wD9////AkBCDwAAAAAAIgAgPaPWaBQgTxHOMVfMfpX21blroUe8KAd6w2gLRelFuiAYUYkAAAAAACIAIDx3862ZOy+vKdDZ4oysyRZX0HARoqQ9LqqK2ukxoopiAAAAAE8BBDWHzwMvESQsgAAAAfw77kI6AYzrbSJqBmMojtD7XuD6nXkKs3DQMOBHMObIA4COLhzUgr3QcZaUPFqBM9Fpr4YCK2uwOBdxZE7AdETXEB/M5N4wAACAAQAAgAEAAIBPAQQ1h88DVqwD9IAAAAH5CK5KZrD/oasUtVrwzkjypwIly5AQkC1pAa+QuT6PgQJRrxXgW7i36sGJWz9fR//v7NgyGgLvIimPidCiA33wYBBg86CzMAAAgAEAAIABAACATwEENYfPA325Ro2AAAAB9SJwx2h6Ovs1HvTxuaMMEPO205IXBoOuqUiME5oRyZgDIiOFIzjqZ/v9jcNSqyYl55ondkYhI2vxwCEwkNNInp8Q7QIQyDAAAIABAACAAQAAgAABASuAlpgAAAAAACIAILNTGKQyViCBs/y3kcG+Q/3NcIIypkqLb3/EMmN57BDEAQVpUiEC2FTFYM/mwE4L60Q0G2p5QElV7YlMD7fcgoJEH79pLLEhAwJn/wsRl0hvcYj5Y3Bv3uQlxZ57pBZ9KSeuEPVNmjS/IQMaU3fyWsF+N0FpN8hSusDj6bESvd9YR509kdgWMLKLj1OuIgYC2FTFYM/mwE4L60Q0G2p5QElV7YlMD7fcgoJEH79pLLEYH8zk3jAAAIABAACAAQAAgAAAAAAAAAAAIgYDAmf/CxGXSG9xiPljcG/e5CXFnnukFn0pJ64Q9U2aNL8YYPOgszAAAIABAACAAQAAgAAAAAAAAAAAIgYDGlN38lrBfjdBaTfIUrrA4+mxEr3fWEedPZHYFjCyi48Y7QIQyDAAAIABAACAAQAAgAAAAAAAAAAAAAAA", Network.RegTest);
        result.Should().BeEquivalentTo(psbt);
    }

    [Fact]
    async Task GenerateTemplatePSBT_MultiSigSucceeds()
    {
        // Arrange
        var wallet = CreateWallet.MultiSig(_internalWallet);
        var withdrawalRequest = new WalletWithdrawalRequest()
        {
            Id = 1,
            Status = WalletWithdrawalRequestStatus.Pending,
            Wallet = wallet,
            WalletWithdrawalRequestPSBTs = new List<WalletWithdrawalRequestPSBT>(),
            WalletWithdrawalRequestDestinations = new List<WalletWithdrawalRequestDestination>
            {
                new WalletWithdrawalRequestDestination
                {
                    Address = "bcrt1q8k3av6q5yp83rn332lx8a90k6kukhg28hs5qw7krdq95t629hgsqk6ztmf",
                    Amount = 0.01m
                }
            }
        };

        var walletWithdrawalRequestRepository = new Mock<IWalletWithdrawalRequestRepository>();
        var walletWithdrawalRequestPsbtRepository = new Mock<IWalletWithdrawalRequestPsbtRepository>();
        var fmutxoRepository = new Mock<IFMUTXORepository>();
        var nbXplorerService = new Mock<INBXplorerService>();
        var utxoTagRepository = new Mock<IUTXOTagRepository>();
        var mapper = new Mock<IMapper>();
        walletWithdrawalRequestRepository
            .Setup((w) => w.GetById(It.IsAny<int>()))
            .ReturnsAsync(withdrawalRequest);
        walletWithdrawalRequestRepository
            .Setup((w) => w.AddUTXOs(It.IsAny<WalletWithdrawalRequest>(), It.IsAny<List<FMUTXO>>()))
            .ReturnsAsync((true, null));
        walletWithdrawalRequestPsbtRepository
            .Setup((w) => w.AddAsync(It.IsAny<WalletWithdrawalRequestPSBT>()))
            .ReturnsAsync((true, null));
        nbXplorerService
            .Setup(x => x.GetStatusAsync(default))
            .ReturnsAsync(new StatusResult() { IsFullySynched = true });
        nbXplorerService
            .Setup(x => x.GetUnusedAsync(It.IsAny<DerivationStrategyBase>(), DerivationFeature.Change, 0, false, default))
            .ReturnsAsync(new KeyPathInformation() { Address = BitcoinAddress.Create("bcrt1q83ml8tve8vh672wsm83getxfzetaquq352jr6t423tdwjvdz3f3qe4r4t7", Network.RegTest) });
        nbXplorerService
            .Setup(x => x.GetUTXOsAsync(It.IsAny<DerivationStrategyBase>(), default))
            .ReturnsAsync(new UTXOChanges()
            {
                Confirmed = new UTXOChange()
                {
                    UTXOs = new List<UTXO>()
                    {
                        new UTXO()
                        {
                            Outpoint = new OutPoint(),
                            Value = new Money((long)10000000),
                            ScriptPubKey = (wallet.GetDerivationStrategy() as StandardDerivationStrategyBase)!.GetDerivation(KeyPath.Parse("0/0")).ScriptPubKey,
                            KeyPath = KeyPath.Parse("0/0")
                        }
                    }
                }
            });
        fmutxoRepository
            .Setup(x => x.GetLockedUTXOs(null, null))
            .ReturnsAsync(new List<FMUTXO>());
        utxoTagRepository
            .Setup(x => x.GetByKeyValue(It.IsAny<string>(), It.IsAny<string>()))
            .ReturnsAsync(new List<UTXOTag>());

        var coinSelectionService = new CoinSelectionService(_logger, mapper.Object, fmutxoRepository.Object, nbXplorerService.Object, null, walletWithdrawalRequestRepository.Object, utxoTagRepository.Object);

        var bitcoinService = new BitcoinService(_logger, mapper.Object, walletWithdrawalRequestRepository.Object, walletWithdrawalRequestPsbtRepository.Object, null, null, nbXplorerService.Object, coinSelectionService);

        // Act
        var result = await bitcoinService.GenerateTemplatePSBT(withdrawalRequest);

        // Assert
        var psbt = PSBT.Parse("cHNidP8BAIkBAAAAAQAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA/////wD9////AkBCDwAAAAAAIgAgPaPWaBQgTxHOMVfMfpX21blroUe8KAd6w2gLRelFuiAYUYkAAAAAACIAIDx3862ZOy+vKdDZ4oysyRZX0HARoqQ9LqqK2ukxoopiAAAAAE8BBDWHzwMvESQsgAAAAfw77kI6AYzrbSJqBmMojtD7XuD6nXkKs3DQMOBHMObIA4COLhzUgr3QcZaUPFqBM9Fpr4YCK2uwOBdxZE7AdETXEB/M5N4wAACAAQAAgAEAAIBPAQQ1h88DVqwD9IAAAAH5CK5KZrD/oasUtVrwzkjypwIly5AQkC1pAa+QuT6PgQJRrxXgW7i36sGJWz9fR//v7NgyGgLvIimPidCiA33wYBBg86CzMAAAgAEAAIABAACATwEENYfPA325Ro0AAAAAgN63GqLxTu1/NyL0SV4a0Hn1n8Dzg+Wye9nbb16ZISADr+s+pcKnDcSqKHKWSl4v8Rcq80ZqG/7QObYmZUl/xUYQ7QIQyDAAAIABAACAAAAAAAABASuAlpgAAAAAACIAINCp0IUCw4KZ8J/JokbAV1TBQtK4m6WLzUomP5VBhszOAQVpUiEC2FTFYM/mwE4L60Q0G2p5QElV7YlMD7fcgoJEH79pLLEhAwJn/wsRl0hvcYj5Y3Bv3uQlxZ57pBZ9KSeuEPVNmjS/IQNvzitZiz5ksZFSQuRibjPP4pwo+OWOqZLBL2x5ZrFVqVOuIgYC2FTFYM/mwE4L60Q0G2p5QElV7YlMD7fcgoJEH79pLLEYH8zk3jAAAIABAACAAQAAgAAAAAAAAAAAIgYDAmf/CxGXSG9xiPljcG/e5CXFnnukFn0pJ64Q9U2aNL8YYPOgszAAAIABAACAAQAAgAAAAAAAAAAAIgYDb84rWYs+ZLGRUkLkYm4zz+KcKPjljqmSwS9seWaxVakY7QIQyDAAAIABAACAAAAAAAAAAAAAAAAAAAAA", Network.RegTest);
        result.Should().BeEquivalentTo(psbt);
    }

    [Fact]
    async Task GenerateTemplatePSBT_SingleSigSucceeds()
    {
        // Arrange
        var wallet = CreateWallet.SingleSig(_internalWallet);
        var withdrawalRequest = new WalletWithdrawalRequest()
        {
            Id = 1,
            Status = WalletWithdrawalRequestStatus.Pending,
            Wallet = wallet,
            WalletWithdrawalRequestPSBTs = new List<WalletWithdrawalRequestPSBT>(),
            WalletWithdrawalRequestDestinations = new List<WalletWithdrawalRequestDestination>
            {
                new WalletWithdrawalRequestDestination
                {
                    Address = "bcrt1q8k3av6q5yp83rn332lx8a90k6kukhg28hs5qw7krdq95t629hgsqk6ztmf",
                    Amount = 0.01m
                }
            }
        };

        var walletWithdrawalRequestRepository = new Mock<IWalletWithdrawalRequestRepository>();
        var walletWithdrawalRequestPsbtRepository = new Mock<IWalletWithdrawalRequestPsbtRepository>();
        var fmutxoRepository = new Mock<IFMUTXORepository>();
        var nbXplorerService = new Mock<INBXplorerService>();
        var utxoTagRepository = new Mock<IUTXOTagRepository>();
        var mapper = new Mock<IMapper>();
        walletWithdrawalRequestRepository
            .Setup((w) => w.GetById(It.IsAny<int>()))
            .ReturnsAsync(withdrawalRequest);
        walletWithdrawalRequestRepository
            .Setup((w) => w.AddUTXOs(It.IsAny<WalletWithdrawalRequest>(), It.IsAny<List<FMUTXO>>()))
            .ReturnsAsync((true, null));
        walletWithdrawalRequestPsbtRepository
            .Setup((w) => w.AddAsync(It.IsAny<WalletWithdrawalRequestPSBT>()))
            .ReturnsAsync((true, null));
        nbXplorerService
            .Setup(x => x.GetStatusAsync(default))
            .ReturnsAsync(new StatusResult() { IsFullySynched = true });
        nbXplorerService
            .Setup(x => x.GetUnusedAsync(It.IsAny<DerivationStrategyBase>(), DerivationFeature.Change, 0, false, default))
            .ReturnsAsync(new KeyPathInformation() { Address = BitcoinAddress.Create("bcrt1q83ml8tve8vh672wsm83getxfzetaquq352jr6t423tdwjvdz3f3qe4r4t7", Network.RegTest) });
        nbXplorerService
            .Setup(x => x.GetUTXOsAsync(It.IsAny<DerivationStrategyBase>(), default))
            .ReturnsAsync(new UTXOChanges()
            {
                Confirmed = new UTXOChange()
                {
                    UTXOs = new List<UTXO>()
                    {
                        new UTXO()
                        {
                            Value = new Money((long)10000000),
                            ScriptPubKey = (wallet.GetDerivationStrategy() as StandardDerivationStrategyBase)!.GetDerivation(KeyPath.Parse("0/0")).ScriptPubKey,
                            KeyPath = KeyPath.Parse("0/0")
                        }
                    }
                }
            });
        fmutxoRepository
            .Setup(x => x.GetLockedUTXOs(null, null))
            .ReturnsAsync(new List<FMUTXO>());
        utxoTagRepository
            .Setup(x => x.GetByKeyValue(It.IsAny<string>(), It.IsAny<string>()))
            .ReturnsAsync(new List<UTXOTag>());

        var coinSelectionService = new CoinSelectionService(_logger, mapper.Object, fmutxoRepository.Object, nbXplorerService.Object, null, walletWithdrawalRequestRepository.Object, utxoTagRepository.Object);

        var bitcoinService = new BitcoinService(_logger, mapper.Object, walletWithdrawalRequestRepository.Object, walletWithdrawalRequestPsbtRepository.Object, null, null, nbXplorerService.Object, coinSelectionService);

        // Act
        var result = await bitcoinService.GenerateTemplatePSBT(withdrawalRequest);

        // Assert
        var psbt = PSBT.Parse("cHNidP8BAIkBAAAAAQAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA/////wD9////AkBCDwAAAAAAIgAgPaPWaBQgTxHOMVfMfpX21blroUe8KAd6w2gLRelFuiCsUYkAAAAAACIAIDx3862ZOy+vKdDZ4oysyRZX0HARoqQ9LqqK2ukxoopiAAAAAE8BBDWHzwN9uUaNAAAAAYPR/OiA1LbTzxbLPvbXvtAwckIG3g+0T1zblR/ZodaiA5zBFsigPpL8htN/KJ/Ph8SPvQA/K+mSNXTSA0hgvPNuEO0CEMgwAACAAQAAgAEAAAAAAQEfgJaYAAAAAAAWABTpOvUBMqNMfl7P81etji6x4fXrMyIGA3uD9HVjgF5E+eQhHp+Na6femVYpc4bCA4DmimehAdWcGO0CEMgwAACAAQAAgAEAAAAAAAAAAAAAAAAAAA==", Network.RegTest);
        result.Should().BeEquivalentTo(psbt);
    }

    [Fact]
    async Task GenerateTemplatePSBT_SingleSigFailsFrozenUTXO()
    {
        // Arrange
        var wallet = CreateWallet.SingleSig(_internalWallet);
        var withdrawalRequest = new WalletWithdrawalRequest()
        {
            Id = 1,
            Status = WalletWithdrawalRequestStatus.Pending,
            Wallet = wallet,
            WalletWithdrawalRequestPSBTs = new List<WalletWithdrawalRequestPSBT>(),
            WalletWithdrawalRequestDestinations = new List<WalletWithdrawalRequestDestination>
            {
                new WalletWithdrawalRequestDestination
                {
                    Address = "bcrt1q8k3av6q5yp83rn332lx8a90k6kukhg28hs5qw7krdq95t629hgsqk6ztmf",
                    Amount = 0.01m
                }
            }
        };

        var walletWithdrawalRequestRepository = new Mock<IWalletWithdrawalRequestRepository>();
        var walletWithdrawalRequestPsbtRepository = new Mock<IWalletWithdrawalRequestPsbtRepository>();
        var fmutxoRepository = new Mock<IFMUTXORepository>();
        var nbXplorerService = new Mock<INBXplorerService>();
        var utxoTagRepository = new Mock<IUTXOTagRepository>();
        var mapper = new Mock<IMapper>();
        walletWithdrawalRequestRepository
            .Setup((w) => w.GetById(It.IsAny<int>()))
            .ReturnsAsync(withdrawalRequest);
        walletWithdrawalRequestRepository
            .Setup((w) => w.AddUTXOs(It.IsAny<WalletWithdrawalRequest>(), It.IsAny<List<FMUTXO>>()))
            .ReturnsAsync((true, null));
        walletWithdrawalRequestPsbtRepository
            .Setup((w) => w.AddAsync(It.IsAny<WalletWithdrawalRequestPSBT>()))
            .ReturnsAsync((true, null));
        nbXplorerService
            .Setup(x => x.GetStatusAsync(default))
            .ReturnsAsync(new StatusResult() { IsFullySynched = true });
        nbXplorerService
            .Setup(x => x.GetUnusedAsync(It.IsAny<DerivationStrategyBase>(), DerivationFeature.Change, 0, false, default))
            .ReturnsAsync(new KeyPathInformation() { Address = BitcoinAddress.Create("bcrt1q83ml8tve8vh672wsm83getxfzetaquq352jr6t423tdwjvdz3f3qe4r4t7", Network.RegTest) });
        nbXplorerService
            .Setup(x => x.GetUTXOsAsync(It.IsAny<DerivationStrategyBase>(), default))
            .ReturnsAsync(new UTXOChanges()
            {
                Confirmed = new UTXOChange()
                {
                    UTXOs = new List<UTXO>()
                    {
                        new UTXO()
                        {
                            Outpoint = new OutPoint(1234, 1),
                            Value = new Money((long)10000000),
                            ScriptPubKey = (wallet.GetDerivationStrategy() as StandardDerivationStrategyBase)!.GetDerivation(KeyPath.Parse("0/0")).ScriptPubKey,
                            KeyPath = KeyPath.Parse("0/0")
                        }
                    }
                }
            });
        fmutxoRepository
            .Setup(x => x.GetLockedUTXOs(null, null))
            .ReturnsAsync(new List<FMUTXO>());
        utxoTagRepository
            .SetupSequence(x => x.GetByKeyValue(It.IsAny<string>(), It.IsAny<string>()))
            .ReturnsAsync(new List<UTXOTag>()
            {
                new UTXOTag()
                {
                    Key = Constants.IsFrozenTag,
                    Value = "true",
                    Outpoint = "00000000000000000000000000000000000000000000000000000000000004d2-1"
                }
            })
            .ReturnsAsync(new List<UTXOTag>())
            .ReturnsAsync(new List<UTXOTag>());

        var coinSelectionService = new CoinSelectionService(_logger, mapper.Object, fmutxoRepository.Object, nbXplorerService.Object, null, walletWithdrawalRequestRepository.Object, utxoTagRepository.Object);

        var bitcoinService = new BitcoinService(_logger, mapper.Object, walletWithdrawalRequestRepository.Object, walletWithdrawalRequestPsbtRepository.Object, null, null, nbXplorerService.Object, coinSelectionService);

        // Act
        var act = () => bitcoinService.GenerateTemplatePSBT(withdrawalRequest);

        // Assert
        await act
            .Should()
            .ThrowAsync<NoUTXOsAvailableException>()
            .WithMessage("Exception of type 'NodeGuard.Helpers.NoUTXOsAvailableException' was thrown.");
    }

    [Fact]
    async Task GenerateTemplatePSBT_SingleSigSuccessManuallyUnfrozenUTXO()
    {
        // Arrange
        var wallet = CreateWallet.SingleSig(_internalWallet);
        var withdrawalRequest = new WalletWithdrawalRequest()
        {
            Id = 1,
            Status = WalletWithdrawalRequestStatus.Pending,
            Wallet = wallet,
            WalletWithdrawalRequestPSBTs = new List<WalletWithdrawalRequestPSBT>(),
            WalletWithdrawalRequestDestinations = new List<WalletWithdrawalRequestDestination>
            {
                new WalletWithdrawalRequestDestination
                {
                    Address = "bcrt1q8k3av6q5yp83rn332lx8a90k6kukhg28hs5qw7krdq95t629hgsqk6ztmf",
                    Amount = 0.01m
                }
            }
        };

        var walletWithdrawalRequestRepository = new Mock<IWalletWithdrawalRequestRepository>();
        var walletWithdrawalRequestPsbtRepository = new Mock<IWalletWithdrawalRequestPsbtRepository>();
        var fmutxoRepository = new Mock<IFMUTXORepository>();
        var nbXplorerService = new Mock<INBXplorerService>();
        var utxoTagRepository = new Mock<IUTXOTagRepository>();
        var mapper = new Mock<IMapper>();
        walletWithdrawalRequestRepository
            .Setup((w) => w.GetById(It.IsAny<int>()))
            .ReturnsAsync(withdrawalRequest);
        walletWithdrawalRequestRepository
            .Setup((w) => w.AddUTXOs(It.IsAny<WalletWithdrawalRequest>(), It.IsAny<List<FMUTXO>>()))
            .ReturnsAsync((true, null));
        walletWithdrawalRequestPsbtRepository
            .Setup((w) => w.AddAsync(It.IsAny<WalletWithdrawalRequestPSBT>()))
            .ReturnsAsync((true, null));
        nbXplorerService
            .Setup(x => x.GetStatusAsync(default))
            .ReturnsAsync(new StatusResult() { IsFullySynched = true });
        nbXplorerService
            .Setup(x => x.GetUnusedAsync(It.IsAny<DerivationStrategyBase>(), DerivationFeature.Change, 0, false, default))
            .ReturnsAsync(new KeyPathInformation() { Address = BitcoinAddress.Create("bcrt1q83ml8tve8vh672wsm83getxfzetaquq352jr6t423tdwjvdz3f3qe4r4t7", Network.RegTest) });
        nbXplorerService
            .Setup(x => x.GetUTXOsAsync(It.IsAny<DerivationStrategyBase>(), default))
            .ReturnsAsync(new UTXOChanges()
            {
                Confirmed = new UTXOChange()
                {
                    UTXOs = new List<UTXO>()
                    {
                        new UTXO()
                        {
                            Outpoint = new OutPoint(1234, 1),
                            Value = new Money((long)10000000),
                            ScriptPubKey = (wallet.GetDerivationStrategy() as StandardDerivationStrategyBase)!.GetDerivation(KeyPath.Parse("0/0")).ScriptPubKey,
                            KeyPath = KeyPath.Parse("0/0")
                        }
                    }
                }
            });
        fmutxoRepository
            .Setup(x => x.GetLockedUTXOs(null, null))
            .ReturnsAsync(new List<FMUTXO>());
        utxoTagRepository
            .SetupSequence(x => x.GetByKeyValue(It.IsAny<string>(), It.IsAny<string>()))
            .ReturnsAsync(new List<UTXOTag>()
            {
                new UTXOTag()
                {
                    Key = Constants.IsFrozenTag,
                    Value = "false",
                    Outpoint = "00000000000000000000000000000000000000000000000000000000000004d2-1"
                }
            })
            .ReturnsAsync(new List<UTXOTag>())
            .ReturnsAsync(new List<UTXOTag>()
            {
                new UTXOTag()
                {
                    Key = Constants.IsManuallyFrozenTag,
                    Value = "true",
                    Outpoint = "00000000000000000000000000000000000000000000000000000000000004d2-1"
                }
            });

        var coinSelectionService = new CoinSelectionService(_logger, mapper.Object, fmutxoRepository.Object, nbXplorerService.Object, null, walletWithdrawalRequestRepository.Object, utxoTagRepository.Object);

        var bitcoinService = new BitcoinService(_logger, mapper.Object, walletWithdrawalRequestRepository.Object, walletWithdrawalRequestPsbtRepository.Object, null, null, nbXplorerService.Object, coinSelectionService);

        // Act
        var result = await bitcoinService.GenerateTemplatePSBT(withdrawalRequest);

        // Assert
        var psbt = PSBT.Parse("cHNidP8BAIkBAAAAAdIEAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAQAAAAD9////AkBCDwAAAAAAIgAgPaPWaBQgTxHOMVfMfpX21blroUe8KAd6w2gLRelFuiCsUYkAAAAAACIAIDx3862ZOy+vKdDZ4oysyRZX0HARoqQ9LqqK2ukxoopiAAAAAE8BBDWHzwN9uUaNAAAAAYPR/OiA1LbTzxbLPvbXvtAwckIG3g+0T1zblR/ZodaiA5zBFsigPpL8htN/KJ/Ph8SPvQA/K+mSNXTSA0hgvPNuEO0CEMgwAACAAQAAgAEAAAAAAQEfgJaYAAAAAAAWABTpOvUBMqNMfl7P81etji6x4fXrMyIGA3uD9HVjgF5E+eQhHp+Na6femVYpc4bCA4DmimehAdWcGO0CEMgwAACAAQAAgAEAAAAAAAAAAAAAAAAAAA==", Network.RegTest);
        result.Should().BeEquivalentTo(psbt);
    }

    [Fact]
    async Task GenerateTemplatePSBT_SingleSigFailsManuallyFrozenUTXO()
    {
        // Arrange
        var wallet = CreateWallet.SingleSig(_internalWallet);
        var withdrawalRequest = new WalletWithdrawalRequest()
        {
            Id = 1,
            Status = WalletWithdrawalRequestStatus.Pending,
            Wallet = wallet,
            WalletWithdrawalRequestPSBTs = new List<WalletWithdrawalRequestPSBT>(),
            WalletWithdrawalRequestDestinations = new List<WalletWithdrawalRequestDestination>
            {
                new WalletWithdrawalRequestDestination
                {
                    Address = "bcrt1q8k3av6q5yp83rn332lx8a90k6kukhg28hs5qw7krdq95t629hgsqk6ztmf",
                    Amount = 0.01m
                }
            }
        };

        var walletWithdrawalRequestRepository = new Mock<IWalletWithdrawalRequestRepository>();
        var walletWithdrawalRequestPsbtRepository = new Mock<IWalletWithdrawalRequestPsbtRepository>();
        var fmutxoRepository = new Mock<IFMUTXORepository>();
        var nbXplorerService = new Mock<INBXplorerService>();
        var utxoTagRepository = new Mock<IUTXOTagRepository>();
        var mapper = new Mock<IMapper>();
        walletWithdrawalRequestRepository
            .Setup((w) => w.GetById(It.IsAny<int>()))
            .ReturnsAsync(withdrawalRequest);
        walletWithdrawalRequestRepository
            .Setup((w) => w.AddUTXOs(It.IsAny<WalletWithdrawalRequest>(), It.IsAny<List<FMUTXO>>()))
            .ReturnsAsync((true, null));
        walletWithdrawalRequestPsbtRepository
            .Setup((w) => w.AddAsync(It.IsAny<WalletWithdrawalRequestPSBT>()))
            .ReturnsAsync((true, null));
        nbXplorerService
            .Setup(x => x.GetStatusAsync(default))
            .ReturnsAsync(new StatusResult() { IsFullySynched = true });
        nbXplorerService
            .Setup(x => x.GetUnusedAsync(It.IsAny<DerivationStrategyBase>(), DerivationFeature.Change, 0, false, default))
            .ReturnsAsync(new KeyPathInformation() { Address = BitcoinAddress.Create("bcrt1q83ml8tve8vh672wsm83getxfzetaquq352jr6t423tdwjvdz3f3qe4r4t7", Network.RegTest) });
        nbXplorerService
            .Setup(x => x.GetUTXOsAsync(It.IsAny<DerivationStrategyBase>(), default))
            .ReturnsAsync(new UTXOChanges()
            {
                Confirmed = new UTXOChange()
                {
                    UTXOs = new List<UTXO>()
                    {
                        new UTXO()
                        {
                            Outpoint = new OutPoint(1234, 1),
                            Value = new Money((long)10000000),
                            ScriptPubKey = (wallet.GetDerivationStrategy() as StandardDerivationStrategyBase)!.GetDerivation(KeyPath.Parse("0/0")).ScriptPubKey,
                            KeyPath = KeyPath.Parse("0/0")
                        }
                    }
                }
            });
        fmutxoRepository
            .Setup(x => x.GetLockedUTXOs(null, null))
            .ReturnsAsync(new List<FMUTXO>());
        utxoTagRepository
            .SetupSequence(x => x.GetByKeyValue(It.IsAny<string>(), It.IsAny<string>()))
            .ReturnsAsync(new List<UTXOTag>()
            {
                new UTXOTag()
                {
                    Key = Constants.IsFrozenTag,
                    Value = "false",
                    Outpoint = "00000000000000000000000000000000000000000000000000000000000004d2-1"
                }
            })
            .ReturnsAsync(new List<UTXOTag>()
            {
                new UTXOTag()
                {
                    Key = Constants.IsManuallyFrozenTag,
                    Value = "true",
                    Outpoint = "00000000000000000000000000000000000000000000000000000000000004d2-1"
                }
            })
            .ReturnsAsync(new List<UTXOTag>());
        var coinSelectionService = new CoinSelectionService(_logger, mapper.Object, fmutxoRepository.Object, nbXplorerService.Object, null, walletWithdrawalRequestRepository.Object, utxoTagRepository.Object);

        var bitcoinService = new BitcoinService(_logger, mapper.Object, walletWithdrawalRequestRepository.Object, walletWithdrawalRequestPsbtRepository.Object, null, null, nbXplorerService.Object, coinSelectionService);

        // Act
        var act = () => bitcoinService.GenerateTemplatePSBT(withdrawalRequest);

        // Assert
        await act
            .Should()
            .ThrowAsync<NoUTXOsAvailableException>()
            .WithMessage("Exception of type 'NodeGuard.Helpers.NoUTXOsAvailableException' was thrown.");
    }

    [Fact]
    async Task GenerateTemplatePSBT_Changeless_SingleSigSucceeds()
    {
        // Arrange
        var wallet = CreateWallet.SingleSig(_internalWallet);
        var utxos = new List<UTXO>()
        {
            new UTXO()
            {
                Value = new Money((long)10000000),
                ScriptPubKey = (wallet.GetDerivationStrategy() as StandardDerivationStrategyBase)!.GetDerivation(KeyPath.Parse("0/0")).ScriptPubKey,
                KeyPath = KeyPath.Parse("0/0"),
                Index = 1,
                TransactionHash = 12345678901234567890,
                Outpoint = new OutPoint(12345678901234567890, 1)
            }
        };
        var withdrawalRequest = new WalletWithdrawalRequest()
        {
            Id = 1,
            Status = WalletWithdrawalRequestStatus.Pending,
            Wallet = wallet,
            WalletWithdrawalRequestPSBTs = new List<WalletWithdrawalRequestPSBT>(),
            WalletWithdrawalRequestDestinations = new List<WalletWithdrawalRequestDestination>
            {
                new WalletWithdrawalRequestDestination
                {
                    Address = "bcrt1q8k3av6q5yp83rn332lx8a90k6kukhg28hs5qw7krdq95t629hgsqk6ztmf",
                    Amount = 0.09m
                }
            },
            Changeless = true,
        };

        var walletWithdrawalRequestRepository = new Mock<IWalletWithdrawalRequestRepository>();
        var walletWithdrawalRequestPsbtRepository = new Mock<IWalletWithdrawalRequestPsbtRepository>();
        var fmutxoRepository = new Mock<IFMUTXORepository>();
        var utxoTagRepository = new Mock<IUTXOTagRepository>();
        var nbXplorerService = new Mock<INBXplorerService>();
        var mapper = new Mock<IMapper>();
        walletWithdrawalRequestRepository
            .Setup((w) => w.GetById(It.IsAny<int>()))
            .ReturnsAsync(withdrawalRequest);
        walletWithdrawalRequestRepository
            .Setup((w) => w.AddUTXOs(It.IsAny<WalletWithdrawalRequest>(), It.IsAny<List<FMUTXO>>()))
            .ReturnsAsync((true, null));
        walletWithdrawalRequestPsbtRepository
            .Setup((w) => w.AddAsync(It.IsAny<WalletWithdrawalRequestPSBT>()))
            .ReturnsAsync((true, null));
        nbXplorerService
            .Setup(x => x.GetStatusAsync(default))
            .ReturnsAsync(new StatusResult() { IsFullySynched = true });
        nbXplorerService
            .Setup(x => x.GetUnusedAsync(It.IsAny<DerivationStrategyBase>(), DerivationFeature.Change, 0, false, default))
            .ReturnsAsync(new KeyPathInformation() { Address = BitcoinAddress.Create("bcrt1q83ml8tve8vh672wsm83getxfzetaquq352jr6t423tdwjvdz3f3qe4r4t7", Network.RegTest) });
        utxoTagRepository
            .Setup(x => x.GetByKeyValue(It.IsAny<string>(), It.IsAny<string>()))
            .ReturnsAsync(new List<UTXOTag>());
        nbXplorerService
            .Setup(x => x.GetUTXOsAsync(It.IsAny<DerivationStrategyBase>(), default))
            .ReturnsAsync(new UTXOChanges()
            {
                Confirmed = new UTXOChange()
                {
                    UTXOs = utxos,
                }
            });

        var fmUtxos = utxos.Select(x => new FMUTXO() { TxId = x.Outpoint.Hash.ToString(), OutputIndex = 1 }).ToList();
        fmutxoRepository
            .Setup(x => x.GetLockedUTXOs(null, null))
            .ReturnsAsync(fmUtxos);

        walletWithdrawalRequestRepository
            .Setup(x => x.GetUTXOs(It.IsAny<IBitcoinRequest>()))
            .ReturnsAsync((true, fmUtxos));

        var coinSelectionService = new CoinSelectionService(_logger, mapper.Object, fmutxoRepository.Object, nbXplorerService.Object, null, walletWithdrawalRequestRepository.Object, utxoTagRepository.Object);

        var bitcoinService = new BitcoinService(_logger, mapper.Object, walletWithdrawalRequestRepository.Object, walletWithdrawalRequestPsbtRepository.Object, null, null, nbXplorerService.Object, coinSelectionService);

        // Act
        var result = await bitcoinService.GenerateTemplatePSBT(withdrawalRequest);

        // Assert
        var psbt = PSBT.Parse("cHNidP8BAF4BAAAAAdIKH+sAAAAAjKlUqwAAAAAAAAAAAAAAAAAAAAAAAAAAAQAAAAD9////AZiUmAAAAAAAIgAgPaPWaBQgTxHOMVfMfpX21blroUe8KAd6w2gLRelFuiAAAAAATwEENYfPA325Ro0AAAABg9H86IDUttPPFss+9te+0DByQgbeD7RPXNuVH9mh1qIDnMEWyKA+kvyG038on8+HxI+9AD8r6ZI1dNIDSGC8824Q7QIQyDAAAIABAACAAQAAAAABAR+AlpgAAAAAABYAFOk69QEyo0x+Xs/zV62OLrHh9eszIgYDe4P0dWOAXkT55CEen41rp96ZVilzhsIDgOaKZ6EB1ZwY7QIQyDAAAIABAACAAQAAAAAAAAAAAAAAAAA=", Network.RegTest);
        result.Should().BeEquivalentTo(psbt);
    }

    /// <summary>
    /// Builds the unsigned template PSBT for the same transaction a set of approver PSBTs signs. Signing does
    /// not alter the transaction, so the template shares its txid — which is exactly what PerformWithdrawal's
    /// binding check verifies before the internal wallet signs.
    /// </summary>
    private static string TemplateFor(string signedPsbtBase64)
    {
        var network = CurrentNetworkHelper.GetCurrentNetwork();
        var transaction = PSBT.Parse(signedPsbtBase64, network).GetGlobalTransaction();

        return PSBT.FromTransaction(transaction, network).ToBase64();
    }

    [Fact]
    async Task PerformWithdrawal_SingleSigSucceeds()
    {
        // Arrange
        var wallet = CreateWallet.SingleSig(_internalWallet);

        // Built rather than hardcoded. The previous literal spent a NULL outpoint (32 zero bytes with index
        // 0xFFFFFFFF), which NBitcoin classifies as a coinbase, so tx.Check() rejected it. That went unnoticed
        // while the result was merely logged; now that PerformWithdrawal refuses to broadcast a transaction
        // failing its own sanity check, the fixture has to be a transaction that could really exist.
        var network = CurrentNetworkHelper.GetCurrentNetwork();
        var walletScript = (wallet.GetDerivationStrategy() as StandardDerivationStrategyBase)!
            .GetDerivation(KeyPath.Parse("0/0")).ScriptPubKey;

        var fundingTx = network.CreateTransaction();
        fundingTx.Outputs.Add(new TxOut(Money.Coins(0.1m), walletScript));
        var walletCoin = new Coin(fundingTx, 0U);

        var spendTx = network.CreateTransaction();
        spendTx.Inputs.Add(new TxIn(walletCoin.Outpoint));
        spendTx.Outputs.Add(new TxOut(Money.Coins(0.01m),
            BitcoinAddress.Create("bcrt1q8k3av6q5yp83rn332lx8a90k6kukhg28hs5qw7krdq95t629hgsqk6ztmf", network)));

        var psbt = PSBT.FromTransaction(spendTx, network).AddCoins(walletCoin).ToBase64();
        var approvedTxId = spendTx.GetHash();
        var walletWithdrawalRequestPSBTs = new List<WalletWithdrawalRequestPSBT>()
        {
            new ()
            {
                IsFinalisedPSBT = false,
                IsInternalWalletPSBT = false,
                IsTemplatePSBT = true,
                PSBT = psbt,
            }
        };
        var withdrawalRequest = new WalletWithdrawalRequest()
        {
            Id = 1,
            Status = WalletWithdrawalRequestStatus.PSBTSignaturesPending,
            Wallet = wallet,
            WalletWithdrawalRequestPSBTs = walletWithdrawalRequestPSBTs,
            WalletWithdrawalRequestDestinations = new List<WalletWithdrawalRequestDestination>
            {
                new WalletWithdrawalRequestDestination
                {
                    Address = "bcrt1q8k3av6q5yp83rn332lx8a90k6kukhg28hs5qw7krdq95t629hgsqk6ztmf",
                    Amount = 0.01m
                }
            }
        };
        var node = new Node()
        {
            PubKey = "03485d8dcdd149c87553eeb80586eb2bece874d412e9f117304446ce189955d375",
            ChannelAdminMacaroon = "def",
            Endpoint = "10.0.0.2"
        };
        var walletWithdrawalRequestRepository = new Mock<IWalletWithdrawalRequestRepository>();
        var nodeRepository = new Mock<INodeRepository>();
        var nbXplorerService = new Mock<INBXplorerService>();
        walletWithdrawalRequestRepository
            .Setup((w) => w.GetById(It.IsAny<int>()))
            .ReturnsAsync(withdrawalRequest);
        walletWithdrawalRequestRepository
            .Setup((w) => w.Update(It.IsAny<WalletWithdrawalRequest>()))
            .Returns((true, null));
        nbXplorerService
            .Setup(x => x.GetUTXOsAsync(It.IsAny<DerivationStrategyBase>(), default))
            .ReturnsAsync(new UTXOChanges()
            {
                Confirmed = new UTXOChange()
                {
                    UTXOs = new List<UTXO>()
                    {
                        new UTXO()
                        {
                            // Must match the PSBT's input so the embedded signer finds the keypath.
                            Outpoint = walletCoin.Outpoint,
                            Value = walletCoin.Amount,
                            ScriptPubKey = walletScript,
                            KeyPath = KeyPath.Parse("0/0")
                        }
                    }
                }
            });
        nbXplorerService
            .Setup(x => x.BroadcastAsync(It.IsAny<Transaction>(), default, default))
            .ReturnsAsync(new BroadcastResult() { Success = true });
        nodeRepository
            .Setup(x => x.GetAllManagedByNodeGuard(It.IsAny<bool>()))
            .Returns(Task.FromResult(new List<Node>() { node }));
        var bitcoinService = new BitcoinService(_logger, null, walletWithdrawalRequestRepository.Object, null, nodeRepository.Object, null, nbXplorerService.Object, null);

        // Act
        var act = () => bitcoinService.PerformWithdrawal(withdrawalRequest);

        // Assert
        await act.Should().NotThrowAsync();

        // The transaction reaching the network must be the one that was approved.
        nbXplorerService.Verify(x => x.BroadcastAsync(
            It.Is<Transaction>(tx => tx.GetHash() == approvedTxId), default, default), Times.Once);
    }

    /// <summary>
    /// Pins the invariant that a request has AT MOST ONE template PSBT row.
    ///
    /// PerformWithdrawal's hot-wallet branch selects the template with .Single(x => x.IsTemplatePSBT), so a
    /// second tagged template makes every hot-wallet withdrawal throw. This matters because
    /// GenerateTemplatePSBT already persists the template itself: Withdrawals.razor and TransferFundsModal used
    /// to store a redundant second copy (untagged, which silently inflated the signature count), and "just tag
    /// it" would have broken this path instead. Both redundant stores were removed. If either is reintroduced
    /// as a tagged row, this test explains the failure.
    /// </summary>
    [Fact]
    async Task PerformWithdrawal_WithDuplicateTemplateRows_FailsLoudly()
    {
        // Arrange
        var wallet = CreateWallet.SingleSig(_internalWallet);
        var network = CurrentNetworkHelper.GetCurrentNetwork();
        var walletScript = (wallet.GetDerivationStrategy() as StandardDerivationStrategyBase)!
            .GetDerivation(KeyPath.Parse("0/0")).ScriptPubKey;

        var fundingTx = network.CreateTransaction();
        fundingTx.Outputs.Add(new TxOut(Money.Coins(0.1m), walletScript));
        var walletCoin = new Coin(fundingTx, 0U);

        var spendTx = network.CreateTransaction();
        spendTx.Inputs.Add(new TxIn(walletCoin.Outpoint));
        spendTx.Outputs.Add(new TxOut(Money.Coins(0.01m),
            BitcoinAddress.Create("bcrt1q8k3av6q5yp83rn332lx8a90k6kukhg28hs5qw7krdq95t629hgsqk6ztmf", network)));

        var templateBase64 = PSBT.FromTransaction(spendTx, network).AddCoins(walletCoin).ToBase64();

        var withdrawalRequest = new WalletWithdrawalRequest
        {
            Id = 1,
            Status = WalletWithdrawalRequestStatus.PSBTSignaturesPending,
            Wallet = wallet,
            WalletWithdrawalRequestPSBTs = new List<WalletWithdrawalRequestPSBT>
            {
                new() { IsTemplatePSBT = true, PSBT = templateBase64 },
                new() { IsTemplatePSBT = true, PSBT = templateBase64 }, // the duplicate
            },
            WalletWithdrawalRequestDestinations = new List<WalletWithdrawalRequestDestination>
            {
                new()
                {
                    Address = "bcrt1q8k3av6q5yp83rn332lx8a90k6kukhg28hs5qw7krdq95t629hgsqk6ztmf",
                    Amount = 0.01m,
                },
            },
        };

        var walletWithdrawalRequestRepository = new Mock<IWalletWithdrawalRequestRepository>();
        walletWithdrawalRequestRepository.Setup(x => x.GetById(It.IsAny<int>())).ReturnsAsync(withdrawalRequest);
        walletWithdrawalRequestRepository.Setup(x => x.Update(It.IsAny<WalletWithdrawalRequest>()))
            .Returns((true, null));

        var nbXplorerService = new Mock<INBXplorerService>();
        var bitcoinService = new BitcoinService(_logger, null, walletWithdrawalRequestRepository.Object, null,
            null, null, nbXplorerService.Object, null);

        // Act
        var act = () => bitcoinService.PerformWithdrawal(withdrawalRequest);

        // Assert
        await act.Should().ThrowAsync<Exception>(
            "two template rows must not be silently tolerated on the signing path");

        nbXplorerService.Verify(x => x.BroadcastAsync(It.IsAny<Transaction>(), default, default), Times.Never);
    }

    [Fact]
    async Task PerformWithdrawal_MultiSigSucceeds()
    {
        // Arrange
        var wallet = CreateWallet.MultiSig(_internalWallet);
        var psbt1 = "cHNidP8BAIkBAAAAATqZB6sbll4a0AJOf+RGbdqw07G/O9FatkFr+PDJAy+EAQAAAAD/////AkBCDwAAAAAAIgAgTlHqBosTtDYNNC59Qaz2968zru/mbl0l3tylEw+bKs2YTiZ3AAAAACIAINbjv3PBr8yjQit+5PSOCXdJgwfIoJ3Hv0HMD8+di5CSAAAAAE8BBDWHzwMvESQsgAAAAfw77kI6AYzrbSJqBmMojtD7XuD6nXkKs3DQMOBHMObIA4COLhzUgr3QcZaUPFqBM9Fpr4YCK2uwOBdxZE7AdETXEB/M5N4wAACAAQAAgAEAAIBPAQQ1h88DVqwD9IAAAAH5CK5KZrD/oasUtVrwzkjypwIly5AQkC1pAa+QuT6PgQJRrxXgW7i36sGJWz9fR//v7NgyGgLvIimPidCiA33wYBBg86CzMAAAgAEAAIABAACATwEENYfPA325Ro0AAAAAgN63GqLxTu1/NyL0SV4a0Hn1n8Dzg+Wye9nbb16ZISADr+s+pcKnDcSqKHKWSl4v8Rcq80ZqG/7QObYmZUl/xUYQ7QIQyDAAAIABAACAAAAAAAABASsAlDV3AAAAACIAIAF9guNzq1T08+t+DdFQoBYxMjvBQRTYuFmw2ppaQKvfIgIDHvfaz8S4WW4LqTCUmaadde52cCEeX0/qJryg6ukbY4ZHMEQCIHwm8KI69yEdHpCjsX3ifRyh8ZVVZC0/yKzXfRfL9tLfAiB5igcDqwiqCZHtgS0LO8uaJlX6bJrHOVX4KKePXBUtpQEBBWlSIQLQRFXTSgK8hussgvnt26CIeGhzduVmAI7NgraP64MFtiEDHvfaz8S4WW4LqTCUmaadde52cCEeX0/qJryg6ukbY4YhA/dSE/9TMSUTREqX5s2YWHSe8Obyw+HSZ+xuyVTUPMUmU64iBgLQRFXTSgK8hussgvnt26CIeGhzduVmAI7NgraP64MFthhg86CzMAAAgAEAAIABAACAAAAAAAsAAAAiBgMe99rPxLhZbgupMJSZpp117nZwIR5fT+omvKDq6RtjhhgfzOTeMAAAgAEAAIABAACAAAAAAAsAAAAiBgP3UhP/UzElE0RKl+bNmFh0nvDm8sPh0mfsbslU1DzFJhjtAhDIMAAAgAEAAIAAAAAAAAAAAAsAAAAAAAA=";
        var psbt2 = "cHNidP8BAIkBAAAAATqZB6sbll4a0AJOf+RGbdqw07G/O9FatkFr+PDJAy+EAQAAAAD/////AkBCDwAAAAAAIgAgTlHqBosTtDYNNC59Qaz2968zru/mbl0l3tylEw+bKs2YTiZ3AAAAACIAINbjv3PBr8yjQit+5PSOCXdJgwfIoJ3Hv0HMD8+di5CSAAAAAE8BBDWHzwMvESQsgAAAAfw77kI6AYzrbSJqBmMojtD7XuD6nXkKs3DQMOBHMObIA4COLhzUgr3QcZaUPFqBM9Fpr4YCK2uwOBdxZE7AdETXEB/M5N4wAACAAQAAgAEAAIBPAQQ1h88DVqwD9IAAAAH5CK5KZrD/oasUtVrwzkjypwIly5AQkC1pAa+QuT6PgQJRrxXgW7i36sGJWz9fR//v7NgyGgLvIimPidCiA33wYBBg86CzMAAAgAEAAIABAACATwEENYfPA325Ro0AAAAAgN63GqLxTu1/NyL0SV4a0Hn1n8Dzg+Wye9nbb16ZISADr+s+pcKnDcSqKHKWSl4v8Rcq80ZqG/7QObYmZUl/xUYQ7QIQyDAAAIABAACAAAAAAAABASsAlDV3AAAAACIAIAF9guNzq1T08+t+DdFQoBYxMjvBQRTYuFmw2ppaQKvfIgIC0ERV00oCvIbrLIL57dugiHhoc3blZgCOzYK2j+uDBbZHMEQCIF6mZdDgN+Q++oSO0lsvDYsTvCwxlwyGbvDAsDf8VV0RAiAKyQ9ZTd0JgB4rsSC+2aHdPjzWYU0BdeVGel8bDHwatAEBBWlSIQLQRFXTSgK8hussgvnt26CIeGhzduVmAI7NgraP64MFtiEDHvfaz8S4WW4LqTCUmaadde52cCEeX0/qJryg6ukbY4YhA/dSE/9TMSUTREqX5s2YWHSe8Obyw+HSZ+xuyVTUPMUmU64iBgLQRFXTSgK8hussgvnt26CIeGhzduVmAI7NgraP64MFthhg86CzMAAAgAEAAIABAACAAAAAAAsAAAAiBgMe99rPxLhZbgupMJSZpp117nZwIR5fT+omvKDq6RtjhhgfzOTeMAAAgAEAAIABAACAAAAAAAsAAAAiBgP3UhP/UzElE0RKl+bNmFh0nvDm8sPh0mfsbslU1DzFJhjtAhDIMAAAgAEAAIAAAAAAAAAAAAsAAAAAAAA=";
        var walletWithdrawalRequestPSBTs = new List<WalletWithdrawalRequestPSBT>()
        {
            // PerformWithdrawal verifies the combined PSBT still describes the approved transaction, so the
            // fixture must carry the template row that production always has — GenerateTemplatePSBT creates it
            // before approval is even possible.
            new ()
            {
                IsFinalisedPSBT = false,
                IsInternalWalletPSBT = false,
                IsTemplatePSBT = true,
                PSBT = TemplateFor(psbt1),
            },
            new ()
            {
                IsFinalisedPSBT = false,
                IsInternalWalletPSBT = false,
                IsTemplatePSBT = false,
                PSBT = psbt1,
            },
            new ()
            {
                IsFinalisedPSBT = false,
                IsInternalWalletPSBT = false,
                IsTemplatePSBT = false,
                PSBT = psbt2,
            }
        };
        var withdrawalRequest = new WalletWithdrawalRequest()
        {
            Id = 1,
            Status = WalletWithdrawalRequestStatus.PSBTSignaturesPending,
            Wallet = wallet,
            WalletWithdrawalRequestPSBTs = walletWithdrawalRequestPSBTs,
            WalletWithdrawalRequestDestinations = new List<WalletWithdrawalRequestDestination>
            {
                new WalletWithdrawalRequestDestination
                {
                    Address = "bcrt1q8k3av6q5yp83rn332lx8a90k6kukhg28hs5qw7krdq95t629hgsqk6ztmf",
                    Amount = 0.01m
                }
            }
        };
        var node = new Node()
        {
            PubKey = "03485d8dcdd149c87553eeb80586eb2bece874d412e9f117304446ce189955d375",
            ChannelAdminMacaroon = "def",
            Endpoint = "10.0.0.2"
        };
        var walletWithdrawalRequestRepository = new Mock<IWalletWithdrawalRequestRepository>();
        var nodeRepository = new Mock<INodeRepository>();
        var nbXplorerService = new Mock<INBXplorerService>();
        walletWithdrawalRequestRepository
            .Setup((w) => w.GetById(It.IsAny<int>()))
            .ReturnsAsync(withdrawalRequest);
        walletWithdrawalRequestRepository
            .Setup((w) => w.Update(It.IsAny<WalletWithdrawalRequest>()))
            .Returns((true, null));
        nbXplorerService
            .Setup(x => x.GetUTXOsAsync(It.IsAny<DerivationStrategyBase>(), default))
            .ReturnsAsync(new UTXOChanges()
            {
                Confirmed = new UTXOChange()
                {
                    UTXOs = new List<UTXO>()
                    {
                        new UTXO()
                        {
                            Value = new Money((long)10000000),
                            ScriptPubKey = (wallet.GetDerivationStrategy() as StandardDerivationStrategyBase)!.GetDerivation(KeyPath.Parse("0/0")).ScriptPubKey,
                            KeyPath = KeyPath.Parse("0/0")
                        }
                    }
                }
            });
        nbXplorerService
            .Setup(x => x.BroadcastAsync(It.IsAny<Transaction>(), default, default))
            .ReturnsAsync(new BroadcastResult() { Success = true });
        nodeRepository
            .Setup(x => x.GetAllManagedByNodeGuard(It.IsAny<bool>()))
            .Returns(Task.FromResult(new List<Node>() { node }));
        var bitcoinService = new BitcoinService(_logger, null, walletWithdrawalRequestRepository.Object, null, nodeRepository.Object, null, nbXplorerService.Object, null);

        // Act
        var act = () => bitcoinService.PerformWithdrawal(withdrawalRequest);

        // Assert
        await act.Should().NotThrowAsync();
    }

    [Fact]
    async Task PerformWithdrawal_LegacyMultiSigSucceeds()
    {
        // Arrange
        var wallet = CreateWallet.LegacyMultiSig(_internalWallet);
        var psbt1 = "cHNidP8BAIkBAAAAATqZB6sbll4a0AJOf+RGbdqw07G/O9FatkFr+PDJAy+EAQAAAAD/////AkBCDwAAAAAAIgAgTlHqBosTtDYNNC59Qaz2968zru/mbl0l3tylEw+bKs2YTiZ3AAAAACIAINbjv3PBr8yjQit+5PSOCXdJgwfIoJ3Hv0HMD8+di5CSAAAAAE8BBDWHzwMvESQsgAAAAfw77kI6AYzrbSJqBmMojtD7XuD6nXkKs3DQMOBHMObIA4COLhzUgr3QcZaUPFqBM9Fpr4YCK2uwOBdxZE7AdETXEB/M5N4wAACAAQAAgAEAAIBPAQQ1h88DVqwD9IAAAAH5CK5KZrD/oasUtVrwzkjypwIly5AQkC1pAa+QuT6PgQJRrxXgW7i36sGJWz9fR//v7NgyGgLvIimPidCiA33wYBBg86CzMAAAgAEAAIABAACATwEENYfPA325Ro0AAAAAgN63GqLxTu1/NyL0SV4a0Hn1n8Dzg+Wye9nbb16ZISADr+s+pcKnDcSqKHKWSl4v8Rcq80ZqG/7QObYmZUl/xUYQ7QIQyDAAAIABAACAAAAAAAABASsAlDV3AAAAACIAIAF9guNzq1T08+t+DdFQoBYxMjvBQRTYuFmw2ppaQKvfIgIDHvfaz8S4WW4LqTCUmaadde52cCEeX0/qJryg6ukbY4ZHMEQCIHwm8KI69yEdHpCjsX3ifRyh8ZVVZC0/yKzXfRfL9tLfAiB5igcDqwiqCZHtgS0LO8uaJlX6bJrHOVX4KKePXBUtpQEBBWlSIQLQRFXTSgK8hussgvnt26CIeGhzduVmAI7NgraP64MFtiEDHvfaz8S4WW4LqTCUmaadde52cCEeX0/qJryg6ukbY4YhA/dSE/9TMSUTREqX5s2YWHSe8Obyw+HSZ+xuyVTUPMUmU64iBgLQRFXTSgK8hussgvnt26CIeGhzduVmAI7NgraP64MFthhg86CzMAAAgAEAAIABAACAAAAAAAsAAAAiBgMe99rPxLhZbgupMJSZpp117nZwIR5fT+omvKDq6RtjhhgfzOTeMAAAgAEAAIABAACAAAAAAAsAAAAiBgP3UhP/UzElE0RKl+bNmFh0nvDm8sPh0mfsbslU1DzFJhjtAhDIMAAAgAEAAIAAAAAAAAAAAAsAAAAAAAA=";
        var psbt2 = "cHNidP8BAIkBAAAAATqZB6sbll4a0AJOf+RGbdqw07G/O9FatkFr+PDJAy+EAQAAAAD/////AkBCDwAAAAAAIgAgTlHqBosTtDYNNC59Qaz2968zru/mbl0l3tylEw+bKs2YTiZ3AAAAACIAINbjv3PBr8yjQit+5PSOCXdJgwfIoJ3Hv0HMD8+di5CSAAAAAE8BBDWHzwMvESQsgAAAAfw77kI6AYzrbSJqBmMojtD7XuD6nXkKs3DQMOBHMObIA4COLhzUgr3QcZaUPFqBM9Fpr4YCK2uwOBdxZE7AdETXEB/M5N4wAACAAQAAgAEAAIBPAQQ1h88DVqwD9IAAAAH5CK5KZrD/oasUtVrwzkjypwIly5AQkC1pAa+QuT6PgQJRrxXgW7i36sGJWz9fR//v7NgyGgLvIimPidCiA33wYBBg86CzMAAAgAEAAIABAACATwEENYfPA325Ro0AAAAAgN63GqLxTu1/NyL0SV4a0Hn1n8Dzg+Wye9nbb16ZISADr+s+pcKnDcSqKHKWSl4v8Rcq80ZqG/7QObYmZUl/xUYQ7QIQyDAAAIABAACAAAAAAAABASsAlDV3AAAAACIAIAF9guNzq1T08+t+DdFQoBYxMjvBQRTYuFmw2ppaQKvfIgIC0ERV00oCvIbrLIL57dugiHhoc3blZgCOzYK2j+uDBbZHMEQCIF6mZdDgN+Q++oSO0lsvDYsTvCwxlwyGbvDAsDf8VV0RAiAKyQ9ZTd0JgB4rsSC+2aHdPjzWYU0BdeVGel8bDHwatAEBBWlSIQLQRFXTSgK8hussgvnt26CIeGhzduVmAI7NgraP64MFtiEDHvfaz8S4WW4LqTCUmaadde52cCEeX0/qJryg6ukbY4YhA/dSE/9TMSUTREqX5s2YWHSe8Obyw+HSZ+xuyVTUPMUmU64iBgLQRFXTSgK8hussgvnt26CIeGhzduVmAI7NgraP64MFthhg86CzMAAAgAEAAIABAACAAAAAAAsAAAAiBgMe99rPxLhZbgupMJSZpp117nZwIR5fT+omvKDq6RtjhhgfzOTeMAAAgAEAAIABAACAAAAAAAsAAAAiBgP3UhP/UzElE0RKl+bNmFh0nvDm8sPh0mfsbslU1DzFJhjtAhDIMAAAgAEAAIAAAAAAAAAAAAsAAAAAAAA=";
        var walletWithdrawalRequestPSBTs = new List<WalletWithdrawalRequestPSBT>()
        {
            // PerformWithdrawal verifies the combined PSBT still describes the approved transaction, so the
            // fixture must carry the template row that production always has — GenerateTemplatePSBT creates it
            // before approval is even possible.
            new ()
            {
                IsFinalisedPSBT = false,
                IsInternalWalletPSBT = false,
                IsTemplatePSBT = true,
                PSBT = TemplateFor(psbt1),
            },
            new ()
            {
                IsFinalisedPSBT = false,
                IsInternalWalletPSBT = false,
                IsTemplatePSBT = false,
                PSBT = psbt1,
            },
            new ()
            {
                IsFinalisedPSBT = false,
                IsInternalWalletPSBT = false,
                IsTemplatePSBT = false,
                PSBT = psbt2,
            }
        };
        var withdrawalRequest = new WalletWithdrawalRequest()
        {
            Id = 1,
            Status = WalletWithdrawalRequestStatus.PSBTSignaturesPending,
            Wallet = wallet,
            WalletWithdrawalRequestPSBTs = walletWithdrawalRequestPSBTs,
            WalletWithdrawalRequestDestinations = new List<WalletWithdrawalRequestDestination>
            {
                new WalletWithdrawalRequestDestination
                {
                    Address = "bcrt1q8k3av6q5yp83rn332lx8a90k6kukhg28hs5qw7krdq95t629hgsqk6ztmf",
                    Amount = 0.01m
                }
            }
        };
        var node = new Node()
        {
            PubKey = "03485d8dcdd149c87553eeb80586eb2bece874d412e9f117304446ce189955d375",
            ChannelAdminMacaroon = "def",
            Endpoint = "10.0.0.2"
        };
        var walletWithdrawalRequestRepository = new Mock<IWalletWithdrawalRequestRepository>();
        var nodeRepository = new Mock<INodeRepository>();
        var nbXplorerService = new Mock<INBXplorerService>();
        walletWithdrawalRequestRepository
            .Setup((w) => w.GetById(It.IsAny<int>()))
            .ReturnsAsync(withdrawalRequest);
        walletWithdrawalRequestRepository
            .Setup((w) => w.Update(It.IsAny<WalletWithdrawalRequest>()))
            .Returns((true, null));
        nbXplorerService
            .Setup(x => x.GetUTXOsAsync(It.IsAny<DerivationStrategyBase>(), default))
            .ReturnsAsync(new UTXOChanges()
            {
                Confirmed = new UTXOChange()
                {
                    UTXOs = new List<UTXO>()
                    {
                        new UTXO()
                        {
                            Value = new Money((long)10000000),
                            ScriptPubKey = (wallet.GetDerivationStrategy() as StandardDerivationStrategyBase)!.GetDerivation(KeyPath.Parse("0/0")).ScriptPubKey,
                            KeyPath = KeyPath.Parse("0/0")
                        }
                    }
                }
            });
        nbXplorerService
            .Setup(x => x.BroadcastAsync(It.IsAny<Transaction>(), default, default))
            .ReturnsAsync(new BroadcastResult() { Success = true });
        nodeRepository
            .Setup(x => x.GetAllManagedByNodeGuard(It.IsAny<bool>()))
            .Returns(Task.FromResult(new List<Node>() { node }));
        var bitcoinService = new BitcoinService(_logger, null, walletWithdrawalRequestRepository.Object, null, nodeRepository.Object, null, nbXplorerService.Object, null);

        // Act
        var act = () => bitcoinService.PerformWithdrawal(withdrawalRequest);

        // Assert
        await act.Should().NotThrowAsync();
    }

    [Fact]
    async Task GenerateTemplatePSBT_MultipleDestinations_SingleSigSucceeds()
    {
        // Arrange
        var wallet = CreateWallet.SingleSig(_internalWallet);
        var withdrawalRequest = new WalletWithdrawalRequest()
        {
            Id = 1,
            Status = WalletWithdrawalRequestStatus.Pending,
            Wallet = wallet,
            WalletWithdrawalRequestPSBTs = new List<WalletWithdrawalRequestPSBT>(),
            WalletWithdrawalRequestDestinations = new List<WalletWithdrawalRequestDestination>
            {
                new WalletWithdrawalRequestDestination
                {
                    Address = "bcrt1qmde5y02qx2mywuzn05r50xkn9l6sv8h7646zyk",
                    Amount = 0.005m
                },
                new WalletWithdrawalRequestDestination
                {
                    Address = "bcrt1q9vzcaxm4xsq6p8rp8at7xsa2ehxncxdkdlrrwp",
                    Amount = 0.003m
                },
                new WalletWithdrawalRequestDestination
                {
                    Address = "bcrt1qpq9v4xhks7x5lgs7d54wzednkphan5uzqp6jw8",
                    Amount = 0.002m
                }
            }
        };

        var walletWithdrawalRequestRepository = new Mock<IWalletWithdrawalRequestRepository>();
        var walletWithdrawalRequestPsbtRepository = new Mock<IWalletWithdrawalRequestPsbtRepository>();
        var fmutxoRepository = new Mock<IFMUTXORepository>();
        var nbXplorerService = new Mock<INBXplorerService>();
        var utxoTagRepository = new Mock<IUTXOTagRepository>();
        var mapper = new Mock<IMapper>();
        walletWithdrawalRequestRepository
            .Setup((w) => w.GetById(It.IsAny<int>()))
            .ReturnsAsync(withdrawalRequest);
        walletWithdrawalRequestRepository
            .Setup((w) => w.AddUTXOs(It.IsAny<WalletWithdrawalRequest>(), It.IsAny<List<FMUTXO>>()))
            .ReturnsAsync((true, null));
        walletWithdrawalRequestPsbtRepository
            .Setup((w) => w.AddAsync(It.IsAny<WalletWithdrawalRequestPSBT>()))
            .ReturnsAsync((true, null));
        nbXplorerService
            .Setup(x => x.GetStatusAsync(default))
            .ReturnsAsync(new StatusResult() { IsFullySynched = true });
        nbXplorerService
            .Setup(x => x.GetUnusedAsync(It.IsAny<DerivationStrategyBase>(), DerivationFeature.Change, 0, false, default))
            .ReturnsAsync(new KeyPathInformation() { Address = BitcoinAddress.Create("bcrt1qhkvrjg9wa7h3sasl7260ehstwtcgq62a3udy5p", Network.RegTest) });
        nbXplorerService
            .Setup(x => x.GetUTXOsAsync(It.IsAny<DerivationStrategyBase>(), default))
            .ReturnsAsync(new UTXOChanges()
            {
                Confirmed = new UTXOChange()
                {
                    UTXOs = new List<UTXO>()
                    {
                        new UTXO()
                        {
                            Value = new Money((long)20000000), // 0.2 BTC - enough for multiple outputs plus fees
                            ScriptPubKey = (wallet.GetDerivationStrategy() as StandardDerivationStrategyBase)!.GetDerivation(KeyPath.Parse("0/0")).ScriptPubKey,
                            KeyPath = KeyPath.Parse("0/0")
                        }
                    }
                }
            });
        fmutxoRepository
            .Setup(x => x.GetLockedUTXOs(null, null))
            .ReturnsAsync(new List<FMUTXO>());
        utxoTagRepository
            .Setup(x => x.GetByKeyValue(It.IsAny<string>(), It.IsAny<string>()))
            .ReturnsAsync(new List<UTXOTag>());

        var coinSelectionService = new CoinSelectionService(_logger, mapper.Object, fmutxoRepository.Object, nbXplorerService.Object, null, walletWithdrawalRequestRepository.Object, utxoTagRepository.Object);

        var bitcoinService = new BitcoinService(_logger, mapper.Object, walletWithdrawalRequestRepository.Object, walletWithdrawalRequestPsbtRepository.Object, null, null, nbXplorerService.Object, coinSelectionService);

        // Act
        var result = await bitcoinService.GenerateTemplatePSBT(withdrawalRequest);

        // Assert
        result.Should().NotBeNull();

        // Verify that the PSBT has the expected number of outputs
        // 3 destination outputs + 1 change output = 4 total outputs
        result.Outputs.Count.Should().Be(4);

        // Verify destination amounts are present (order may vary due to shuffling)
        var outputValues = result.Outputs.Select(o => o.Value).ToList();
        outputValues.Should().Contain(new Money(0.005m, MoneyUnit.BTC));
        outputValues.Should().Contain(new Money(0.003m, MoneyUnit.BTC));
        outputValues.Should().Contain(new Money(0.002m, MoneyUnit.BTC));

        // Verify that there is a change output (should be greater than the destination amounts)
        var changeOutput = outputValues.Where(v => v > new Money(0.005m, MoneyUnit.BTC)).FirstOrDefault();
        changeOutput.Should().NotBeNull();
        changeOutput!.Satoshi.Should().BeGreaterThan(0);
    }


    [Fact]
    async Task GenerateTemplatePSBT_WithdrawAllFunds_SingleSigSucceeds()
    {
        // Arrange
        var wallet = CreateWallet.SingleSig(_internalWallet);
        var withdrawalRequest = new WalletWithdrawalRequest()
        {
            Id = 1,
            Status = WalletWithdrawalRequestStatus.Pending,
            Wallet = wallet,
            WithdrawAllFunds = true,
            WalletWithdrawalRequestPSBTs = new List<WalletWithdrawalRequestPSBT>(),
            WalletWithdrawalRequestDestinations = new List<WalletWithdrawalRequestDestination>
            {
                new WalletWithdrawalRequestDestination
                {
                    Address = "bcrt1q8k3av6q5yp83rn332lx8a90k6kukhg28hs5qw7krdq95t629hgsqk6ztmf",
                    Amount = 0m // Will be set to full balance
                }
            }
        };

        var walletWithdrawalRequestRepository = new Mock<IWalletWithdrawalRequestRepository>();
        var walletWithdrawalRequestPsbtRepository = new Mock<IWalletWithdrawalRequestPsbtRepository>();
        var fmutxoRepository = new Mock<IFMUTXORepository>();
        var nbXplorerService = new Mock<INBXplorerService>();
        var utxoTagRepository = new Mock<IUTXOTagRepository>();
        var mapper = new Mock<IMapper>();

        walletWithdrawalRequestRepository
            .Setup((w) => w.GetById(It.IsAny<int>()))
            .ReturnsAsync(withdrawalRequest);
        walletWithdrawalRequestRepository
            .Setup((w) => w.Update(It.IsAny<WalletWithdrawalRequest>()))
            .Returns((true, null));
        walletWithdrawalRequestRepository
            .Setup((w) => w.AddUTXOs(It.IsAny<WalletWithdrawalRequest>(), It.IsAny<List<FMUTXO>>()))
            .ReturnsAsync((true, null));
        walletWithdrawalRequestPsbtRepository
            .Setup((w) => w.AddAsync(It.IsAny<WalletWithdrawalRequestPSBT>()))
            .ReturnsAsync((true, null));
        nbXplorerService
            .Setup(x => x.GetStatusAsync(default))
            .ReturnsAsync(new StatusResult() { IsFullySynched = true });
        nbXplorerService
            .Setup(x => x.GetBalanceAsync(It.IsAny<DerivationStrategyBase>(), default))
            .ReturnsAsync(new GetBalanceResponse() { Confirmed = new Money((long)50000000) }); // 0.5 BTC
        nbXplorerService
            .Setup(x => x.GetUnusedAsync(It.IsAny<DerivationStrategyBase>(), DerivationFeature.Change, 0, false, default))
            .ReturnsAsync(new KeyPathInformation() { Address = BitcoinAddress.Create("bcrt1q83ml8tve8vh672wsm83getxfzetaquq352jr6t423tdwjvdz3f3qe4r4t7", Network.RegTest) });
        nbXplorerService
            .Setup(x => x.GetUTXOsAsync(It.IsAny<DerivationStrategyBase>(), default))
            .ReturnsAsync(new UTXOChanges()
            {
                Confirmed = new UTXOChange()
                {
                    UTXOs = new List<UTXO>()
                    {
                        new UTXO()
                        {
                            Value = new Money((long)50000000),
                            ScriptPubKey = (wallet.GetDerivationStrategy() as StandardDerivationStrategyBase)!.GetDerivation(KeyPath.Parse("0/0")).ScriptPubKey,
                            KeyPath = KeyPath.Parse("0/0")
                        }
                    }
                }
            });
        fmutxoRepository
            .Setup(x => x.GetLockedUTXOs(null, null))
            .ReturnsAsync(new List<FMUTXO>());
        utxoTagRepository
            .Setup(x => x.GetByKeyValue(It.IsAny<string>(), It.IsAny<string>()))
            .ReturnsAsync(new List<UTXOTag>());

        var coinSelectionService = new CoinSelectionService(_logger, mapper.Object, fmutxoRepository.Object, nbXplorerService.Object, null, walletWithdrawalRequestRepository.Object, utxoTagRepository.Object);
        var bitcoinService = new BitcoinService(_logger, mapper.Object, walletWithdrawalRequestRepository.Object, walletWithdrawalRequestPsbtRepository.Object, null, null, nbXplorerService.Object, coinSelectionService);

        // Act
        var result = await bitcoinService.GenerateTemplatePSBT(withdrawalRequest);

        // Assert
        result.Should().NotBeNull();
        // The destination amount should have been updated to the full balance
        withdrawalRequest.WalletWithdrawalRequestDestinations.First().Amount.Should().Be(0.5m);
        // For withdraw all funds, verify that all available funds are being sent
        result.Outputs.Should().HaveCountGreaterThanOrEqualTo(1);
    }

    [Fact]
    async Task GenerateTemplatePSBT_WithdrawAllFunds_MultipleDestinations_ShouldFail()
    {
        // Arrange
        var wallet = CreateWallet.SingleSig(_internalWallet);
        var withdrawalRequest = new WalletWithdrawalRequest()
        {
            Id = 1,
            Status = WalletWithdrawalRequestStatus.Pending,
            Wallet = wallet,
            WithdrawAllFunds = true,
            WalletWithdrawalRequestPSBTs = new List<WalletWithdrawalRequestPSBT>(),
            WalletWithdrawalRequestDestinations = new List<WalletWithdrawalRequestDestination>
            {
                new WalletWithdrawalRequestDestination
                {
                    Address = "bcrt1q8k3av6q5yp83rn332lx8a90k6kukhg28hs5qw7krdq95t629hgsqk6ztmf",
                    Amount = 0m
                },
                new WalletWithdrawalRequestDestination
                {
                    Address = "bcrt1qxy2kgdygjrsqtzq2n0yrf2493p83kkfjhx0wlh",
                    Amount = 0m
                }
            }
        };

        var walletWithdrawalRequestRepository = new Mock<IWalletWithdrawalRequestRepository>();
        var walletWithdrawalRequestPsbtRepository = new Mock<IWalletWithdrawalRequestPsbtRepository>();
        var fmutxoRepository = new Mock<IFMUTXORepository>();
        var nbXplorerService = new Mock<INBXplorerService>();
        var utxoTagRepository = new Mock<IUTXOTagRepository>();
        var mapper = new Mock<IMapper>();

        walletWithdrawalRequestRepository
            .Setup((w) => w.GetById(It.IsAny<int>()))
            .ReturnsAsync(withdrawalRequest);
        walletWithdrawalRequestRepository
            .Setup((w) => w.Update(It.IsAny<WalletWithdrawalRequest>()))
            .Returns((true, null));
        walletWithdrawalRequestRepository
            .Setup((w) => w.AddUTXOs(It.IsAny<WalletWithdrawalRequest>(), It.IsAny<List<FMUTXO>>()))
            .ReturnsAsync((true, null));
        walletWithdrawalRequestPsbtRepository
            .Setup((w) => w.AddAsync(It.IsAny<WalletWithdrawalRequestPSBT>()))
            .ReturnsAsync((true, null));
        nbXplorerService
            .Setup(x => x.GetStatusAsync(default))
            .ReturnsAsync(new StatusResult() { IsFullySynched = true });
        nbXplorerService
            .Setup(x => x.GetBalanceAsync(It.IsAny<DerivationStrategyBase>(), default))
            .ReturnsAsync(new GetBalanceResponse() { Confirmed = new Money((long)50000000) });
        nbXplorerService
            .Setup(x => x.GetUnusedAsync(It.IsAny<DerivationStrategyBase>(), DerivationFeature.Change, 0, false, default))
            .ReturnsAsync(new KeyPathInformation() { Address = BitcoinAddress.Create("bcrt1q83ml8tve8vh672wsm83getxfzetaquq352jr6t423tdwjvdz3f3qe4r4t7", Network.RegTest) });
        nbXplorerService
            .Setup(x => x.GetUTXOsAsync(It.IsAny<DerivationStrategyBase>(), default))
            .ReturnsAsync(new UTXOChanges()
            {
                Confirmed = new UTXOChange()
                {
                    UTXOs = new List<UTXO>()
                    {
                        new UTXO()
                        {
                            Value = new Money((long)50000000),
                            ScriptPubKey = (wallet.GetDerivationStrategy() as StandardDerivationStrategyBase)!.GetDerivation(KeyPath.Parse("0/0")).ScriptPubKey,
                            KeyPath = KeyPath.Parse("0/0")
                        }
                    }
                }
            });
        fmutxoRepository
            .Setup(x => x.GetLockedUTXOs(null, null))
            .ReturnsAsync(new List<FMUTXO>());
        utxoTagRepository
            .Setup(x => x.GetByKeyValue(It.IsAny<string>(), It.IsAny<string>()))
            .ReturnsAsync(new List<UTXOTag>());

        var coinSelectionService = new CoinSelectionService(_logger, mapper.Object, fmutxoRepository.Object, nbXplorerService.Object, null, walletWithdrawalRequestRepository.Object, utxoTagRepository.Object);
        var bitcoinService = new BitcoinService(_logger, mapper.Object, walletWithdrawalRequestRepository.Object, walletWithdrawalRequestPsbtRepository.Object, null, null, nbXplorerService.Object, coinSelectionService);

        // Act
        var act = () => bitcoinService.GenerateTemplatePSBT(withdrawalRequest);

        // Assert
        await act
            .Should()
            .ThrowAsync<ArgumentException>()
            .WithMessage("Withdraw all funds can only have one destination address.");
    }

    [Fact]
    async Task GenerateTemplatePSBT_Changeless_MultipleDestinations_ShouldFail()
    {
        // Arrange
        var wallet = CreateWallet.SingleSig(_internalWallet);
        var withdrawalRequest = new WalletWithdrawalRequest()
        {
            Id = 1,
            Status = WalletWithdrawalRequestStatus.Pending,
            Wallet = wallet,
            Changeless = true,
            WalletWithdrawalRequestPSBTs = new List<WalletWithdrawalRequestPSBT>(),
            WalletWithdrawalRequestDestinations = new List<WalletWithdrawalRequestDestination>
            {
                new WalletWithdrawalRequestDestination
                {
                    Address = "bcrt1q8k3av6q5yp83rn332lx8a90k6kukhg28hs5qw7krdq95t629hgsqk6ztmf",
                    Amount = 0.01m
                },
                new WalletWithdrawalRequestDestination
                {
                    Address = "bcrt1qxy2kgdygjrsqtzq2n0yrf2493p83kkfjhx0wlh",
                    Amount = 0.005m
                }
            }
        };

        var walletWithdrawalRequestRepository = new Mock<IWalletWithdrawalRequestRepository>();
        var walletWithdrawalRequestPsbtRepository = new Mock<IWalletWithdrawalRequestPsbtRepository>();
        var fmutxoRepository = new Mock<IFMUTXORepository>();
        var nbXplorerService = new Mock<INBXplorerService>();
        var utxoTagRepository = new Mock<IUTXOTagRepository>();
        var mapper = new Mock<IMapper>();

        walletWithdrawalRequestRepository
            .Setup((w) => w.GetById(It.IsAny<int>()))
            .ReturnsAsync(withdrawalRequest);
        walletWithdrawalRequestRepository
            .Setup((w) => w.AddUTXOs(It.IsAny<WalletWithdrawalRequest>(), It.IsAny<List<FMUTXO>>()))
            .ReturnsAsync((true, null));
        walletWithdrawalRequestPsbtRepository
            .Setup((w) => w.AddAsync(It.IsAny<WalletWithdrawalRequestPSBT>()))
            .ReturnsAsync((true, null));
        nbXplorerService
            .Setup(x => x.GetStatusAsync(default))
            .ReturnsAsync(new StatusResult() { IsFullySynched = true });
        nbXplorerService
            .Setup(x => x.GetUnusedAsync(It.IsAny<DerivationStrategyBase>(), DerivationFeature.Change, 0, false, default))
            .ReturnsAsync(new KeyPathInformation() { Address = BitcoinAddress.Create("bcrt1q83ml8tve8vh672wsm83getxfzetaquq352jr6t423tdwjvdz3f3qe4r4t7", Network.RegTest) });
        nbXplorerService
            .Setup(x => x.GetUTXOsAsync(It.IsAny<DerivationStrategyBase>(), default))
            .ReturnsAsync(new UTXOChanges()
            {
                Confirmed = new UTXOChange()
                {
                    UTXOs = new List<UTXO>()
                    {
                        new UTXO()
                        {
                            Value = new Money((long)20000000),
                            ScriptPubKey = (wallet.GetDerivationStrategy() as StandardDerivationStrategyBase)!.GetDerivation(KeyPath.Parse("0/0")).ScriptPubKey,
                            KeyPath = KeyPath.Parse("0/0")
                        }
                    }
                }
            });
        fmutxoRepository
            .Setup(x => x.GetLockedUTXOs(null, null))
            .ReturnsAsync(new List<FMUTXO>());
        utxoTagRepository
            .Setup(x => x.GetByKeyValue(It.IsAny<string>(), It.IsAny<string>()))
            .ReturnsAsync(new List<UTXOTag>());

        // Mock to return UTXOs for changeless validation
        walletWithdrawalRequestRepository
            .Setup(x => x.GetUTXOs(It.IsAny<WalletWithdrawalRequest>()))
            .ReturnsAsync((true, new List<FMUTXO>
            {
                new FMUTXO
                {
                    TxId = "abc123",
                    OutputIndex = 0,
                    SatsAmount = 20000000 // 0.2 BTC
                }
            }));

        var coinSelectionService = new CoinSelectionService(_logger, mapper.Object, fmutxoRepository.Object, nbXplorerService.Object, null, walletWithdrawalRequestRepository.Object, utxoTagRepository.Object);
        var bitcoinService = new BitcoinService(_logger, mapper.Object, walletWithdrawalRequestRepository.Object, walletWithdrawalRequestPsbtRepository.Object, null, null, nbXplorerService.Object, coinSelectionService);

        // Act
        var act = () => bitcoinService.GenerateTemplatePSBT(withdrawalRequest);

        // Assert
        await act
            .Should()
            .ThrowAsync<ArgumentException>()
            .WithMessage("Changeless transactions can only have one destination address.");
    }

    [Fact]
    async Task GenerateTemplatePSBT_ReuseTemplatePSBT_WhenUTXOsStillValid()
    {
        // Arrange
        var wallet = CreateWallet.SingleSig(_internalWallet);
        var existingTemplatePSBT = "cHNidP8BAIkBAAAAAQAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA/////wD/////AkBCDwAAAAAAIgAgPaPWaBQgTxHOMVfMfpX21blroUe8KAd6w2gLRelFuiCsUYkAAAAAACIAIDx3862ZOy+vKdDZ4oysyRZX0HARoqQ9LqqK2ukxoopiAAAAAE8BBDWHzwN9uUaNAAAAAYPR/OiA1LbTzxbLPvbXvtAwckIG3g+0T1zblR/ZodaiA5zBFsigPpL8htN/KJ/Ph8SPvQA/K+mSNXTSA0hgvPNuEO0CEMgwAACAAQAAgAEAAAAAAQEfgJaYAAAAAAAWABTpOvUBMqNMfl7P81etji6x4fXrMyIGA3uD9HVjgF5E+eQhHp+Na6femVYpc4bCA4DmimehAdWcGO0CEMgwAACAAQAAgAEAAAAAAAAAAAAAAAAAAA==";
        var withdrawalRequest = new WalletWithdrawalRequest()
        {
            Id = 1,
            Status = WalletWithdrawalRequestStatus.Pending,
            Wallet = wallet,
            WalletWithdrawalRequestPSBTs = new List<WalletWithdrawalRequestPSBT>
            {
                new WalletWithdrawalRequestPSBT
                {
                    Id = 1,
                    IsTemplatePSBT = true,
                    PSBT = existingTemplatePSBT
                }
            },
            WalletWithdrawalRequestDestinations = new List<WalletWithdrawalRequestDestination>
            {
                new WalletWithdrawalRequestDestination
                {
                    Address = "bcrt1q8k3av6q5yp83rn332lx8a90k6kukhg28hs5qw7krdq95t629hgsqk6ztmf",
                    Amount = 0.01m
                }
            }
        };

        var walletWithdrawalRequestRepository = new Mock<IWalletWithdrawalRequestRepository>();
        var nbXplorerService = new Mock<INBXplorerService>();

        walletWithdrawalRequestRepository
            .Setup((w) => w.GetById(It.IsAny<int>()))
            .ReturnsAsync(withdrawalRequest);
        nbXplorerService
            .Setup(x => x.GetStatusAsync(default))
            .ReturnsAsync(new StatusResult() { IsFullySynched = true });
        nbXplorerService
            .Setup(x => x.GetUTXOsAsync(It.IsAny<DerivationStrategyBase>(), default))
            .ReturnsAsync(new UTXOChanges()
            {
                Confirmed = new UTXOChange()
                {
                    UTXOs = new List<UTXO>()
                    {
                        new UTXO()
                        {
                            Outpoint = new OutPoint(), // This matches the PSBT input
                            Value = new Money((long)10000000),
                            ScriptPubKey = (wallet.GetDerivationStrategy() as StandardDerivationStrategyBase)!.GetDerivation(KeyPath.Parse("0/0")).ScriptPubKey,
                            KeyPath = KeyPath.Parse("0/0")
                        }
                    }
                }
            });

        var bitcoinService = new BitcoinService(_logger, null, walletWithdrawalRequestRepository.Object, null, null, null, nbXplorerService.Object, null);

        // Act
        var result = await bitcoinService.GenerateTemplatePSBT(withdrawalRequest);

        // Assert
        result.Should().NotBeNull();
        result.ToBase64().Should().Be(existingTemplatePSBT); // Should return the existing template PSBT
    }

    /// <summary>
    /// Precondition for <see cref="PerformWithdrawal_DoesNotCoSignOrBroadcastASubstitutedTransaction"/>:
    /// on a wallet where NodeGuard is a required co-signer (Keys.Count == MofN, here a 2-of-2), a single
    /// human approval already satisfies the threshold, so PerformWithdrawal proceeds to have the internal
    /// wallet sign. That is precisely why the PSBT it signs must be bound to the approved template.
    /// </summary>
    [Fact]
    public void PerformWithdrawal_2Of2_SingleHumanApprovalTriggersInternalCoSigning()
    {
        var fixture = new SubstitutionFixture();

        fixture.Wallet.RequiresInternalWalletSigning.Should().BeTrue(
            "the fixture must be a wallet where NodeGuard is a required co-signer (Keys.Count == MofN)");

        var request = fixture.BuildRequest(fixture.AttackerApprovalBase64);

        request.NumberOfSignaturesCollected.Should().Be(1, "the fixture stores exactly one human approval");
        request.AreAllRequiredHumanSignaturesCollected.Should().BeTrue(
            "one human signature plus NodeGuard's own is the whole 2-of-2");
    }

    /// <summary>
    /// NodeGuard's internal wallet must not co-sign or broadcast a transaction that differs from the
    /// withdrawal request's approved template. The only human approval here is a valid signature over a
    /// DIFFERENT transaction paying an attacker-controlled address; PerformWithdrawal must refuse it and
    /// broadcast nothing, so a lone keyholder cannot redirect funds by substituting the transaction.
    /// </summary>
    [Fact]
    public async Task PerformWithdrawal_DoesNotCoSignOrBroadcastASubstitutedTransaction()
    {
        var fixture = new SubstitutionFixture();
        var request = fixture.BuildRequest(fixture.AttackerApprovalBase64);

        var walletWithdrawalRequestRepository = new Mock<IWalletWithdrawalRequestRepository>();
        walletWithdrawalRequestRepository.Setup(x => x.GetById(It.IsAny<int>())).ReturnsAsync(request);
        walletWithdrawalRequestRepository.Setup(x => x.Update(It.IsAny<WalletWithdrawalRequest>()))
            .Returns((true, null));

        var nbXplorerService = new Mock<INBXplorerService>();
        nbXplorerService.Setup(x => x.GetUTXOsAsync(It.IsAny<DerivationStrategyBase>(), default))
            .ReturnsAsync(fixture.BuildUtxoChanges());

        Transaction? broadcast = null;
        nbXplorerService.Setup(x => x.BroadcastAsync(It.IsAny<Transaction>(), default, default))
            .Callback<Transaction, bool, CancellationToken>((tx, _, _) => broadcast = tx)
            .ReturnsAsync(new BroadcastResult { Success = true });

        var nodeRepository = new Mock<INodeRepository>();
        nodeRepository.Setup(x => x.GetAllManagedByNodeGuard(It.IsAny<bool>()))
            .ReturnsAsync(new List<Node> { new() { PubKey = "02" + new string('a', 64), Name = "test-node" } });

        var bitcoinService = new BitcoinService(_logger, null,
            walletWithdrawalRequestRepository.Object, null, nodeRepository.Object, null,
            nbXplorerService.Object, null);

        // Act
        var act = () => bitcoinService.PerformWithdrawal(request);

        // Assert — the substitution is refused before signing, and nothing is broadcast.
        await act.Should().ThrowAsync<Exception>();
        broadcast.Should().BeNull(
            "a withdrawal whose only approval describes a different transaction must never reach broadcast");
    }

    /// <summary>
    /// A cold 2-of-2 wallet: one human key (whose seed the substitution uses) plus NodeGuard's internal
    /// co-signing key, funded with a single UTXO. Produces the approved template plus the substitute — a
    /// valid signature over a different transaction paying a different destination.
    /// </summary>
    private sealed class SubstitutionFixture
    {
        private const string HumanSeed =
            "social mango annual basic work brain economy one safe physical junk other toy valid load cook napkin maple runway island oil fan legend stem";

        private static readonly Network Network = Network.RegTest;

        internal Wallet Wallet { get; }
        internal BitcoinAddress ApprovedAddress { get; }
        internal BitcoinAddress AttackerAddress { get; }
        internal Money ApprovedAmount { get; } = Money.Coins(0.01m);
        internal Money AttackerAmount { get; } = Money.Coins(0.09m);
        internal string TemplateBase64 { get; }
        internal string AttackerApprovalBase64 { get; }

        private readonly ScriptCoin _coin;
        private readonly KeyPath _utxoKeyPath = KeyPath.Parse("0/0");
        private readonly Script _walletScript;

        internal SubstitutionFixture()
        {
            var internalWallet = CreateWallet.CreateInternalWallet();
            var humanKey = CreateWallet.CreateUserKey("human key", "human-user", HumanSeed);

            // Replica of CreateWallet.CreateInternalKey, which is private.
            var internalKey = new Key
            {
                Name = "NodeGuard Co-signing Key",
                XPUB = internalWallet.GetXpubForAccount("0"),
                InternalWalletId = internalWallet.Id,
                Path = internalWallet.GetKeyPathForAccount("0"),
                MasterFingerprint = internalWallet.MasterFingerprint,
            };

            // Keys.Count == MofN  =>  RequiresInternalWalletSigning is true.
            Wallet = new Wallet
            {
                Id = 1,
                MofN = 2,
                Keys = new List<Key> { humanKey, internalKey },
                Name = "2-of-2 wallet",
                WalletAddressType = WalletAddressType.NativeSegwit,
                InternalWallet = internalWallet,
                InternalWalletId = internalWallet.Id,
                IsFinalised = true,
                InternalWalletSubDerivationPath = "0",
                InternalWalletMasterFingerprint = internalWallet.MasterFingerprint,
            };

            var derivation = (Wallet.GetDerivationStrategy() as StandardDerivationStrategyBase)!
                .GetDerivation(_utxoKeyPath);
            _walletScript = derivation.ScriptPubKey;

            var funding = Network.CreateTransaction();
            funding.Outputs.Add(new TxOut(Money.Coins(0.1m), _walletScript));
            _coin = new Coin(funding, 0U).ToScriptCoin(derivation.Redeem);

            ApprovedAddress = new NBitcoin.Key().PubKey.GetAddress(ScriptPubKeyType.Segwit, Network);
            AttackerAddress = new NBitcoin.Key().PubKey.GetAddress(ScriptPubKeyType.Segwit, Network);

            TemplateBase64 = ToPsbt(Spend(ApprovedAddress, ApprovedAmount)).ToBase64();

            // A valid signature over a different transaction, made with the key the signer legitimately holds.
            var attackerPsbt = ToPsbt(Spend(AttackerAddress, AttackerAmount));
            attackerPsbt.SignWithKeys(DeriveHumanPrivateKey(humanKey));
            AttackerApprovalBase64 = attackerPsbt.ToBase64();
        }

        private Transaction Spend(BitcoinAddress destination, Money amount)
        {
            var tx = Network.CreateTransaction();
            tx.Inputs.Add(new TxIn(_coin.Outpoint));
            tx.Outputs.Add(new TxOut(amount, destination.ScriptPubKey));
            return tx;
        }

        private PSBT ToPsbt(Transaction tx) => PSBT.FromTransaction(tx, Network).AddCoins(_coin);

        // Mirrors Wallet.DeriveUtxoPrivateKey for the human key: master -> account path -> utxo path.
        private NBitcoin.Key DeriveHumanPrivateKey(Key humanKey)
            => new Mnemonic(HumanSeed)
                .DeriveExtKey()
                .GetWif(Network)
                .Derive(KeyPath.Parse(humanKey.Path!))
                .Derive(_utxoKeyPath)
                .PrivateKey;

        internal WalletWithdrawalRequest BuildRequest(string humanApprovalBase64) => new()
        {
            Id = 1,
            Status = WalletWithdrawalRequestStatus.PSBTSignaturesPending,
            Wallet = Wallet,
            WalletId = Wallet.Id,
            WalletWithdrawalRequestPSBTs = new List<WalletWithdrawalRequestPSBT>
            {
                new() { IsTemplatePSBT = true, PSBT = TemplateBase64 },
                new() { IsTemplatePSBT = false, PSBT = humanApprovalBase64, SignerId = "human-user" },
            },
            WalletWithdrawalRequestDestinations = new List<WalletWithdrawalRequestDestination>
            {
                new() { Address = ApprovedAddress.ToString(), Amount = ApprovedAmount.ToUnit(MoneyUnit.BTC) },
            },
        };

        internal UTXOChanges BuildUtxoChanges() => new()
        {
            Confirmed = new UTXOChange
            {
                UTXOs = new List<UTXO>
                {
                    new()
                    {
                        Outpoint = _coin.Outpoint,
                        Value = _coin.Amount,
                        ScriptPubKey = _walletScript,
                        KeyPath = _utxoKeyPath,
                    },
                },
            },
        };
    }

    /// <summary>
    /// Minimal fixture for the coin selection branch in GenerateTemplatePSBT. The mocked selection
    /// service returns nothing, so the call throws once the branch has been taken, which is all these
    /// tests need to see.
    /// </summary>
    private static (BitcoinService service, WalletWithdrawalRequest request, Mock<ICoinSelectionService> coinSelectionService)
        CreateServiceForSelectionBranch(ILogger<BitcoinService> logger, InternalWallet internalWallet,
            bool withdrawAllFunds = false, List<UTXO>? plainListingUtxos = null)
    {
        var withdrawalRequest = new WalletWithdrawalRequest()
        {
            Id = 1,
            Status = WalletWithdrawalRequestStatus.Pending,
            Wallet = CreateWallet.SingleSig(internalWallet),
            WithdrawAllFunds = withdrawAllFunds,
            WalletWithdrawalRequestPSBTs = new List<WalletWithdrawalRequestPSBT>(),
            WalletWithdrawalRequestDestinations = new List<WalletWithdrawalRequestDestination>
            {
                new()
                {
                    Address = "bcrt1q8k3av6q5yp83rn332lx8a90k6kukhg28hs5qw7krdq95t629hgsqk6ztmf",
                    Amount = 0.01m
                }
            }
        };

        var walletWithdrawalRequestRepository = new Mock<IWalletWithdrawalRequestRepository>();
        walletWithdrawalRequestRepository.Setup(x => x.GetById(It.IsAny<int>())).ReturnsAsync(withdrawalRequest);
        walletWithdrawalRequestRepository.Setup(x => x.Update(It.IsAny<WalletWithdrawalRequest>())).Returns((true, null));

        var nbXplorerService = new Mock<INBXplorerService>();
        nbXplorerService.Setup(x => x.GetStatusAsync(default))
            .ReturnsAsync(new StatusResult() { IsFullySynched = true });

        var coinSelectionService = new Mock<ICoinSelectionService>();
        coinSelectionService
            .Setup(x => x.GetLockedUTXOsForRequest(It.IsAny<IBitcoinRequest>(), It.IsAny<BitcoinRequestType>()))
            .ReturnsAsync(new List<UTXO>());
        coinSelectionService
            .Setup(x => x.GetAvailableUTXOsAsync(It.IsAny<DerivationStrategyBase>()))
            .ReturnsAsync(plainListingUtxos ?? new List<UTXO>());
        coinSelectionService
            .Setup(x => x.GetAvailableUTXOsAsync(It.IsAny<DerivationStrategyBase>(),
                It.IsAny<CoinSelectionStrategy>(), It.IsAny<int>(), It.IsAny<long>(), It.IsAny<long>()))
            .ReturnsAsync(new List<UTXO>());

        var bitcoinService = new BitcoinService(logger, new Mock<IMapper>().Object,
            walletWithdrawalRequestRepository.Object, new Mock<IWalletWithdrawalRequestPsbtRepository>().Object,
            null, null, nbXplorerService.Object, coinSelectionService.Object);

        return (bitcoinService, withdrawalRequest, coinSelectionService);
    }

    [Fact]
    async Task GenerateTemplatePSBT_WithNBXplorerCoinSelection_AsksForTheUTXOsClosestToTheAmount()
    {
        var previousFlag = Constants.COIN_SELECTION_FROM_NBXPLORER_ENABLED;
        Constants.COIN_SELECTION_FROM_NBXPLORER_ENABLED = true;
        try
        {
            // Arrange
            var (bitcoinService, withdrawalRequest, coinSelectionService) =
                CreateServiceForSelectionBranch(_logger, _internalWallet);
            var target = withdrawalRequest.SatsAmount;

            // Act
            var act = () => bitcoinService.GenerateTemplatePSBT(withdrawalRequest);

            // Assert
            await act.Should().ThrowAsync<NoUTXOsAvailableException>();

            coinSelectionService.Verify(x => x.GetAvailableUTXOsAsync(
                It.IsAny<DerivationStrategyBase>(), CoinSelectionStrategy.ClosestToTargetFirst,
                0, target, target), Times.Once);
            coinSelectionService.Verify(x => x.GetAvailableUTXOsAsync(It.IsAny<DerivationStrategyBase>()), Times.Never);
            coinSelectionService.Verify(x => x.GetTxInputCoins(It.IsAny<List<UTXO>>(), It.IsAny<IBitcoinRequest>(),
                It.IsAny<DerivationStrategyBase>(), true), Times.Once);
        }
        finally
        {
            Constants.COIN_SELECTION_FROM_NBXPLORER_ENABLED = previousFlag;
        }
    }

    [Fact]
    async Task GenerateTemplatePSBT_WithNBXplorerCoinSelection_AlsoCoversWithdrawAllFunds()
    {
        var previousFlag = Constants.COIN_SELECTION_FROM_NBXPLORER_ENABLED;
        Constants.COIN_SELECTION_FROM_NBXPLORER_ENABLED = true;
        try
        {
            // Arrange
            // A full withdrawal takes every UTXO whichever selector runs, because the amount is set to
            // the whole balance just above. It goes through NBXplorer as well rather than being a case
            // of its own
            var walletUtxo = new UTXO
            {
                Outpoint = new OutPoint(new uint256(1), 0),
                Value = new Money(500_000L)
            };
            var (bitcoinService, withdrawalRequest, coinSelectionService) =
                CreateServiceForSelectionBranch(_logger, _internalWallet, withdrawAllFunds: true,
                    plainListingUtxos: new List<UTXO> { walletUtxo });

            // Act
            var act = () => bitcoinService.GenerateTemplatePSBT(withdrawalRequest);

            // Assert
            await act.Should().ThrowAsync<NoUTXOsAvailableException>();

            // The amount became the wallet balance, and that is what NBXplorer is asked to aim for
            withdrawalRequest.SatsAmount.Should().Be(500_000);
            coinSelectionService.Verify(x => x.GetAvailableUTXOsAsync(
                It.IsAny<DerivationStrategyBase>(), CoinSelectionStrategy.ClosestToTargetFirst,
                0, 500_000, 500_000), Times.Once);
            coinSelectionService.Verify(x => x.GetTxInputCoins(It.IsAny<List<UTXO>>(), It.IsAny<IBitcoinRequest>(),
                It.IsAny<DerivationStrategyBase>(), true), Times.Once);

            // The plain listing is still read once, to work out the balance rather than to select
            coinSelectionService.Verify(x => x.GetAvailableUTXOsAsync(It.IsAny<DerivationStrategyBase>()), Times.Once);
        }
        finally
        {
            Constants.COIN_SELECTION_FROM_NBXPLORER_ENABLED = previousFlag;
        }
    }

    [Fact]
    async Task GenerateTemplatePSBT_WithoutNBXplorerCoinSelection_UsesThePlainListing()
    {
        var previousFlag = Constants.COIN_SELECTION_FROM_NBXPLORER_ENABLED;
        Constants.COIN_SELECTION_FROM_NBXPLORER_ENABLED = false;
        try
        {
            // Arrange
            var (bitcoinService, withdrawalRequest, coinSelectionService) =
                CreateServiceForSelectionBranch(_logger, _internalWallet);

            // Act
            var act = () => bitcoinService.GenerateTemplatePSBT(withdrawalRequest);

            // Assert
            await act.Should().ThrowAsync<NoUTXOsAvailableException>();

            coinSelectionService.Verify(x => x.GetAvailableUTXOsAsync(It.IsAny<DerivationStrategyBase>()), Times.Once);
            coinSelectionService.Verify(x => x.GetAvailableUTXOsAsync(It.IsAny<DerivationStrategyBase>(),
                It.IsAny<CoinSelectionStrategy>(), It.IsAny<int>(), It.IsAny<long>(), It.IsAny<long>()), Times.Never);
            coinSelectionService.Verify(x => x.GetTxInputCoins(It.IsAny<List<UTXO>>(), It.IsAny<IBitcoinRequest>(),
                It.IsAny<DerivationStrategyBase>(), false), Times.Once);
        }
        finally
        {
            Constants.COIN_SELECTION_FROM_NBXPLORER_ENABLED = previousFlag;
        }
    }
}
