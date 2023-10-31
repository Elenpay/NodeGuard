using FluentAssertions;
using NBitcoin;
using NBXplorer.DerivationStrategy;
using NodeGuard.Data.Models;
using NodeGuard.Helpers;
using NSubstitute.ExceptionExtensions;
using Key = NodeGuard.Data.Models.Key;

namespace NodeGuard.Tests;

public class WalletParserTests
{
    
    [Theory]
    [InlineData("wsh(multi(2,[62a7956f/84h/1h/0h]tpubDDXgATYzdQkHHhZZCMcNJj8BGDENvzMVou5v9NdxiP4rxDLj33nS233dGFW4htpVZSJ6zds9eVqAV9RyRHHiKtwQKX8eR4n4KN3Dwmj7A3h/0/*,[11312aa2/84h/1h/0h]tpubDC8a54NFtQtMQAZ97VhoU9V6jVTvi9w4Y5SaAXJSBYETKg3AoX5CCKndznhPWxJUBToPCpT44s86QbKdGpKAnSjcMTGW4kE6UQ8vpBjcybW/0/*,[8f71b834/84h/1h/0h]tpubDChjnP9LXNrJp43biqjY7FH93wgRRNrNxB4Q8pH7PPRy8UPcH2S6V46WGVJ47zVGF7SyBJNCpnaogsFbsybVQckGtVhCkng3EtFn8qmxptS/0/*))",
        "2-of-tpubDDXgATYzdQkHHhZZCMcNJj8BGDENvzMVou5v9NdxiP4rxDLj33nS233dGFW4htpVZSJ6zds9eVqAV9RyRHHiKtwQKX8eR4n4KN3Dwmj7A3h-tpubDC8a54NFtQtMQAZ97VhoU9V6jVTvi9w4Y5SaAXJSBYETKg3AoX5CCKndznhPWxJUBToPCpT44s86QbKdGpKAnSjcMTGW4kE6UQ8vpBjcybW-tpubDChjnP9LXNrJp43biqjY7FH93wgRRNrNxB4Q8pH7PPRy8UPcH2S6V46WGVJ47zVGF7SyBJNCpnaogsFbsybVQckGtVhCkng3EtFn8qmxptS-[keeporder]",
        "RegTest", 2,3)]
    [InlineData("wsh(sortedmulti(2,[00000000/48h/1h/1h]tpubDCNTr5eMFBvYQJKJNySFcXM3HdxjseLSpTA5crAPAbXYjBb5zgtwKrHTTdRu11vUCZVYeHcV6H2oj2reuGma9Hu3t1LSPNgL5b8F6W6hsQN/0/*,[60f3a0b3/48h/1h/1h]tpubDCfM7v7fKZ31gTGGggNMycfCr5cDGinyijveRZ44RYSgAgEARwhaBd6PPpWst8kKbhEVoqNasgjHFWZKrEQoJ9pzPVEmNZDNe92hShzEMDy/0/*,[60f3a0b2/48h/1h/1h]tpubDCwziS3VhtLnXSR7oL9Xkft5LVbrsfEQ9h7YkCNSa1cYSi1KNuMEnsb9NeouNjpq91KSK3R87jFx8oFGvSM5g9Vax1VrvawWzD9xnGjgndB/0/*))#jt4e29lt",
        "2-of-tpubDCfM7v7fKZ31gTGGggNMycfCr5cDGinyijveRZ44RYSgAgEARwhaBd6PPpWst8kKbhEVoqNasgjHFWZKrEQoJ9pzPVEmNZDNe92hShzEMDy-tpubDCNTr5eMFBvYQJKJNySFcXM3HdxjseLSpTA5crAPAbXYjBb5zgtwKrHTTdRu11vUCZVYeHcV6H2oj2reuGma9Hu3t1LSPNgL5b8F6W6hsQN-tpubDCwziS3VhtLnXSR7oL9Xkft5LVbrsfEQ9h7YkCNSa1cYSi1KNuMEnsb9NeouNjpq91KSK3R87jFx8oFGvSM5g9Vax1VrvawWzD9xnGjgndB",
        "RegTest", 2,3)]
    public void ParseOutputDescriptor_ValidSegwitMultisig(string descriptor, string expectedDerivationStrategy, string network, int m , int n)
    {
        //Arrange
        var currentNetwork = Network.GetNetwork(network);

        //Act
        var (strategyBase,valueTuples) = WalletParser.ParseOutputDescriptor(descriptor, currentNetwork);

        //Assert
        strategyBase.ToString().Should().Be(expectedDerivationStrategy);
        valueTuples.Length.Should().Be(n);
        foreach (var (bitcoinExtPubKey, rootedKeyPath) in valueTuples)
        {
            bitcoinExtPubKey.Should().NotBeNull();
            rootedKeyPath.Should().NotBeNull();
        }
        strategyBase.Should().BeOfType<P2WSHDerivationStrategy>();
        
        ((P2WSHDerivationStrategy)strategyBase).Inner.Should().BeOfType<MultisigDerivationStrategy>();
        

        var inner = (MultisigDerivationStrategy)((P2WSHDerivationStrategy)strategyBase).Inner;
        inner.IsLegacy.Should().BeFalse();
        inner.Keys.Count.Should().Be(n);
        inner.RequiredSignatures.Should().Be(m);
        inner.ToString().Should().Be(expectedDerivationStrategy);
        var keys = valueTuples.Select(x => new Key
        {
            Id = 0,
            CreationDatetime = DateTimeOffset.UtcNow,
            Name = "Imported key from output descriptor",
            XPUB = x.Item1.ToString() ?? throw new InvalidOperationException(),
            Description =null,
            MasterFingerprint = x.Item2.MasterFingerprint.ToString(),
            Path = x.Item2.KeyPath.ToString(),
            IsBIP39ImportedKey = false,
                    
        }).ToList();

        var msigStrategy =  ((P2WSHDerivationStrategy) strategyBase).Inner as MultisigDerivationStrategy;
        //Create a wallet from the descriptor and test the matching strategies
        var wallet = new Data.Models.Wallet()
        {
            MofN = m,
            Keys = new List<Key>(keys),
            IsUnSortedMultiSig = !msigStrategy.LexicographicOrder
        };
        
        var derivationStrategy = wallet.GetDerivationStrategy();
        
        derivationStrategy.Should().BeOfType<P2WSHDerivationStrategy>();
        
        ((P2WSHDerivationStrategy)derivationStrategy).Inner.Should().BeOfType<MultisigDerivationStrategy>();
        
        strategyBase.ToString().Should().Be(derivationStrategy.ToString());

    }
    
