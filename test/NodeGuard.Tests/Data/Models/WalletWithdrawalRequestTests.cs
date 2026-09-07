using FluentAssertions;
using NodeGuard.Data.Models;

namespace NodeGuard.Tests;

public class WalletWithdrawalRequestTests
{
    [Fact]
    public async Task SignatureCounter_Positive_RequiresInternalwallet()
    {


        var request = new WalletWithdrawalRequest
        {
            Wallet = new Wallet
            {
                Keys = new List<Key>
                {
                    new Key(),
                    new Key(),
                    new Key()
                }
            },
            WalletWithdrawalRequestPSBTs = new List<WalletWithdrawalRequestPSBT>
            {
                new WalletWithdrawalRequestPSBT(),
                new WalletWithdrawalRequestPSBT(),
                new WalletWithdrawalRequestPSBT()
            }
        };
        // Act

        request.Wallet.MofN = 3;

        request.WalletWithdrawalRequestPSBTs.ForEach(x => x.IsInternalWalletPSBT = false);
        request.WalletWithdrawalRequestPSBTs.ForEach(x => x.IsFinalisedPSBT = false);
        request.WalletWithdrawalRequestPSBTs.ForEach(x => x.IsTemplatePSBT = false);

        request.WalletWithdrawalRequestPSBTs.First().IsTemplatePSBT = true;

        // Assert
        request.Wallet.RequiresInternalWalletSigning.Should().Be(true);
        request.AreAllRequiredHumanSignaturesCollected.Should().Be(true);
        request.NumberOfSignaturesCollected.Should().Be(2);
    }

    [Fact]
    public async Task SignatureCounter_Positive_NotRequiresInternalwallet()
    {
        // Arrange

        var request = new WalletWithdrawalRequest
        {
            Wallet = new Wallet
            {
                Keys = new List<Key>
                {
                    new Key(),
                    new Key(),
                    new Key()
                }
            },
            WalletWithdrawalRequestPSBTs = new List<WalletWithdrawalRequestPSBT>
            {
                new WalletWithdrawalRequestPSBT(),
                new WalletWithdrawalRequestPSBT(),
                new WalletWithdrawalRequestPSBT()
            }
        };
        // Act

        request.Wallet.MofN = 2;

        request.WalletWithdrawalRequestPSBTs.ForEach(x => x.IsInternalWalletPSBT = false);
        request.WalletWithdrawalRequestPSBTs.ForEach(x => x.IsFinalisedPSBT = false);
        request.WalletWithdrawalRequestPSBTs.ForEach(x => x.IsTemplatePSBT = false);

        request.WalletWithdrawalRequestPSBTs.First().IsTemplatePSBT = true;

        // Assert
        request.Wallet.RequiresInternalWalletSigning.Should().Be(false);
        request.AreAllRequiredHumanSignaturesCollected.Should().Be(true);
        request.NumberOfSignaturesCollected.Should().Be(2);
    }

    [Fact]
    public async Task SignatureCount_Negative_NotRequiresInternalWallet()
    {
        // Arrange
        var request = new WalletWithdrawalRequest
        {
            Wallet = new Wallet
            {
                Keys = new List<Key>
                {
                    new Key(),
                    new Key(),
                    new Key()
                }
            },
            WalletWithdrawalRequestPSBTs = new List<WalletWithdrawalRequestPSBT>
            {
                new WalletWithdrawalRequestPSBT(),
                new WalletWithdrawalRequestPSBT(),
                new WalletWithdrawalRequestPSBT()
            }
        };
        // Act

        request.Wallet.IsHotWallet = false;
        request.Wallet.MofN = 2;

        request.WalletWithdrawalRequestPSBTs.ForEach(x => x.IsInternalWalletPSBT = false);
        request.WalletWithdrawalRequestPSBTs.ForEach(x => x.IsFinalisedPSBT = false);
        request.WalletWithdrawalRequestPSBTs.ForEach(x => x.IsTemplatePSBT = false);

        request.WalletWithdrawalRequestPSBTs.First().IsTemplatePSBT = true;
        request.WalletWithdrawalRequestPSBTs.Last().IsFinalisedPSBT = true;

        // Assert
        request.Wallet.RequiresInternalWalletSigning.Should().BeFalse();
        request.AreAllRequiredHumanSignaturesCollected.Should().BeFalse();
        request.NumberOfSignaturesCollected.Should().Be(1);
    }

