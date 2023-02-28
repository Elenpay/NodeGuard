using AutoMapper;
using FluentAssertions;
using FundsManager.Data.Models;
using FundsManager.Data.Repositories.Interfaces;
using FundsManager.Helpers;
using FundsManager.TestHelpers;
using Microsoft.Extensions.Logging;
using NBitcoin;
using NBXplorer.DerivationStrategy;
using NBXplorer.Models;
using NSubstitute.Exceptions;
using Key = FundsManager.Data.Models.Key;

namespace FundsManager.Services;

public class BitcoinServiceTests
{
    private ILogger<BitcoinService> _logger = new Mock<ILogger<BitcoinService>>().Object;

    [Fact]
    async void GenerateTemplatePSBT_NoWithdrawalRequest()
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
    async void GenerateTemplatePSBT_RequestNotPending(WalletWithdrawalRequestStatus status)
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

        var bitcoinService = new BitcoinService(_logger, null, null, walletWithdrawalRequestRepository.Object, null, null, null, null);

        // Act
        var act = () => bitcoinService.GenerateTemplatePSBT(withdrawalRequest);

        // Assert
        await act
            .Should()
            .ThrowAsync<InvalidOperationException>()
            .WithMessage("PSBT Generation cancelled, operation is not in pending state");
    }

    [Fact]
    async void GenerateTemplatePSBT_NBXplorerNotFullySynced()
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

        var bitcoinService = new BitcoinService(_logger, null, null, walletWithdrawalRequestRepository.Object, null, null, null, nbXplorerService.Object);

        // Act
        var act = () => bitcoinService.GenerateTemplatePSBT(withdrawalRequest);

        // Assert
        await act
            .Should()
            .ThrowAsync<NBXplorerNotFullySyncedException>()
            .WithMessage("Error, nbxplorer not fully synched");
    }

    [Fact]
    async void GenerateTemplatePSBT_NoDerivationStrategy()
    {
        // Arrange
        var withdrawalRequest = new WalletWithdrawalRequest()
        {
            Id = 1,
            Status = WalletWithdrawalRequestStatus.Pending,
            Wallet = CreateWallet.MultiSig()
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

        var bitcoinService = new BitcoinService(_logger, null, null, walletWithdrawalRequestRepository.Object, null, null, null, nbXplorerService.Object);

        // Act
        var act = () => bitcoinService.GenerateTemplatePSBT(withdrawalRequest);

        // Assert
        await act
            .Should()
            .ThrowAsync<ArgumentNotFoundException>()
            .WithMessage("Error while getting the derivation strategy scheme for wallet: 0");
    }

    [Fact]
    async void GenerateTemplatePSBT_MultisigSucceeds()
    {
        // Arrange
        var wallet = CreateWallet.MultiSig();
        var withdrawalRequest = new WalletWithdrawalRequest()
        {
            Id = 1,
            Status = WalletWithdrawalRequestStatus.Pending,
            Wallet = wallet,
            WalletWithdrawalRequestPSBTs = new List<WalletWithdrawalRequestPSBT>(),
            Amount = 0.01m,
            DestinationAddress = "bcrt1q8k3av6q5yp83rn332lx8a90k6kukhg28hs5qw7krdq95t629hgsqk6ztmf"
        };

        var walletWithdrawalRequestRepository = new Mock<IWalletWithdrawalRequestRepository>();
        var walletWithdrawalRequestPsbtRepository = new Mock<IWalletWithdrawalRequestPsbtRepository>();
        var fmutxoRepository = new Mock<IFMUTXORepository>();
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
                            ScriptPubKey = wallet.GetDerivationStrategy().GetDerivation(KeyPath.Parse("0/0")).ScriptPubKey,
                            KeyPath = KeyPath.Parse("0/0")
                        }
                    }
                }
            });
        fmutxoRepository
            .Setup(x => x.GetLockedUTXOs(It.IsAny<int>(), null))
            .ReturnsAsync(new List<FMUTXO>());

        var bitcoinService = new BitcoinService(_logger, fmutxoRepository.Object, mapper.Object, walletWithdrawalRequestRepository.Object, walletWithdrawalRequestPsbtRepository.Object, null, null, nbXplorerService.Object);

        // Act
        var result = await bitcoinService.GenerateTemplatePSBT(withdrawalRequest);

        // Assert
        var psbt = PSBT.Parse("cHNidP8BAIkBAAAAAQAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA/////wD/////AkBCDwAAAAAAIgAgPaPWaBQgTxHOMVfMfpX21blroUe8KAd6w2gLRelFuiAYUYkAAAAAACIAIDx3862ZOy+vKdDZ4oysyRZX0HARoqQ9LqqK2ukxoopiAAAAAE8BBDWHzwMvESQsgAAAAfw77kI6AYzrbSJqBmMojtD7XuD6nXkKs3DQMOBHMObIA4COLhzUgr3QcZaUPFqBM9Fpr4YCK2uwOBdxZE7AdETXEB/M5N4wAACAAQAAgAEAAIBPAQQ1h88DVqwD9IAAAAH5CK5KZrD/oasUtVrwzkjypwIly5AQkC1pAa+QuT6PgQJRrxXgW7i36sGJWz9fR//v7NgyGgLvIimPidCiA33wYBBg86CzMAAAgAEAAIABAACATwEENYfPA325Ro0AAAAAgN63GqLxTu1/NyL0SV4a0Hn1n8Dzg+Wye9nbb16ZISADr+s+pcKnDcSqKHKWSl4v8Rcq80ZqG/7QObYmZUl/xUYQ7QIQyDAAAIABAACAAAAAAAABASuAlpgAAAAAACIAINCp0IUCw4KZ8J/JokbAV1TBQtK4m6WLzUomP5VBhszOAQVpUiEC2FTFYM/mwE4L60Q0G2p5QElV7YlMD7fcgoJEH79pLLEhAwJn/wsRl0hvcYj5Y3Bv3uQlxZ57pBZ9KSeuEPVNmjS/IQNvzitZiz5ksZFSQuRibjPP4pwo+OWOqZLBL2x5ZrFVqVOuIgYC2FTFYM/mwE4L60Q0G2p5QElV7YlMD7fcgoJEH79pLLEYH8zk3jAAAIABAACAAQAAgAAAAAAAAAAAIgYDAmf/CxGXSG9xiPljcG/e5CXFnnukFn0pJ64Q9U2aNL8YYPOgszAAAIABAACAAQAAgAAAAAAAAAAAIgYDb84rWYs+ZLGRUkLkYm4zz+KcKPjljqmSwS9seWaxVakY7QIQyDAAAIABAACAAAAAAAAAAAAAAAAAAAAA", Network.RegTest);
        result.Should().BeEquivalentTo(psbt);
    }

    [Fact]
    async void GenerateTemplatePSBT_SingleSucceeds()
    {
        // Arrange
        var wallet = CreateWallet.SingleSig();
        var withdrawalRequest = new WalletWithdrawalRequest()
        {
            Id = 1,
            Status = WalletWithdrawalRequestStatus.Pending,
            Wallet = wallet,
            WalletWithdrawalRequestPSBTs = new List<WalletWithdrawalRequestPSBT>(),
            Amount = 0.01m,
            DestinationAddress = "bcrt1q8k3av6q5yp83rn332lx8a90k6kukhg28hs5qw7krdq95t629hgsqk6ztmf"
        };

        var walletWithdrawalRequestRepository = new Mock<IWalletWithdrawalRequestRepository>();
        var walletWithdrawalRequestPsbtRepository = new Mock<IWalletWithdrawalRequestPsbtRepository>();
        var fmutxoRepository = new Mock<IFMUTXORepository>();
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
                            ScriptPubKey = wallet.GetDerivationStrategy().GetDerivation(KeyPath.Parse("0/0")).ScriptPubKey,
                            KeyPath = KeyPath.Parse("0/0")
                        }
                    }
                }
            });
        fmutxoRepository
            .Setup(x => x.GetLockedUTXOs(It.IsAny<int>(), null))
            .ReturnsAsync(new List<FMUTXO>());

        var bitcoinService = new BitcoinService(_logger, fmutxoRepository.Object, mapper.Object, walletWithdrawalRequestRepository.Object, walletWithdrawalRequestPsbtRepository.Object, null, null, nbXplorerService.Object);

        // Act
        var result = await bitcoinService.GenerateTemplatePSBT(withdrawalRequest);

        // Assert
        var psbt = PSBT.Parse("cHNidP8BAIkBAAAAAQAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA/////wD/////AkBCDwAAAAAAIgAgPaPWaBQgTxHOMVfMfpX21blroUe8KAd6w2gLRelFuiCsUYkAAAAAACIAIDx3862ZOy+vKdDZ4oysyRZX0HARoqQ9LqqK2ukxoopiAAAAAE8BBDWHzwN9uUaNAAAAAYPR/OiA1LbTzxbLPvbXvtAwckIG3g+0T1zblR/ZodaiA5zBFsigPpL8htN/KJ/Ph8SPvQA/K+mSNXTSA0hgvPNuEO0CEMgwAACAAQAAgAEAAAAAAQEfgJaYAAAAAAAWABTpOvUBMqNMfl7P81etji6x4fXrMwAAAA==", Network.RegTest);
        result.Should().BeEquivalentTo(psbt);
    }
}