    [Theory]
    [InlineData("wsh(sortedmulti(2,[00000000/48h/1h/1h]tpubDCNTr5eMFBvYQJKJNySFcXM3HdxjseLSpTA5crAPAbXYjBb5zgtwKrHTTdRu11vUCZVYeHcV6H2oj2reuGma9Hu3t1LSPNgL5b8F6W6hsQN/<0;1>/*,[60f3a0b3/48h/1h/1h]tpubDCfM7v7fKZ31gTGGggNMycfCr5cDGinyijveRZ44RYSgAgEARwhaBd6PPpWst8kKbhEVoqNasgjHFWZKrEQoJ9pzPVEmNZDNe92hShzEMDy/<0;1>/*,[60f3a0b2/48h/1h/1h]tpubDCwziS3VhtLnXSR7oL9Xkft5LVbrsfEQ9h7YkCNSa1cYSi1KNuMEnsb9NeouNjpq91KSK3R87jFx8oFGvSM5g9Vax1VrvawWzD9xnGjgndB/<0;1>/*))#wjr8h7a2", "regtest")]
    public void ParseOutputDescriptor_InvalidSegwitMultisig_ChangePath(string descriptor,
        string network)
    {
        //Arrange
        var currentNetwork = Network.GetNetwork(network);

        //Act
        var function = () => WalletParser.ParseOutputDescriptor(descriptor, currentNetwork);

        //Assert
        function.Should().ThrowExactly<ArgumentException>("Invalid descriptor should throw an exception when parsed");
    }
    
