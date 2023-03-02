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
using System.Net;
using FundsManager.Data.Models;
using FundsManager.Data.Repositories.Interfaces;
using FundsManager.Helpers;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using NBitcoin;
using NBitcoin.RPC;
using NBXplorer;
using NBXplorer.DerivationStrategy;
using NBXplorer.Models;
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

    private static Key CreateUserKey(string keyName, string userId, string walletSeed)
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
    
    public static Wallet SingleSig(InternalWallet internalWallet)
    {
        var internalWalletKey = CreateInternalKey(internalWallet, "1");
        return new Wallet
        {
            IsHotWallet = true,
            MofN = 1,
            Keys = new List<Key> { internalWalletKey },
            Name = "Test wallet",
            WalletAddressType = WalletAddressType.NativeSegwit,
            InternalWallet = internalWallet,
            InternalWalletId = internalWallet.Id,
            IsFinalised = true,
            CreationDatetime = DateTimeOffset.Now,
            InternalWalletSubDerivationPath = "1"
        };
    }
    
    public static Wallet MultiSig(InternalWallet internalWallet, ILogger? logger = null)
    {
        var internalWalletKey = CreateInternalKey(internalWallet, "0");
        var wallet1seed =
                        "social mango annual basic work brain economy one safe physical junk other toy valid load cook napkin maple runway island oil fan legend stem";
        var wallet2seed =
            "solar goat auto bachelor chronic input twin depth fork scale divorce fury mushroom column image sauce car public artist announce treat spend jacket physical";
        
        logger?.LogInformation("Wallet 1 seed: {MnemonicString}", wallet1seed);
        logger?.LogInformation("Wallet 2 seed: {MnemonicString}", wallet2seed);

        return new Wallet
        {
            MofN = 2,
            Keys = new List<Key>
                {
                    CreateUserKey("Key 1", "1", wallet1seed),
                    CreateUserKey("Key 2", "2", wallet2seed),
                    internalWalletKey
                },
            Name = "Test wallet",
            WalletAddressType = WalletAddressType.NativeSegwit,
            InternalWallet = internalWallet,
            InternalWalletId = internalWallet.Id,
            IsFinalised = true,
            CreationDatetime = DateTimeOffset.Now,
            InternalWalletSubDerivationPath = "0"
        };
    }
    
    public static Wallet LegacyMultiSig(InternalWallet internalWallet, ILogger? logger = null)
    {
        var internalWalletKey = CreateInternalKey(internalWallet, "1'");
        
        var wallet1seed =
                        "wrong judge easily street crazy blouse royal employ spoon split curtain food vapor amazing grow funny false rather table keen arrest wash word define";
        var wallet2seed =
            "air pluck stool maximum oven mimic bulb tonight boat alarm chair fresh keen course trumpet ranch envelope wealth wood holiday patrol vague put square";

        logger?.LogInformation("Wallet 1 seed: {MnemonicString}", wallet1seed);
        logger?.LogInformation("Wallet 2 seed: {MnemonicString}", wallet2seed);

        return new Wallet
        {
            MofN = 2,
            Keys = new List<Key>
                {
                    CreateUserKey("Key 1", "1", wallet1seed),
                    CreateUserKey("Key 2", "2", wallet2seed),
                    internalWalletKey
                },
            Name = "Legacy Test wallet",
            WalletAddressType = WalletAddressType.NativeSegwit,
            InternalWallet = internalWallet,
            InternalWalletId = internalWallet.Id,
            IsFinalised = true,
            CreationDatetime = DateTimeOffset.Now,
        };
    }
}