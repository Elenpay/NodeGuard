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

using FluentAssertions;
using NodeGuard.Data.Models;
using NodeGuard.Helpers;
using NodeGuard.TestHelpers;
using NBitcoin;
using NBXplorer.Models;
using UTXO = NBXplorer.Models.UTXO;

namespace NodeGuard.Tests
{
    public class LightningHelperTests
    {
        private InternalWallet _internalWallet = CreateWallet.CreateInternalWallet();

        [Fact]
        public void UTXODuplicateTest_Duplicated()
        {
            //Arrange
            var utxoFixture = new UTXO
            {
                Value = new Money(10),
                Outpoint = new OutPoint(uint256.Parse("2cf4255a9860746bd9bb432feb4cf635dcf96435162f58ccd8283f453e0c7679"),1),
                Index =1,
                ScriptPubKey = new Script()
            };

            var utxoChanges = new UTXOChanges
            {
                Confirmed = new UTXOChange
                {
                    UTXOs = new List<UTXO>(new List<UTXO>
                    {
                        utxoFixture, utxoFixture, utxoFixture
                    })
                }
            };
            //Act

            utxoChanges.RemoveDuplicateUTXOs();
            //Assert

            utxoChanges.Confirmed.UTXOs.Count.Should().Be(1);
        }

        [Fact]
        public void UTXODuplicateTest_NoDuplicated()
        {
            //Arrange
            var utxoFixture = new UTXO
            {
                Value = new Money(10),
                Outpoint = new OutPoint(uint256.Parse("2cf4255a9860746bd9bb432feb4cf635dcf96435162f58ccd8283f453e0c7679"), 1),
                Index =1,
                ScriptPubKey = new Script()
            };

            var utxoChanges = new UTXOChanges
            {
                Confirmed = new UTXOChange
                {
                    UTXOs = new List<UTXO>(new List<UTXO>
                    {
                        utxoFixture
                    })
                }
            };
            //Act

            utxoChanges.RemoveDuplicateUTXOs();
            //Assert

            utxoChanges.Confirmed.UTXOs.Count.Should().Be(1);
        }

        [Fact]
        public void AddDerivationData_MultisigSucceeds()
        {
            // Arrange
            var wallet = CreateWallet.MultiSig(_internalWallet);

            var utxo = new UTXO()
            {
                Value = new Money((long)10000000000),
                ScriptPubKey = wallet.GetDerivationStrategy().GetDerivation(KeyPath.Parse("0/0")).ScriptPubKey,
                KeyPath = KeyPath.Parse("0/0")
            };
            var utxoList = new List<UTXO>() { utxo };
            var coins = utxoList
                .Select<UTXO, ICoin>(x => x.AsCoin(wallet.GetDerivationStrategy()).ToScriptCoin(x.ScriptPubKey))
                .ToList();

            var network = Network.RegTest;
            var destinationAddress = BitcoinAddress.Create("bcrt1q83ml8tve8vh672wsm83getxfzetaquq352jr6t423tdwjvdz3f3qe4r4t7", network);
            var changeAddress = BitcoinAddress.Create("bcrt1q8k3av6q5yp83rn332lx8a90k6kukhg28hs5qw7krdq95t629hgsqk6ztmf", network);
            
            var txBuilder = network.CreateTransactionBuilder();
            txBuilder.SetSigningOptions(SigHash.All)
                .SetChange(changeAddress)
                .AddCoins(coins)
                .Send(destinationAddress, 10000)
                .SendAllRemainingToChange();
            txBuilder.ShuffleOutputs = false;
            var cleanPsbt = txBuilder.BuildPSBT(false);

            // Act
            var result = LightningHelper.AddDerivationData(wallet, cleanPsbt, utxoList, coins);

            // Assert
            var psbt = PSBT.Parse("cHNidP8BAIkBAAAAAQAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA/////wD/////AhAnAAAAAAAAIgAgPHfzrZk7L68p0NnijKzJFlfQcBGipD0uqora6TGiimLwvAtUAgAAACIAID2j1mgUIE8RzjFXzH6V9tW5a6FHvCgHesNoC0XpRbogAAAAAE8BBDWHzwMvESQsgAAAAfw77kI6AYzrbSJqBmMojtD7XuD6nXkKs3DQMOBHMObIA4COLhzUgr3QcZaUPFqBM9Fpr4YCK2uwOBdxZE7AdETXEB/M5N4wAACAAQAAgAEAAIBPAQQ1h88DVqwD9IAAAAH5CK5KZrD/oasUtVrwzkjypwIly5AQkC1pAa+QuT6PgQJRrxXgW7i36sGJWz9fR//v7NgyGgLvIimPidCiA33wYBBg86CzMAAAgAEAAIABAACATwEENYfPA325Ro0AAAAAgN63GqLxTu1/NyL0SV4a0Hn1n8Dzg+Wye9nbb16ZISADr+s+pcKnDcSqKHKWSl4v8Rcq80ZqG/7QObYmZUl/xUYQ7QIQyDAAAIABAACAAAAAAAABASsA5AtUAgAAACIAINCp0IUCw4KZ8J/JokbAV1TBQtK4m6WLzUomP5VBhszOAQVpUiEC2FTFYM/mwE4L60Q0G2p5QElV7YlMD7fcgoJEH79pLLEhAwJn/wsRl0hvcYj5Y3Bv3uQlxZ57pBZ9KSeuEPVNmjS/IQNvzitZiz5ksZFSQuRibjPP4pwo+OWOqZLBL2x5ZrFVqVOuIgYC2FTFYM/mwE4L60Q0G2p5QElV7YlMD7fcgoJEH79pLLEYH8zk3jAAAIABAACAAQAAgAAAAAAAAAAAIgYDAmf/CxGXSG9xiPljcG/e5CXFnnukFn0pJ64Q9U2aNL8YYPOgszAAAIABAACAAQAAgAAAAAAAAAAAIgYDb84rWYs+ZLGRUkLkYm4zz+KcKPjljqmSwS9seWaxVakY7QIQyDAAAIABAACAAAAAAAAAAAAAAAAAAAAA", network);
            result.Should().BeEquivalentTo(psbt);
        }
        
