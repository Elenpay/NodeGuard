using FluentAssertions;
using FundsManager.TestHelpers;

namespace FundsManager.Data.Models;

public class WalletTests
{
    [Fact]
    void GetDerivationStrategy_NoKeys()
    {
        // Arrange
        var wallet = new Wallet();

        // Act
        var result = wallet.GetDerivationStrategy();

        // Assert
        result.Should().BeNull();
    }

    [Fact]
    void GetDerivationStrategy_SingleSigDerivationStrategy()
    {
        // Arrange
        var wallet = CreateWallet.SingleSig(); 

        // Act
        var result = wallet.GetDerivationStrategy();

        // Assert
        var derivation = result!.GetDerivation();
        derivation.Redeem.Should().BeNull();
        derivation.ScriptPubKey.ToString().Should().Be("0 796cf3de2a828f0fa18f0f7d2cd11ea7273aca1b");
    }

    [Fact]
    void GetDerivationStrategy_MultiSigDerivationStrategy()
    {
        // Arrange
        var wallet = CreateWallet.MultiSig();

        // Act
        var result = wallet.GetDerivationStrategy();
        // Assert
        var derivation = result!.GetDerivation();
        derivation.Redeem.ToString().Should().Be(
            "2 " +
            "0251af15e05bb8b7eac1895b3f5f47ffefecd8321a02ef22298f89d0a2037df060 " +
            "03808e2e1cd482bdd07196943c5a8133d169af86022b6bb0381771644ec07444d7 " +
            "03afeb3ea5c2a70dc4aa2872964a5e2ff1172af3466a1bfed039b62665497fc546 " +
            "3 " +
            "OP_CHECKMULTISIG"
        );

        derivation.ScriptPubKey.ToString().Should().Be("0 467798178e61383c90c2b55b1b3255b703caf09d74df3990b0094690090b6ad0");
    }
}