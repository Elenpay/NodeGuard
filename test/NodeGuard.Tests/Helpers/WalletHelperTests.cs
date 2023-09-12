using FluentAssertions;
using NBitcoin;
using NBXplorer.DerivationStrategy;
using NodeGuard.Helpers;

namespace NodeGuard.Tests;

public class WalletHelperTests
{
    
    [Theory]
    [InlineData("wsh(sortedmulti(2,[62a7956f/84h/1h/0h]tpubDDXgATYzdQkHHhZZCMcNJj8BGDENvzMVou5v9NdxiP4rxDLj33nS233dGFW4htpVZSJ6zds9eVqAV9RyRHHiKtwQKX8eR4n4KN3Dwmj7A3h/0/*,[11312aa2/84h/1h/0h]tpubDC8a54NFtQtMQAZ97VhoU9V6jVTvi9w4Y5SaAXJSBYETKg3AoX5CCKndznhPWxJUBToPCpT44s86QbKdGpKAnSjcMTGW4kE6UQ8vpBjcybW/0/*,[8f71b834/84h/1h/0h]tpubDChjnP9LXNrJp43biqjY7FH93wgRRNrNxB4Q8pH7PPRy8UPcH2S6V46WGVJ47zVGF7SyBJNCpnaogsFbsybVQckGtVhCkng3EtFn8qmxptS/0/*))",
        "2-of-tpubDDXgATYzdQkHHhZZCMcNJj8BGDENvzMVou5v9NdxiP4rxDLj33nS233dGFW4htpVZSJ6zds9eVqAV9RyRHHiKtwQKX8eR4n4KN3Dwmj7A3h-tpubDC8a54NFtQtMQAZ97VhoU9V6jVTvi9w4Y5SaAXJSBYETKg3AoX5CCKndznhPWxJUBToPCpT44s86QbKdGpKAnSjcMTGW4kE6UQ8vpBjcybW-tpubDChjnP9LXNrJp43biqjY7FH93wgRRNrNxB4Q8pH7PPRy8UPcH2S6V46WGVJ47zVGF7SyBJNCpnaogsFbsybVQckGtVhCkng3EtFn8qmxptS",
        "RegTest", 2,3)]
    [InlineData("wsh(sortedmulti(2,[00000000/48h/1h/1h]tpubDCNTr5eMFBvYQJKJNySFcXM3HdxjseLSpTA5crAPAbXYjBb5zgtwKrHTTdRu11vUCZVYeHcV6H2oj2reuGma9Hu3t1LSPNgL5b8F6W6hsQN/0/*,[60f3a0b3/48h/1h/1h]tpubDCfM7v7fKZ31gTGGggNMycfCr5cDGinyijveRZ44RYSgAgEARwhaBd6PPpWst8kKbhEVoqNasgjHFWZKrEQoJ9pzPVEmNZDNe92hShzEMDy/0/*,[60f3a0b2/48h/1h/1h]tpubDCwziS3VhtLnXSR7oL9Xkft5LVbrsfEQ9h7YkCNSa1cYSi1KNuMEnsb9NeouNjpq91KSK3R87jFx8oFGvSM5g9Vax1VrvawWzD9xnGjgndB/0/*))#jt4e29lt",
        "2-of-tpubDCNTr5eMFBvYQJKJNySFcXM3HdxjseLSpTA5crAPAbXYjBb5zgtwKrHTTdRu11vUCZVYeHcV6H2oj2reuGma9Hu3t1LSPNgL5b8F6W6hsQN-tpubDCfM7v7fKZ31gTGGggNMycfCr5cDGinyijveRZ44RYSgAgEARwhaBd6PPpWst8kKbhEVoqNasgjHFWZKrEQoJ9pzPVEmNZDNe92hShzEMDy-tpubDCwziS3VhtLnXSR7oL9Xkft5LVbrsfEQ9h7YkCNSa1cYSi1KNuMEnsb9NeouNjpq91KSK3R87jFx8oFGvSM5g9Vax1VrvawWzD9xnGjgndB",
        "RegTest", 2,3)]
    public void ParseOutputDescriptor_ValidSegwitMultisig(string descriptor, string expectedDerivationStrategy, string network, int m , int n)
    {
        //Arrange
        var currentNetwork = Network.GetNetwork(network);

        //Act
        var (strategyBase,rootedKeyPath) = WalletParser.ParseOutputDescriptor(descriptor, currentNetwork);

        //Assert
        strategyBase.ToString().Should().Be(expectedDerivationStrategy);
        rootedKeyPath.Length.Should().Be(n);
        strategyBase.Should().BeOfType<P2WSHDerivationStrategy>();
        
        ((P2WSHDerivationStrategy)strategyBase).Inner.Should().BeOfType<MultisigDerivationStrategy>();
        

        var inner = (MultisigDerivationStrategy)((P2WSHDerivationStrategy)strategyBase).Inner;
        inner.IsLegacy.Should().BeFalse();
        inner.Keys.Count.Should().Be(n);
        inner.RequiredSignatures.Should().Be(m);
        inner.ToString().Should().Be(expectedDerivationStrategy);
        
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
        var (strategyBase,rootedKeyPath) = WalletParser.ParseOutputDescriptor(descriptor, currentNetwork);
        
        //Assert
        strategyBase.ToString().Should().Be(expectedDerivationStrategy);
        rootedKeyPath.Length.Should().Be(1);
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
    
}