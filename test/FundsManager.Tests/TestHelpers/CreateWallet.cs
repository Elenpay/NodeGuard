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

public class CreateWallet
{
    public static Wallet CreateTestWallet()
    {
        var internalWallet = new InternalWallet
        {
            DerivationPath = Constants.DEFAULT_DERIVATION_PATH,
            MnemonicString =
                            "middle teach digital prefer fiscal theory syrup enter crash muffin easily anxiety ill barely eagle swim volume consider dynamic unaware deputy middle into physical",
            CreationDatetime = DateTimeOffset.Now,
        };

        var internalWalletKey = new Key
        {
            Name = "FundsManager Co-signing Key",
            XPUB = internalWallet.XPUB,
            InternalWalletId = internalWallet.Id,
            Path = internalWallet.DerivationPath,
            MasterFingerprint = new Mnemonic(internalWallet.MnemonicString).DeriveExtKey().GetWif(Network.RegTest).GetPublicKey().GetHDFingerPrint().ToString()
        };

        var wallet1seed =
                        "social mango annual basic work brain economy one safe physical junk other toy valid load cook napkin maple runway island oil fan legend stem";

        var masterKey1 = new Mnemonic(wallet1seed).DeriveExtKey().GetWif(Network.RegTest);
        var keyPath1 =
            new KeyPath(Constants.DEFAULT_DERIVATION_PATH); //https://github.com/dgarage/NBXplorer/blob/0595a87f22c142aee6a6e4a0194f75aec4717819/NBXplorer/Controllers/MainController.cs#L1141
        var accountKey1 = masterKey1.Derive(keyPath1);
        var bitcoinExtPubKey1 = accountKey1.Neuter();
        var accountKeyPath1 = new RootedKeyPath(masterKey1.GetPublicKey().GetHDFingerPrint(), keyPath1);

        var wallet1DerivationScheme = bitcoinExtPubKey1.ToWif();

        var wallet2seed =
            "solar goat auto bachelor chronic input twin depth fork scale divorce fury mushroom column image sauce car public artist announce treat spend jacket physical";

        var masterKey2 = new Mnemonic(wallet2seed).DeriveExtKey().GetWif(Network.RegTest);
        var keyPath2 =
            new KeyPath(Constants.DEFAULT_DERIVATION_PATH); //https://github.com/dgarage/NBXplorer/blob/0595a87f22c142aee6a6e4a0194f75aec4717819/NBXplorer/Controllers/MainController.cs#L1141
        var accountKey2 = masterKey2.Derive(keyPath2);
        var bitcoinExtPubKey2 = accountKey2.Neuter();
        var accountKeyPath2 = new RootedKeyPath(masterKey2.GetPublicKey().GetHDFingerPrint(), keyPath2);

        var wallet2DerivationScheme = bitcoinExtPubKey2.ToWif();

        var testingMultisigWallet = new Wallet
        {
            MofN = 2,
            Keys = new List<Key>
                {
                    new Key
                    {
                        Name = "Key 1",
                        UserId = "1",
                        XPUB = wallet1DerivationScheme,
                        Path = accountKeyPath1.KeyPath.ToString(),
                        MasterFingerprint = accountKeyPath1.MasterFingerprint.ToString(),
                    },
                    new Key
                    {
                        Name = "Key 2",
                        UserId = "2",
                        XPUB = wallet2DerivationScheme,
                        Path = accountKeyPath2.KeyPath.ToString(),
                        MasterFingerprint = accountKeyPath2.MasterFingerprint.ToString(),
                    },
                    internalWalletKey
                },
            Name = "Test wallet",
            WalletAddressType = WalletAddressType.NativeSegwit,
            InternalWallet = internalWallet,
            InternalWalletId = internalWallet.Id,
            IsFinalised = true,
            CreationDatetime = DateTimeOffset.Now
        };

        return testingMultisigWallet;
    }
}