    [Theory]
    [InlineData("sh(sortedmulti(2,[00000000/48h/1h/1h]tpubDCNTr5eMFBvYQJKJNySFcXM3HdxjseLSpTA5crAPAbXYjBb5zgtwKrHTTdRu11vUCZVYeHcV6H2oj2reuGma9Hu3t1LSPNgL5b8F6W6hsQN/0/*,[60f3a0b3/48h/1h/1h]tpubDCfM7v7fKZ31gTGGggNMycfCr5cDGinyijveRZ44RYSgAgEARwhaBd6PPpWst8kKbhEVoqNasgjHFWZKrEQoJ9pzPVEmNZDNe92hShzEMDy/0/*,[60f3a0b2/48h/1h/1h]tpubDCwziS3VhtLnXSR7oL9Xkft5LVbrsfEQ9h7YkCNSa1cYSi1KNuMEnsb9NeouNjpq91KSK3R87jFx8oFGvSM5g9Vax1VrvawWzD9xnGjgndB/0/*))", "regtest")]
    [InlineData("sh(wsh(sortedmulti(2,[00000000/48h/1h/1h]tpubDCNTr5eMFBvYQJKJNySFcXM3HdxjseLSpTA5crAPAbXYjBb5zgtwKrHTTdRu11vUCZVYeHcV6H2oj2reuGma9Hu3t1LSPNgL5b8F6W6hsQN/0/*,[60f3a0b3/48h/1h/1h]tpubDCfM7v7fKZ31gTGGggNMycfCr5cDGinyijveRZ44RYSgAgEARwhaBd6PPpWst8kKbhEVoqNasgjHFWZKrEQoJ9pzPVEmNZDNe92hShzEMDy/0/*,[60f3a0b2/48h/1h/1h]tpubDCwziS3VhtLnXSR7oL9Xkft5LVbrsfEQ9h7YkCNSa1cYSi1KNuMEnsb9NeouNjpq91KSK3R87jFx8oFGvSM5g9Vax1VrvawWzD9xnGjgndB/0/*)))", "regtest")]
    public void ParseOutputDescriptor_InvalidSegwitMultisig_UnsupportedMultisig(string descriptor,
        string network)
    {
        //Arrange
        var currentNetwork = Network.GetNetwork(network);

        //Act
        var function = () => WalletParser.ParseOutputDescriptor(descriptor, currentNetwork);

        //Assert
        function.Should().ThrowExactly<FormatException>("Invalid descriptor should throw an exception when parsed");
    }

    [Theory]
    [InlineData("wpkh([8bafd160/49h/0h/0h]xpub661MyMwAqRbcGVBsTGeNZN6QGVHmMHLdSA4FteGsRrEriu4pnVZMZWnruFFFXkMnyoBjyHndD3Qwcfz4MPzBUxjSevweNFQx7SAYZATtcDw/0/*)#9x4vkw48","xpub661MyMwAqRbcGVBsTGeNZN6QGVHmMHLdSA4FteGsRrEriu4pnVZMZWnruFFFXkMnyoBjyHndD3Qwcfz4MPzBUxjSevweNFQx7SAYZATtcDw","mainnet")]
    [InlineData("wpkh([deadbeef/0h/1h/2]xpub6ERApfZwUNrhLCkDtcHTcxd75RbzS1ed54G1LkBUHQVHQKqhMkhgbmJbZRkrgZw4koxb5JaHWkY4ALHY2grBGRjaDMzQLcgJvLJuZZvRcEL/0/*)","xpub6ERApfZwUNrhLCkDtcHTcxd75RbzS1ed54G1LkBUHQVHQKqhMkhgbmJbZRkrgZw4koxb5JaHWkY4ALHY2grBGRjaDMzQLcgJvLJuZZvRcEL","mainnet")]
    public async Task ParseOutputDescriptor_ValidSegwitSinglesig(string descriptor, string expectedDerivationStrategy, string network)
    {
        //Arrange
        var currentNetwork = Network.GetNetwork(network);
        
        //Act
        var (strategyBase,valueTuples) = WalletParser.ParseOutputDescriptor(descriptor, currentNetwork);
        
        //Assert
        strategyBase.ToString().Should().Be(expectedDerivationStrategy);
        valueTuples.Length.Should().Be(1);
        foreach (var (bitcoinExtPubKey, rootedKeyPath) in valueTuples)
        {
            bitcoinExtPubKey.Should().NotBeNull();
            bitcoinExtPubKey.ToString().Should().Be(expectedDerivationStrategy);
            rootedKeyPath.Should().NotBeNull();
        }
        strategyBase.Should().BeOfType<DirectDerivationStrategy>();
        ((DirectDerivationStrategy) strategyBase).Segwit.Should().Be(true);

    }
    