    [Fact]
    public async Task SignatureCount_Negative_RequiresInternalWallet()
    {
        // Arrange
        var request = new WalletWithdrawalRequest
        {
            Wallet = new Wallet
            {
                Keys = new List<Key>
                {
                    new Key(),
                    new Key(),
                    new Key()
                }
            },
            WalletWithdrawalRequestPSBTs = new List<WalletWithdrawalRequestPSBT>
            {
                new WalletWithdrawalRequestPSBT(),
                new WalletWithdrawalRequestPSBT(),
                new WalletWithdrawalRequestPSBT()
            }
        };
        // Act
        request.Wallet.IsHotWallet = false;
        request.Wallet.MofN = 3;

        request.WalletWithdrawalRequestPSBTs.ForEach(x => x.IsInternalWalletPSBT = false);
        request.WalletWithdrawalRequestPSBTs.ForEach(x => x.IsFinalisedPSBT = false);
        request.WalletWithdrawalRequestPSBTs.ForEach(x => x.IsTemplatePSBT = false);

        request.WalletWithdrawalRequestPSBTs.First().IsTemplatePSBT = true;
        request.WalletWithdrawalRequestPSBTs.Last().IsFinalisedPSBT = true;

        // Assert
        request.Wallet.RequiresInternalWalletSigning.Should().BeTrue();
        request.AreAllRequiredHumanSignaturesCollected.Should().BeFalse();
        request.NumberOfSignaturesCollected.Should().Be(1);
    }

    [Fact]
    public void GetSingleTemplatePsbt_NoRows_ReturnsNull()
    {
        new WalletWithdrawalRequest().GetSingleTemplatePsbt().Should().BeNull();
        new WalletWithdrawalRequest { WalletWithdrawalRequestPSBTs = new List<WalletWithdrawalRequestPSBT>() }
            .GetSingleTemplatePsbt().Should().BeNull();
    }

    [Fact]
    public void GetSingleTemplatePsbt_OneTemplateAmongApprovals_ReturnsIt()
    {
        var template = new WalletWithdrawalRequestPSBT { Id = 2, IsTemplatePSBT = true, PSBT = "template" };
        var request = new WalletWithdrawalRequest
        {
            WalletWithdrawalRequestPSBTs = new List<WalletWithdrawalRequestPSBT>
            {
                new() { Id = 1, PSBT = "approval" },
                template,
                new() { Id = 3, IsFinalisedPSBT = true, PSBT = "final" },
            },
        };

        request.GetSingleTemplatePsbt().Should().BeSameAs(template);
    }

    /// <summary>
    /// Two templates means it is unknowable which transaction was approved. The database forbids it
    /// (IX_WalletWithdrawalRequestPSBTs_Template); the model refuses to guess if it ever sees it.
    /// </summary>
    [Fact]
    public void GetSingleTemplatePsbt_TwoTemplates_Throws()
    {
        var request = new WalletWithdrawalRequest
        {
            Id = 5107,
            WalletWithdrawalRequestPSBTs = new List<WalletWithdrawalRequestPSBT>
            {
                new() { Id = 9612, IsTemplatePSBT = true, PSBT = "a" },
                new() { Id = 9613, IsTemplatePSBT = true, PSBT = "b" },
            },
        };

        request.Invoking(r => r.GetSingleTemplatePsbt())
            .Should().Throw<InvalidOperationException>()
            .WithMessage("*5107*more than one template*");
    }
}