        [Fact]
        public void AddDerivationData_SinglesigSucceeds()
        {
            // Arrange
            var wallet = CreateWallet.SingleSig(_internalWallet);

            var utxo = new UTXO()
            {
                Value = new Money((long)10000000000),
                ScriptPubKey = wallet.GetDerivationStrategy().GetDerivation(KeyPath.Parse("0/0")).ScriptPubKey,
                KeyPath = KeyPath.Parse("0/0")
            };
            var utxoList = new List<UTXO>() { utxo };
            var coins = utxoList
                .Select<UTXO, ICoin>(x => x.AsCoin(wallet.GetDerivationStrategy()))
                .ToList();

            var network = Network.RegTest;
            var destinationAddress = BitcoinAddress.Create("bcrt1q83ml8tve8vh672wsm83getxfzetaquq352jr6t423tdwjvdz3f3qe4r4t7", network);
            var changeAddress = BitcoinAddress.Create("bcrt1q8k3av6q5yp83rn332lx8a90k6kukhg28hs5qw7krdq95t629hgsqk6ztmf", network);
            
            var txBuilder = network.CreateTransactionBuilder();
            txBuilder.SetSigningOptions(SigHash.All)
                .SetChange(changeAddress)
                .AddCoins(coins)
                .Send(destinationAddress, 10000)
                .SendAllRemainingToChange();
            txBuilder.ShuffleOutputs = false;
            var cleanPsbt = txBuilder.BuildPSBT(false);

            // Act
            var result = LightningHelper.AddDerivationData(wallet, cleanPsbt, utxoList, coins);

            // Assert
            var psbt = PSBT.Parse(  "cHNidP8BAIkBAAAAAQAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA/////wD/////AhAnAAAAAAAAIgAgPHfzrZk7L68p0NnijKzJFlfQcBGipD0uqora6TGiimLwvAtUAgAAACIAID2j1mgUIE8RzjFXzH6V9tW5a6FHvCgHesNoC0XpRbogAAAAAE8BBDWHzwN9uUaNAAAAAYPR/OiA1LbTzxbLPvbXvtAwckIG3g+0T1zblR/ZodaiA5zBFsigPpL8htN/KJ/Ph8SPvQA/K+mSNXTSA0hgvPNuEO0CEMgwAACAAQAAgAEAAAAAAQEfAOQLVAIAAAAWABTpOvUBMqNMfl7P81etji6x4fXrMyIGA3uD9HVjgF5E+eQhHp+Na6femVYpc4bCA4DmimehAdWcGO0CEMgwAACAAQAAgAEAAAAAAAAAAAAAAAAAAA==", network);
            result.Should().BeEquivalentTo(psbt);
        }
    }
}