    [Theory]
    [InlineData("wpkh([8b60afd1/49h/0h/0h]xpub661MyMwAFXkMnyoBjyHndD3QwRbcGVBsTGeNZN6QGVHcfz4MPzBUxjSevweNFQx7SqmMHLdSA4FteGsRrEriu4pnVZMZWnruFFAYZATtcDw/0/*)#9x4vkw48","mainnet")] //Invalid checksum
    [InlineData("invalid","mainnet")] //Invalid descriptor
    [InlineData("wpkh([8b60afd1/49h/0h/0h]xpub661MyMwAFXkMnyoBjyHndD3QwRbcGVBsTGeNZN6QGVHcfz4MPzBUxjSevweNFQx7SqmMHLdSA4FteGsRrEriu4pnVZMZWnruFFAYZATtcDw/0/*)#9x4vkw48","testnet")] //Invalid network
    [InlineData("pkh(xpub661MyMwAqRbcFW31YEwpkMuc5THy2PSt5bDMsktWQcFF8syAmRUapSCGu8ED9W6oDMSgv6Zz8idoc4a6mr8BDzTJY47LJhkJ8UB7WEGuduB/2147483648","mainnet")] 
    public void ParseOutputDescriptor_InvalidSegwitSingleSig(string descriptor, string network)
    {
        //Arrange
        var currentNetwork = Network.GetNetwork(network);
        
        //Act
        var function = () => WalletParser.ParseOutputDescriptor(descriptor, currentNetwork);

        function.Should().Throw<Exception>("Invalid descriptor should throw an exception when parsed");


    }
    
    // Testing GetOutputDescriptor method
    [Fact]
    public void GetOutputDescriptor_NativeSegwits()
    {
        // Arrange
        // Testing NodeGuard created Native Segwit hot wallet
        var wallet1HotWalletCreated = new Wallet()
        {
            IsHotWallet = true,
            InternalWalletId = 1,
            WalletAddressType = WalletAddressType.NativeSegwit,
            MofN = 1,
            InternalWalletSubDerivationPath = "0",
            Keys = new List<Key>() { 
                new() {
                    XPUB = "xpub661MyMwAqRbcFW31YEwpkMuc5THy2PSt5bDMsktWQcFF8syAmRUapSCGu8ED9W6oDMSgv6Zz8idoc4a6mr8BDzTJY47LJhkJ8UB7WEGuduB",
                    Path = "48'/0'/0'",
                    MasterFingerprint = "ed0210c8",
                    InternalWalletId = 1,
                }
            }
        };

        // Testing NodeGuard created Native Segwit cold wallet
        var wallet1ColdWallet = new Wallet()
        {
            IsHotWallet = false,
            WalletAddressType = WalletAddressType.NativeSegwit,
            MofN = 2,
            Keys = new List<Key>()
            {
                new()
                {
                    XPUB = "xpub661MyMwAqRbcFW31YEwpkMuc5THy2PSt5bDMsktWQcFF8syAmRUapSCGu8ED9W6oDMSgv6Zz8idoc4a6mr8BDzTJY47LJhkJ8UB7WEGuduB",
                    Path = "48'/0'/0",
                    MasterFingerprint = "ed0210c8"
                },
                new()
                {
                    XPUB = "xpub69H7F5d8KSRgmmdJg2KhpAK8SR3DjMwAdkxj3ZuxV27CprR9LgpeyGmXUbC6wb7ERfvrnKZjXoUmmDznezpbZb7ap6r1D3tgFxHmwMkQTPH",
                    Path = "48'/0'/1",
                    MasterFingerprint = "ed0210c8"
                },
                new()
                {
                    XPUB = "xpub6CUGRUonZSQ4TWtTMmzXdrXDtypWKiKrhko4egpiMZbpiaQL2jkwSB1icqYh2cfDfVxdx4df189oLKnC5fSwqPfgyP3hooxujYzAu3fDVmz", 
                    Path = "48'/0'/2", 
                    MasterFingerprint = "ed0210c8"
                }
            }
        };
        
        // Act
        var outputDescriptor1 = wallet1HotWalletCreated.GetOutputDescriptor("mainnet");
        var outputDescriptor2 = wallet1ColdWallet.GetOutputDescriptor("mainnet");
        
        // Assert
        outputDescriptor1.Should().Be("wpkh([ed0210c8/48'/0'/0']xpub661MyMwAqRbcFW31YEwpkMuc5THy2PSt5bDMsktWQcFF8syAmRUapSCGu8ED9W6oDMSgv6Zz8idoc4a6mr8BDzTJY47LJhkJ8UB7WEGuduB/0/*)#yt9sugwc");
        outputDescriptor2.Should().Be(
            "wsh(sortedmulti(2," +
            "[ed0210c8/48'/0'/0]xpub661MyMwAqRbcFW31YEwpkMuc5THy2PSt5bDMsktWQcFF8syAmRUapSCGu8ED9W6oDMSgv6Zz8idoc4a6mr8BDzTJY47LJhkJ8UB7WEGuduB/0/*," +
            "[ed0210c8/48'/0'/1]xpub69H7F5d8KSRgmmdJg2KhpAK8SR3DjMwAdkxj3ZuxV27CprR9LgpeyGmXUbC6wb7ERfvrnKZjXoUmmDznezpbZb7ap6r1D3tgFxHmwMkQTPH/0/*," +
            "[ed0210c8/48'/0'/2]xpub6CUGRUonZSQ4TWtTMmzXdrXDtypWKiKrhko4egpiMZbpiaQL2jkwSB1icqYh2cfDfVxdx4df189oLKnC5fSwqPfgyP3hooxujYzAu3fDVmz/0/*))#8qt3h45m"
        );
    }
    
