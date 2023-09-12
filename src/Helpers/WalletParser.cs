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
    public static (DerivationStrategyBase, RootedKeyPath[]) ParseOutputDescriptor(string outputDescriptorStr, Network currentNetwork)
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

        (DerivationStrategyBase, RootedKeyPath[]) ExtractFromMulti(OutputDescriptor.Multi multi)
        {
            var xpubs = multi.PkProviders.Select(provider => ExtractFromPkProvider(provider));
            var valueTuples = xpubs as (DerivationStrategyBase, RootedKeyPath[])[] ?? xpubs.ToArray();
            return (
                Parse(
                    $"{multi.Threshold}-of-{(string.Join('-', valueTuples.Select(tuple => tuple.Item1.ToString())))}{(multi.IsSorted ? "" : "-[keeporder]")}"),
                valueTuples.SelectMany(tuple => tuple.Item2).ToArray());
        }

        (DerivationStrategyBase, RootedKeyPath[]) ExtractFromPkProvider(PubKeyProvider pubKeyProvider,
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
                    return (innerResult.Item1, new[] {origin.KeyOriginInfo});
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