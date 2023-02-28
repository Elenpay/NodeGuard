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
        derivation.ScriptPubKey.ToString().Should().Be("0 7364aa1f2950a8ba6e3bc1a6f2c4d187975625a5");
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
            "032223852338ea67fbfd8dc352ab2625e79a27764621236bf1c0213090d3489e9f " +
            "03808e2e1cd482bdd07196943c5a8133d169af86022b6bb0381771644ec07444d7 " +
            "3 " +
            "OP_CHECKMULTISIG"
        );

        derivation.ScriptPubKey.ToString().Should().Be("0 8a0a76a2e50c0233e857d0dbcb7a204e8747aa6c005e3bfbb5abb217f0f73783");
    }
}