    [Fact]
    public void GetOutputDescriptor_NestedSegwits()
    {
        // Arrange
        // Testing NodeGuard created Nested Segwit hot wallet
        var wallet1HotWalletCreated = new Wallet()
        {
            IsHotWallet = true,
            InternalWalletId = 1,
            WalletAddressType = WalletAddressType.NestedSegwit,
            MofN = 1,
            InternalWalletSubDerivationPath = "0",
            Keys = new List<Key>() { 
                new() {
                    XPUB = "xpub661MyMwAqRbcFW31YEwpkMuc5THy2PSt5bDMsktWQcFF8syAmRUapSCGu8ED9W6oDMSgv6Zz8idoc4a6mr8BDzTJY47LJhkJ8UB7WEGuduB",
                    Path = "48'/0'/0'",
                    MasterFingerprint = "ed0210c8",
                    InternalWalletId = 1,
                }
            }
        };
        
        // Testing NodeGuard created Nested Segwit cold wallet
        var wallet1ColdWallet = new Wallet()
        {
            IsHotWallet = false,
            WalletAddressType = WalletAddressType.NestedSegwit,
            MofN = 2,
            Keys = new List<Key>()
            {
                new()
                {
                    XPUB = "xpub661MyMwAqRbcFW31YEwpkMuc5THy2PSt5bDMsktWQcFF8syAmRUapSCGu8ED9W6oDMSgv6Zz8idoc4a6mr8BDzTJY47LJhkJ8UB7WEGuduB",
                    Path = "48'/0'/0",
                    MasterFingerprint = "ed0210c8"
                },
                new()
                {
                    XPUB = "xpub69H7F5d8KSRgmmdJg2KhpAK8SR3DjMwAdkxj3ZuxV27CprR9LgpeyGmXUbC6wb7ERfvrnKZjXoUmmDznezpbZb7ap6r1D3tgFxHmwMkQTPH",
                    Path = "48'/0'/1",
                    MasterFingerprint = "ed0210c8"
                },
                new()
                {
                    XPUB = "xpub6CUGRUonZSQ4TWtTMmzXdrXDtypWKiKrhko4egpiMZbpiaQL2jkwSB1icqYh2cfDfVxdx4df189oLKnC5fSwqPfgyP3hooxujYzAu3fDVmz", 
                    Path = "48'/0'/2", 
                    MasterFingerprint = "ed0210c8"
                }
            }
        };
        
        //Act
        var outputDescriptor1 = wallet1HotWalletCreated.GetOutputDescriptor("mainnet");
        var outputDescriptor2 = wallet1ColdWallet.GetOutputDescriptor("mainnet");
        
        // Assert
        outputDescriptor1.Should().Be("sh(wpkh([ed0210c8/48'/0'/0']xpub661MyMwAqRbcFW31YEwpkMuc5THy2PSt5bDMsktWQcFF8syAmRUapSCGu8ED9W6oDMSgv6Zz8idoc4a6mr8BDzTJY47LJhkJ8UB7WEGuduB/0/*))#u4qug9gg");
        outputDescriptor2.Should().Be(
            "sh(sortedmulti(2," +
            "[ed0210c8/48'/0'/0]xpub661MyMwAqRbcFW31YEwpkMuc5THy2PSt5bDMsktWQcFF8syAmRUapSCGu8ED9W6oDMSgv6Zz8idoc4a6mr8BDzTJY47LJhkJ8UB7WEGuduB/0/*," +
            "[ed0210c8/48'/0'/1]xpub69H7F5d8KSRgmmdJg2KhpAK8SR3DjMwAdkxj3ZuxV27CprR9LgpeyGmXUbC6wb7ERfvrnKZjXoUmmDznezpbZb7ap6r1D3tgFxHmwMkQTPH/0/*," +
            "[ed0210c8/48'/0'/2]xpub6CUGRUonZSQ4TWtTMmzXdrXDtypWKiKrhko4egpiMZbpiaQL2jkwSB1icqYh2cfDfVxdx4df189oLKnC5fSwqPfgyP3hooxujYzAu3fDVmz/0/*))#acnwnep3"
        );
    }

