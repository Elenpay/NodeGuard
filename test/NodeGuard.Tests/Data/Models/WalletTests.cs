using FluentAssertions;
using NodeGuard.Data.Models;
using NodeGuard.TestHelpers;
using NBitcoin;
using NBitcoin.Scripting;

namespace NodeGuard.Tests;

public class WalletTests
{
    private InternalWallet _internalWallet = CreateWallet.CreateInternalWallet();

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
        var wallet = CreateWallet.SingleSig(_internalWallet); 

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
        var wallet = CreateWallet.MultiSig(_internalWallet);

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
    
    /// <summary>
        /// This test check that we use sortedmulti() lexicographical order in a correct way by comparing outputdescriptors of address 0/0 between sparrow and our wallet
        /// </summary>
        /// <param name="mOfN"></param>
        /// <param name="xpub1"></param>
        /// <param name="xpub2"></param>
        /// <param name="expectedOutputDescriptor"></param>
        [Theory]
        [InlineData(2,
            "tpubDFjD2KhvH1qGE99v1UgQebupcJEPHxjBRkxjmWxesPPz5jP38GBBHCqimqHtiidrVo5P8PxusC38VT1FLEQGxerdLnvpJHrv6nWeZ62M1kF", // m/48'/1'/0'/2'
            "tpubDEGgUQHT3g8uJKSvDL8NnMB3x2dvkmHGR7HQVHk1p7TVdSc3Zzt1DM7fVtdtcS5qeFXwimY8uWLryaeRbbHBMiK4b3PZrKdLjrrwJAHmuX7", // m/48'/1'/0'/2'
            "sortedmulti(2,026090f9d9a8dfde88e48f6b7d655bff87d174fec71fe3c76adddb998db79292ed,031a5377f25ac17e37416937c852bac0e3e9b112bddf58479d3d91d81630b28b8f,03674f832d2fc0e22fbfe1e992262aa48b619dd5baefbcbd9f1a9eddb1bbc759bf)")]
        [InlineData(3,
            "tpubDDzUjTi441Y6P2BmULHFnFTTZNx3tzW6sm7zgzWhbqHyqzKun7cXjAMEGhikgG41e8wdSMRsQb8UpNoRLL5rvWQaEBhvtpwZxUuTuKZ5Wau", // m/44'/1'/0'/0
            "tpubDDpWvccRJRR9ExaRZt17QUxj5yDZnsCQy4Q5RiNaJ3yZSxqrm1y9GEayz5Qgk3R8JyLLPTYyb1mCxQCUz4qCt5tzfusk4CdEbeCp9vyY95R", // m/44'/1'/0'/0
            "sortedmulti(3,031a5377f25ac17e37416937c852bac0e3e9b112bddf58479d3d91d81630b28b8f,035b9a3989b650f08e843de6a6ba268c6e33ee4ba009b261cb3a7fda74dd9845d7,037ae0c9b7bd495e68b8efc1eeef61e5f0a5c39790367c82c39faf9f846f6a92c3)")]
        public void DerivationScheme_Lexicographicalorder(int mOfN, string xpub1, string xpub2, string expectedOutputDescriptor)
        {
            //Arrange

            var testingMultisigWallet = new Wallet
            {
                MofN = mOfN,
                Keys = new List<Data.Models.Key>
                        {new()
                            {
                                Name = "fm",

                                XPUB = "tpubDCwziS3VhtLnXSR7oL9Xkft5LVbrsfEQ9h7YkCNSa1cYSi1KNuMEnsb9NeouNjpq91KSK3R87jFx8oFGvSM5g9Vax1VrvawWzD9xnGjgndB"
                            },
                            new()
                            {
                                Name = "Key 1",

                                XPUB = xpub1
                            },
                            new()
                            {
                                Name = "Key 2",
                                XPUB =xpub2
                            },
                        },
                Name = "Test wallet",
                WalletAddressType = WalletAddressType.NativeSegwit,
                InternalWalletId = 0,
                IsFinalised = true,
                CreationDatetime = DateTimeOffset.Now
            };

            //Act

            //Shuffle keys
            testingMultisigWallet.Keys = testingMultisigWallet.Keys.OrderBy(a => Guid.NewGuid()).ToList();
            //We derive address 0/0 and check against the output descriptor by sparrow
            var script = testingMultisigWallet.GetDerivationStrategy().GetDerivation(new KeyPath("0/0"));
            var outputDescriptor = OutputDescriptor.InferFromScript(script.Redeem, new FlatSigningRepository(), Network.RegTest);
            //We split after the #checksum
            var outputDescriptorString = outputDescriptor.ToString().Split("#", StringSplitOptions.TrimEntries).First();

            //Assert
            outputDescriptorString.Should().Be(expectedOutputDescriptor.Trim());
        }
}