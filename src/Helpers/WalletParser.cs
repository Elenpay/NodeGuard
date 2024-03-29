using System.Text;
using Humanizer;
using NBitcoin;
using NBitcoin.Scripting;
using NBXplorer.DerivationStrategy;
using NodeGuard.Data.Models;

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

    /// <summary>
    /// Generates an output descriptor for a given wallet based on its type and the Bitcoin network it's associated with.
    /// </summary>
    /// <param name="wallet">The wallet for which the output descriptor is to be generated.</param>
    /// <param name="bitcoinNetwork">The Bitcoin network associated with the wallet.</param>
    /// <returns>A string representation of the output descriptor.</returns>
    /// <exception cref="System.NotImplementedException">Thrown when the wallet address type is Taproot, which is not currently supported.</exception>
    /// <exception cref="System.Exception">Thrown when the output descriptor could not be generated for some reason.</exception>
    /// <remarks>
    /// This method first determines the network based on the provided string. It then checks if the wallet is a hot wallet or not.
    /// If it is, it generates the output descriptor based on the first key in the wallet and the wallet's address type.
    /// If it's not a hot wallet, it generates a multi-signature output descriptor based on all the keys in the wallet and the wallet's address type.
    /// </remarks>
    public static string GetOutputDescriptor(this Wallet wallet, string bitcoinNetwork)
    {
        var network = Network.GetNetwork(bitcoinNetwork);
        OutputDescriptor outputDescriptor = null;
        PubKeyProvider pubKeyProvider;

        if (wallet.IsHotWallet)
        {
            var key = wallet.Keys.FirstOrDefault();
            pubKeyProvider = PubKeyProvider.NewHD(
                new BitcoinExtPubKey(
                    ExtPubKey.Parse(key.XPUB, network),
                    network
                ),
                new KeyPath("/0"),
                PubKeyProvider.DeriveType.UNHARDENED
            );
            var fingerprint = GetMasterFingerprint(key.MasterFingerprint);
            var rootedKeyPath = new RootedKeyPath(
                new HDFingerprint(fingerprint),
                KeyPath.Parse(key.Path)
            );
            pubKeyProvider = PubKeyProvider.NewOrigin(rootedKeyPath, pubKeyProvider);
            
            switch (wallet.WalletAddressType)
            {
                case WalletAddressType.NativeSegwit:
                    outputDescriptor = OutputDescriptor.NewWPKH(pubKeyProvider, network);
                    break;
                case WalletAddressType.NestedSegwit:
                    outputDescriptor = OutputDescriptor.NewWPKH(pubKeyProvider, network);
                    outputDescriptor = OutputDescriptor.NewSH(outputDescriptor, network);
                    break;
                case WalletAddressType.Legacy:
                    outputDescriptor = OutputDescriptor.NewPKH(pubKeyProvider, network);
                    break;
                case WalletAddressType.Taproot:
                    throw new NotImplementedException();
            }
        }
        else
        {
            var pubKeyProviders = new List<PubKeyProvider>();
            foreach (var k in wallet.Keys)
            {
                var rootedKeyPath = new RootedKeyPath(
                    new HDFingerprint(GetMasterFingerprint(k.MasterFingerprint)),
                    KeyPath.Parse(k.Path)
                );
                pubKeyProvider = PubKeyProvider.NewOrigin(
                    rootedKeyPath,
                    PubKeyProvider.NewHD(
                        new BitcoinExtPubKey(
                            ExtPubKey.Parse(k.XPUB, network),
                            network
                        ),
                        new KeyPath("/0"),
                        PubKeyProvider.DeriveType.UNHARDENED
                    )
                );
                pubKeyProviders.Add(pubKeyProvider);
            }
            outputDescriptor = OutputDescriptor.NewMulti(
                (uint)wallet.MofN,
                pubKeyProviders,
                !wallet.IsUnSortedMultiSig,
                network);
            
            switch (wallet.WalletAddressType)
            {
                case WalletAddressType.NativeSegwit:
                    outputDescriptor = OutputDescriptor.NewWSH(outputDescriptor, network);
                    break;
                case WalletAddressType.NestedSegwit:
                    outputDescriptor = OutputDescriptor.NewSH(outputDescriptor, network);
                    break;
                case WalletAddressType.Legacy:
                    break;
                case WalletAddressType.Taproot:
                    throw new NotImplementedException();
            }
        }

        return outputDescriptor is not null ? outputDescriptor.ToString() : throw new Exception("Something went wrong");
    }
    
    /// <summary>
    /// Converts a hexadecimal string representation of a master fingerprint into a byte array.
    /// </summary>
    /// <param name="masterFingerprint">The hexadecimal string representation of the master fingerprint.</param>
    /// <returns>A byte array that represents the master fingerprint.</returns>
    /// <remarks>
    /// This method works by iterating over the input string two characters at a time (since each byte in a hexadecimal string is represented by two characters), converting those two characters into a byte, and then adding that byte to the output array.
    /// </remarks>
    public static byte[] GetMasterFingerprint(string masterFingerprint)
    {
        var internalBytes = Enumerable.Range(0, masterFingerprint.Length)
            .Where(x => x % 2 == 0)
            .Select(x => Convert.ToByte(masterFingerprint.Substring(x, 2), 16))
            .ToArray();
        return internalBytes;
    }
}