    [Fact]
    public void GetOutputDescriptor_Legacy()
    {
        // Arrange
        // Testing NodeGuard created Legacy hot wallet
        var wallet1HotWalletCreated = new Wallet()
        {
            IsHotWallet = true,
            InternalWalletId = 1,
            WalletAddressType = WalletAddressType.Legacy,
            MofN = 1,
            InternalWalletSubDerivationPath = "0",
            Keys = new List<Key>()
            {
                new()
                {
                    XPUB = "xpub661MyMwAqRbcFW31YEwpkMuc5THy2PSt5bDMsktWQcFF8syAmRUapSCGu8ED9W6oDMSgv6Zz8idoc4a6mr8BDzTJY47LJhkJ8UB7WEGuduB",
                    Path = "48'/0'/0'",
                    MasterFingerprint = "ed0210c8",
                    InternalWalletId = 1,
                }
            }
        };

        // Testing NodeGuard created Legacy cold wallet
        var wallet1ColdWallet = new Wallet()
        {
            IsHotWallet = false,
            WalletAddressType = WalletAddressType.Legacy,
            MofN = 2,
            Keys = new List<Key>()
            {
                new()
                {
                    XPUB = "xpub661MyMwAqRbcFW31YEwpkMuc5THy2PSt5bDMsktWQcFF8syAmRUapSCGu8ED9W6oDMSgv6Zz8idoc4a6mr8BDzTJY47LJhkJ8UB7WEGuduB",
                    Path = "48'/0'/0",
                    MasterFingerprint = "ed0210c8"
                },
                new()
                {
                    XPUB = "xpub69H7F5d8KSRgmmdJg2KhpAK8SR3DjMwAdkxj3ZuxV27CprR9LgpeyGmXUbC6wb7ERfvrnKZjXoUmmDznezpbZb7ap6r1D3tgFxHmwMkQTPH",
                    Path = "48'/0'/1",
                    MasterFingerprint = "ed0210c8"
                },
                new()
                {
                    XPUB = "xpub6CUGRUonZSQ4TWtTMmzXdrXDtypWKiKrhko4egpiMZbpiaQL2jkwSB1icqYh2cfDfVxdx4df189oLKnC5fSwqPfgyP3hooxujYzAu3fDVmz",
                    Path = "48'/0'/2",
                    MasterFingerprint = "ed0210c8"
                }
            }
        };
        
        // Act
        var outputDescriptor1 = wallet1HotWalletCreated.GetOutputDescriptor("mainnet");
        var outputDescriptor2 = wallet1ColdWallet.GetOutputDescriptor("mainnet");
        
        // Assert
        outputDescriptor1.Should().Be("pkh([ed0210c8/48'/0'/0']xpub661MyMwAqRbcFW31YEwpkMuc5THy2PSt5bDMsktWQcFF8syAmRUapSCGu8ED9W6oDMSgv6Zz8idoc4a6mr8BDzTJY47LJhkJ8UB7WEGuduB/0/*)#p7sn6nw0");
        outputDescriptor2.Should().Be(
            "sortedmulti(2," +
            "[ed0210c8/48'/0'/0]xpub661MyMwAqRbcFW31YEwpkMuc5THy2PSt5bDMsktWQcFF8syAmRUapSCGu8ED9W6oDMSgv6Zz8idoc4a6mr8BDzTJY47LJhkJ8UB7WEGuduB/0/*," +
            "[ed0210c8/48'/0'/1]xpub69H7F5d8KSRgmmdJg2KhpAK8SR3DjMwAdkxj3ZuxV27CprR9LgpeyGmXUbC6wb7ERfvrnKZjXoUmmDznezpbZb7ap6r1D3tgFxHmwMkQTPH/0/*," +
            "[ed0210c8/48'/0'/2]xpub6CUGRUonZSQ4TWtTMmzXdrXDtypWKiKrhko4egpiMZbpiaQL2jkwSB1icqYh2cfDfVxdx4df189oLKnC5fSwqPfgyP3hooxujYzAu3fDVmz/0/*)#uqv67sle"
        );
    }

