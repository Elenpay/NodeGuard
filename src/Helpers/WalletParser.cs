using NBitcoin;
using NBitcoin.Scripting;
using NBXplorer.DerivationStrategy;

namespace NodeGuard.Helpers;

public static class WalletParser
{
    /// <summary>
    /// Parse the output descriptor string to get the wallet info, Took from BTCPAYServer codebase
    /// </summary>
    /// <param name="outputDescriptorStr"></param>
    public static (DerivationStrategyBase, (BitcoinExtPubKey, RootedKeyPath)[]) ParseOutputDescriptor(
        string outputDescriptorStr, Network currentNetwork)
    {
        if (currentNetwork == null) throw new ArgumentNullException(nameof(currentNetwork));
        if (string.IsNullOrWhiteSpace(outputDescriptorStr))
            throw new ArgumentException("Value cannot be null or whitespace.", nameof(outputDescriptorStr));

        var factory = new DerivationStrategyFactory(currentNetwork);
        outputDescriptorStr = outputDescriptorStr.Trim();

        if (outputDescriptorStr.Contains("<0;1>"))
        {
            throw new ArgumentException("Descriptor contains <0;1> which is not supported, please use <0/*>");
        }

        var outputDescriptor = OutputDescriptor.Parse(outputDescriptorStr, currentNetwork);
        switch (outputDescriptor)
        {
            //TODO TR descriptor when NBitcoin supports it
            case OutputDescriptor.PK _:
                throw new FormatException("Output descriptor not supported: " + outputDescriptorStr);
            case OutputDescriptor.Raw _:
                throw new FormatException("Output descriptor not supported: " + outputDescriptorStr);

            case OutputDescriptor.Addr _:
                throw new FormatException("Output descriptor not supported: " + outputDescriptorStr);
            case OutputDescriptor.Combo _:
                throw new FormatException("Output descriptor not supported: " + outputDescriptorStr);
            case OutputDescriptor.Multi multi:
                return ExtractFromMulti(multi);
            case OutputDescriptor.PKH pkh:
                return ExtractFromPkProvider(pkh.PkProvider, "-[legacy]");
            case OutputDescriptor.SH _:
                throw new FormatException(
                    "Legacy multisig is not supported, please use segwit multisig instead.");
            case OutputDescriptor.WPKH wpkh:
                return ExtractFromPkProvider(wpkh.PkProvider, "");
            case OutputDescriptor.WSH {Inner: OutputDescriptor.Multi multi}:
                return ExtractFromMulti(multi);
            case OutputDescriptor.WSH:
                throw new FormatException("wsh descriptors are only supported with multisig");
            default:
                throw new ArgumentOutOfRangeException(nameof(outputDescriptor));
        }

        (DerivationStrategyBase, (BitcoinExtPubKey, RootedKeyPath)[]) ExtractFromMulti(OutputDescriptor.Multi multi)
        {
            var multiPkProviders = multi.PkProviders;
            
            var xpubs = multiPkProviders.Select(provider => ExtractFromPkProvider(provider)).ToArray();

            var xpubsStrings = xpubs.Select(tuple => tuple.Item1.ToString()).ToArray();
            
            if(multi.IsSorted)
                xpubsStrings = xpubsStrings.OrderBy(x => x).ToArray();
            
            var extractFromMulti = (
                Parse(
                    $"{multi.Threshold}-of-{(string.Join('-', xpubsStrings))}{(multi.IsSorted ? "" : "-[keeporder]")}"),
                xpubs.SelectMany(tuple => tuple.Item2).ToArray());
            return extractFromMulti;
        }

        (DerivationStrategyBase, (BitcoinExtPubKey, RootedKeyPath)[]) ExtractFromPkProvider(
            PubKeyProvider pubKeyProvider,
            string suffix = "")
        {
            switch (pubKeyProvider)
            {
                case PubKeyProvider.Const _:
                    throw new FormatException("Only HD output descriptors are supported.");
                case PubKeyProvider.HD hd:
                    if (hd.Path != null && hd.Path.ToString() != "0")
                    {
                        throw new FormatException("Custom change paths are not supported.");
                    }

                    return (Parse($"{hd.Extkey}{suffix}"), null);
                case PubKeyProvider.Origin origin:
                    var innerResult = ExtractFromPkProvider(origin.Inner, suffix);
                    var bitcoinExtPubKey = innerResult.Item1.GetExtPubKeys().First().GetWif(currentNetwork);
                    var rootedKeyPath = origin.KeyOriginInfo;
                    return (innerResult.Item1, new[] {(extPubKey: bitcoinExtPubKey, KeyOriginInfo: rootedKeyPath)});
                default:
                    throw new ArgumentOutOfRangeException();
            }
        }

        DerivationStrategyBase Parse(string str)
        {
            str = str.Trim();
            var strategy = factory.Parse(str);
            return strategy;
        }
    }
}