/*
 * NodeGuard
 * Copyright (C) 2023  Elenpay
 *
 * This program is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Affero General Public License as published by
 * the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 *
 * This program is distributed in the hope that it will be useful,
 * but WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
 * GNU Affero General Public License for more details.
 *
 * You should have received a copy of the GNU Affero General Public License
 * along with this program.  If not, see http://www.gnu.org/licenses/.
 *
 */
using FundsManager.Data.Models;
using NBitcoin;
using Key = FundsManager.Data.Models.Key;

namespace FundsManager.TestHelpers;

public static class CreateWallet
{
    public static InternalWallet CreateInternalWallet(ILogger? logger = null)
    {
        var internalWallet = new InternalWallet
        {
            DerivationPath = Constants.DEFAULT_DERIVATION_PATH,
            MnemonicString =
                "middle teach digital prefer fiscal theory syrup enter crash muffin easily anxiety ill barely eagle swim volume consider dynamic unaware deputy middle into physical",
            CreationDatetime = DateTimeOffset.Now,
        };
        // force the saving of the master fingerprint
        internalWallet.MasterFingerprint = internalWallet.MasterFingerprint;
                    
        logger?.LogInformation("Internal wallet setup, seed: {MnemonicString}", internalWallet.MnemonicString);
        return internalWallet;
    }

    private static Key CreateInternalKey(InternalWallet internalWallet, string accountId)
    {
        return new Key
        {
            Name = "FundsManager Co-signing Key",
            XPUB = internalWallet.GetXpubForAccount(accountId),
            InternalWalletId = internalWallet.Id,
            Path = internalWallet.GetKeyPathForAccount(accountId), 
            MasterFingerprint = internalWallet.MasterFingerprint
        }; 
    }

    public static Key CreateUserKey(string keyName, string userId, string walletSeed)
    {
        var masterKey1 = new Mnemonic(walletSeed).DeriveExtKey().GetWif(Network.RegTest);
        var keyPath1 =
            new KeyPath("m/48'/1'/1'"); //https://github.com/dgarage/NBXplorer/blob/0595a87f22c142aee6a6e4a0194f75aec4717819/NBXplorer/Controllers/MainController.cs#L1141
        var accountKey1 = masterKey1.Derive(keyPath1);
        var bitcoinExtPubKey1 = accountKey1.Neuter();
        var accountKeyPath1 = new RootedKeyPath(masterKey1.GetPublicKey().GetHDFingerPrint(), keyPath1);
        var wallet1DerivationScheme = bitcoinExtPubKey1.ToWif();

        return new Key
        {
            Name = keyName,
            UserId = userId,
            XPUB = wallet1DerivationScheme,
            Path = accountKeyPath1.KeyPath.ToString(),
            MasterFingerprint = accountKeyPath1.MasterFingerprint.ToString(),
        };
    }
    
    public static Wallet SingleSig(InternalWallet internalWallet, string accountId = "1")
    {
        var internalWalletKey = CreateInternalKey(internalWallet, accountId);
        return new Wallet
        {
            IsHotWallet = true,
            MofN = 1,
            Keys = new List<Key> { internalWalletKey },
            Name = "Test Singlesig wallet",
            WalletAddressType = WalletAddressType.NativeSegwit,
            InternalWallet = internalWallet,
            InternalWalletId = internalWallet.Id,
            IsFinalised = true,
            CreationDatetime = DateTimeOffset.Now,
            InternalWalletSubDerivationPath = accountId,
            InternalWalletMasterFingerprint = internalWallet.MasterFingerprint
        };
    }

    public static Wallet MultiSig(InternalWallet internalWallet, string accountId = "0", string user1 = "1", string user2 = "2", ILogger? logger = null)
    {
        var wallet1Seed =
            "social mango annual basic work brain economy one safe physical junk other toy valid load cook napkin maple runway island oil fan legend stem";
        var wallet2Seed =
            "solar goat auto bachelor chronic input twin depth fork scale divorce fury mushroom column image sauce car public artist announce treat spend jacket physical";
        
        logger?.LogInformation("Wallet 1 seed: {MnemonicString}", wallet1Seed);
        logger?.LogInformation("Wallet 2 seed: {MnemonicString}", wallet2Seed);

        var user1Key = CreateUserKey("Key 1", user1, wallet1Seed);
        var user2Key = CreateUserKey("Key 2", user2, wallet2Seed);
            
        return MultiSig(internalWallet, accountId, user1Key, user2Key);
    }

    public static Wallet MultiSig(InternalWallet internalWallet, string accountId, Key user1Key, Key user2Key)
    {
        var internalWalletKey = CreateInternalKey(internalWallet, accountId);
        var isLegacy = accountId.Contains('\'');
        var legacyLabel = isLegacy ? "Legacy " : "";
        return new Wallet
        {
            MofN = 2,
            Keys = new List<Key>
                {
                    user1Key,
                    user2Key,
                    internalWalletKey
                },
            Name = $"Test {legacyLabel}Multisig wallet",
            WalletAddressType = WalletAddressType.NativeSegwit,
            InternalWallet = internalWallet,
            InternalWalletId = internalWallet.Id,
            IsFinalised = true,
            CreationDatetime = DateTimeOffset.Now,
            InternalWalletSubDerivationPath = isLegacy ? null : accountId,
            InternalWalletMasterFingerprint = isLegacy ? null : internalWallet.MasterFingerprint
        };
    }

    public static Wallet LegacyMultiSig(InternalWallet internalWallet, string accountId = "1'", string user1 = "1", string user2 = "2", ILogger? logger = null)
    {
        if (!accountId.Contains('\''))
        {
            throw new Exception("Account id must be hardened path");
        }
        return MultiSig(internalWallet, accountId, user1, user2, logger);
    }

    public static Wallet LegacyMultiSig(InternalWallet internalWallet, string accountId, Key user1Key, Key user2Key)
    {
        if (!accountId.Contains('\''))
        {
            throw new Exception("Account id must be hardened path");
        }

        return MultiSig(internalWallet, accountId, user1Key, user2Key);
    }
}