    [Fact]
    public void GetOutputDescriptor_Taproot()
    {
        // Arrange
        // Testing NodeGuard created Taproot hot wallet
        var wallet1HotWalletCreated = new Wallet()
        {
            IsHotWallet = true,
            InternalWalletId = 1,
            WalletAddressType = WalletAddressType.Taproot,
            MofN = 1,
            InternalWalletSubDerivationPath = "0",
            Keys = new List<Key>() { 
                new() {
                    XPUB = "xpub661MyMwAqRbcFW31YEwpkMuc5THy2PSt5bDMsktWQcFF8syAmRUapSCGu8ED9W6oDMSgv6Zz8idoc4a6mr8BDzTJY47LJhkJ8UB7WEGuduB",
                    Path = "48'/0'/0'",
                    MasterFingerprint = "ed0210c8",
                    InternalWalletId = 1,
                }
            }
        };
        
        // Testing NodeGuard created Taproot cold wallet
        var wallet1ColdWallet = new Wallet()
        {
            IsHotWallet = false,
            WalletAddressType = WalletAddressType.Taproot,
            MofN = 2,
            Keys = new List<Key>()
            {
                new()
                {
                    XPUB = "xpub661MyMwAqRbcFW31YEwpkMuc5THy2PSt5bDMsktWQcFF8syAmRUapSCGu8ED9W6oDMSgv6Zz8idoc4a6mr8BDzTJY47LJhkJ8UB7WEGuduB",
                    Path = "48'/0'/0",
                    MasterFingerprint = "ed0210c8"
                },
                new()
                {
                    XPUB = "xpub69H7F5d8KSRgmmdJg2KhpAK8SR3DjMwAdkxj3ZuxV27CprR9LgpeyGmXUbC6wb7ERfvrnKZjXoUmmDznezpbZb7ap6r1D3tgFxHmwMkQTPH",
                    Path = "48'/0'/1",
                    MasterFingerprint = "ed0210c8"
                },
                new()
                {
                    XPUB = "xpub6CUGRUonZSQ4TWtTMmzXdrXDtypWKiKrhko4egpiMZbpiaQL2jkwSB1icqYh2cfDfVxdx4df189oLKnC5fSwqPfgyP3hooxujYzAu3fDVmz", 
                    Path = "48'/0'/2", 
                    MasterFingerprint = "ed0210c8"
                }
            }
        };
        
        // Act
        var taprootFunction1 = () => wallet1HotWalletCreated.GetOutputDescriptor("mainnet");
        var taprootFunction2 = () => wallet1ColdWallet.GetOutputDescriptor("mainnet");
        
        // Assert
        taprootFunction1.Should().Throw<NotImplementedException>();
        taprootFunction2.Should().Throw<NotImplementedException>();
    }
}
