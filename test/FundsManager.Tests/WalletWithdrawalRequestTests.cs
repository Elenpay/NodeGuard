using AutoFixture;
using FluentAssertions;
using FundsManager.Data.Models;

namespace FundsManager.Tests;

public class WalletWithdrawalRequestTests
{
    [Fact]
    public async Task SignatureCounter_Positive_RequiresInternalwallet()
    {
        // Arrange
        var fixture = new Fixture();
        fixture.Behaviors.Add(new OmitOnRecursionBehavior());

        var request = fixture.Create<WalletWithdrawalRequest>();
        // Act

        request.Wallet.MofN = 3;
        
        request.WalletWithdrawalRequestPSBTs.ForEach(x=> x.IsInternalWalletPSBT = false);
        request.WalletWithdrawalRequestPSBTs.ForEach(x=> x.IsFinalisedPSBT = false);
        request.WalletWithdrawalRequestPSBTs.ForEach(x=> x.IsTemplatePSBT = false);
        
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
        var fixture = new Fixture();
        fixture.Behaviors.Add(new OmitOnRecursionBehavior());

        var request = fixture.Create<WalletWithdrawalRequest>();
        // Act

        request.Wallet.MofN = 2;
        
        request.WalletWithdrawalRequestPSBTs.ForEach(x=> x.IsInternalWalletPSBT = false);
        request.WalletWithdrawalRequestPSBTs.ForEach(x=> x.IsFinalisedPSBT = false);
        request.WalletWithdrawalRequestPSBTs.ForEach(x=> x.IsTemplatePSBT = false);
        
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
        var fixture = new Fixture();
        fixture.Behaviors.Add(new OmitOnRecursionBehavior());

        var request = fixture.Create<WalletWithdrawalRequest>();
        // Act

        request.Wallet.MofN = 2;
        
        request.WalletWithdrawalRequestPSBTs.ForEach(x=> x.IsInternalWalletPSBT = false);
        request.WalletWithdrawalRequestPSBTs.ForEach(x=> x.IsFinalisedPSBT = false);
        request.WalletWithdrawalRequestPSBTs.ForEach(x=> x.IsTemplatePSBT = false);

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
        var fixture = new Fixture();
        fixture.Behaviors.Add(new OmitOnRecursionBehavior());

        var request = fixture.Create<WalletWithdrawalRequest>();
        // Act

        request.Wallet.MofN = 3;
        
        request.WalletWithdrawalRequestPSBTs.ForEach(x=> x.IsInternalWalletPSBT = false);
        request.WalletWithdrawalRequestPSBTs.ForEach(x=> x.IsFinalisedPSBT = false);
        request.WalletWithdrawalRequestPSBTs.ForEach(x=> x.IsTemplatePSBT = false);

        request.WalletWithdrawalRequestPSBTs.First().IsTemplatePSBT = true;
        request.WalletWithdrawalRequestPSBTs.Last().IsFinalisedPSBT = true;

        // Assert
        request.Wallet.RequiresInternalWalletSigning.Should().BeTrue();
        request.AreAllRequiredHumanSignaturesCollected.Should().BeFalse();
        request.NumberOfSignaturesCollected.Should().Be(1);
    }
    
   
    
}