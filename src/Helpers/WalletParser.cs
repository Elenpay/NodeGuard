using NBitcoin;
using NBitcoin.WalletPolicies;
using NBXplorer.DerivationStrategy;
using NodeGuard.Data.Models;

namespace NodeGuard.Helpers;

public static class WalletParser
{
    /// <summary>
    /// Parse the output descriptor string to get the wallet info.
    /// NodeGuard accepts a deliberately small descriptor grammar: wpkh/pkh single-sig and
    /// (w)sh-less multi/sortedmulti or wsh(multi/sortedmulti), with external-chain-only keys
    /// ("xpub" or "xpub/0/*"). NBitcoin 10 removed the NBitcoin.Scripting OutputDescriptor API and
    /// its Miniscript replacement only parses BIP388 multipath descriptors ("/**"), which NodeGuard
    /// rejects — so the accepted grammar is parsed here directly, preserving the old behaviour.
    /// </summary>
    /// <param name="outputDescriptorStr"></param>
    /// <param name="currentNetwork"></param>
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

        var body = StripAndValidateChecksum(outputDescriptorStr);

        var (fragment, inner) = ReadFragment(body);
        switch (fragment)
        {
            //TODO TR descriptor when NBitcoin supports it
            case "pk":
            case "raw":
            case "addr":
            case "combo":
            case "tr":
                throw new FormatException("Output descriptor not supported: " + outputDescriptorStr);
            case "multi":
            case "sortedmulti":
                return ExtractFromMulti(fragment == "sortedmulti", inner);
            case "pkh":
                return ExtractFromKey(inner, "-[legacy]");
            case "sh":
                throw new FormatException(
                    "Legacy multisig is not supported, please use segwit multisig instead.");
            case "wpkh":
                return ExtractFromKey(inner, "");
            case "wsh":
                var (innerFragment, innerExpression) = ReadFragment(inner);
                if (innerFragment is "multi" or "sortedmulti")
                    return ExtractFromMulti(innerFragment == "sortedmulti", innerExpression);
                throw new FormatException("wsh descriptors are only supported with multisig");
            default:
                throw new FormatException("Output descriptor not supported: " + outputDescriptorStr);
        }

        static string StripAndValidateChecksum(string descriptor)
        {
            var hashIndex = descriptor.IndexOf('#');
            if (hashIndex < 0) return descriptor;

            var body = descriptor[..hashIndex];
            var checksum = descriptor[(hashIndex + 1)..];
            if (Miniscript.GetCheckSum(body) != checksum)
                throw new FormatException("Invalid checksum in output descriptor: " + descriptor);

            return body;
        }

        static (string fragment, string inner) ReadFragment(string expression)
        {
            var open = expression.IndexOf('(');
            if (open <= 0 || !expression.EndsWith(')'))
                throw new FormatException("Output descriptor not supported: " + expression);

            return (expression[..open], expression[(open + 1)..^1]);
        }

        (DerivationStrategyBase, (BitcoinExtPubKey, RootedKeyPath)[]) ExtractFromMulti(bool isSorted, string inner)
        {
            var parts = inner.Split(',');
            if (parts.Length < 2)
                throw new FormatException("Output descriptor not supported: " + inner);

            var threshold = uint.Parse(parts[0].Trim());

            var xpubs = parts.Skip(1).Select(key => ExtractFromKey(key)).ToArray();

            var xpubsStrings = xpubs.Select(tuple => tuple.Item1.ToString()).ToArray();

            if (isSorted)
                xpubsStrings = xpubsStrings.OrderBy(x => x).ToArray();

            var extractFromMulti = (
                Parse(
                    $"{threshold}-of-{(string.Join('-', xpubsStrings))}{(isSorted ? "" : "-[keeporder]")}"),
                xpubs.SelectMany(tuple => tuple.Item2).ToArray());
            return extractFromMulti;
        }

        (DerivationStrategyBase, (BitcoinExtPubKey, RootedKeyPath)[]) ExtractFromKey(
            string keyExpression,
            string suffix = "")
        {
            keyExpression = keyExpression.Trim();

            RootedKeyPath? keyOriginInfo = null;
            if (keyExpression.StartsWith('['))
            {
                var close = keyExpression.IndexOf(']');
                if (close < 0)
                    throw new FormatException("Output descriptor not supported: " + keyExpression);

                keyOriginInfo = RootedKeyPath.Parse(keyExpression[1..close]);
                keyExpression = keyExpression[(close + 1)..];
            }

            var slash = keyExpression.IndexOf('/');
            var xpubString = slash < 0 ? keyExpression : keyExpression[..slash];
            var derivation = slash < 0 ? null : keyExpression[(slash + 1)..];

            //Only the external chain ("0/*") is supported, like the previous OutputDescriptor-based parser
            if (derivation != null && derivation != "0/*")
            {
                throw new FormatException("Custom change paths are not supported.");
            }

            var bitcoinExtPubKey = new BitcoinExtPubKey(xpubString, currentNetwork);

            var strategy = Parse($"{bitcoinExtPubKey}{suffix}");

            if (keyOriginInfo == null)
                return (strategy, null);

            return (strategy, new[] {(extPubKey: bitcoinExtPubKey, KeyOriginInfo: keyOriginInfo)});
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
    /// The BIP380 checksum is appended, like the previous OutputDescriptor-based implementation did.
    /// </remarks>
    public static string GetOutputDescriptor(this Wallet wallet, string bitcoinNetwork)
    {
        var network = Network.GetNetwork(bitcoinNetwork);

        string RenderKey(Data.Models.Key key)
        {
            var rootedKeyPath = new RootedKeyPath(
                new HDFingerprint(GetMasterFingerprint(key.MasterFingerprint)),
                KeyPath.Parse(key.Path)
            );
            var bitcoinExtPubKey = new BitcoinExtPubKey(ExtPubKey.Parse(key.XPUB, network), network);
            return $"[{rootedKeyPath}]{bitcoinExtPubKey}/0/*";
        }

        string body;
        if (wallet.IsHotWallet)
        {
            var key = wallet.Keys.FirstOrDefault();
            var keyExpression = RenderKey(key);

            body = wallet.WalletAddressType switch
            {
                WalletAddressType.NativeSegwit => $"wpkh({keyExpression})",
                WalletAddressType.NestedSegwit => $"sh(wpkh({keyExpression}))",
                WalletAddressType.Legacy => $"pkh({keyExpression})",
                WalletAddressType.Taproot => throw new NotImplementedException(),
                _ => throw new Exception("Something went wrong")
            };
        }
        else
        {
            var keyExpressions = string.Join(',', wallet.Keys.Select(RenderKey));
            var multi =
                $"{(wallet.IsUnSortedMultiSig ? "multi" : "sortedmulti")}({wallet.MofN},{keyExpressions})";

            body = wallet.WalletAddressType switch
            {
                WalletAddressType.NativeSegwit => $"wsh({multi})",
                WalletAddressType.NestedSegwit => $"sh({multi})",
                WalletAddressType.Legacy => multi,
                WalletAddressType.Taproot => throw new NotImplementedException(),
                _ => throw new Exception("Something went wrong")
            };
        }

        return Miniscript.AddChecksum(body);
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
