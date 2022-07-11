using FluentAssertions;
using NBitcoin;
using NBitcoin.DataEncoders;
using NBitcoin.Tests;

namespace FundsManager.Tests
{
    public class NBITCOINPlaygroundTests

    {
        [Fact]
        public void NodeTest()
        {
            using (var env = NodeBuilder.Create(NodeDownloadData.Bitcoin.v22_0, Network.RegTest))
            {
                // Removing node logs from output
                env.ConfigParameters.Add("printtoconsole", "0");

                var alice = env.CreateNode();
                var bob = env.CreateNode();
                var miner = env.CreateNode();
                env.StartAll();
                Console.WriteLine("Created 3 nodes (alice, bob, miner)");

                Console.WriteLine("Connect nodes to each other");
                miner.Sync(alice, true);
                miner.Sync(bob, true);

                Console.WriteLine("Generate 101 blocks so miner can spend money");
                var minerRPC = miner.CreateRPCClient();
                miner.Generate(101);

                var aliceRPC = alice.CreateRPCClient();
                var bobRPC = bob.CreateRPCClient();
                var bobAddress = bobRPC.GetNewAddress();

                Console.WriteLine("Alice gets money from miner");
                var aliceAddress = aliceRPC.GetNewAddress();
                minerRPC.SendToAddress(aliceAddress, Money.Coins(20m));

                Console.WriteLine("Mine a block and check that alice is now synched with the miner (same block height)");
                minerRPC.Generate(1);
                alice.Sync(miner);

                Console.WriteLine($"Alice Balance: {aliceRPC.GetBalance()}");

                Console.WriteLine("Alice send 1 BTC to bob");
                aliceRPC.SendToAddress(bobAddress, Money.Coins(1.0m));
                Console.WriteLine($"Alice mine her own transaction");
                aliceRPC.Generate(1);

                alice.Sync(bob);

                Console.WriteLine($"Alice Balance: {aliceRPC.GetBalance()}");
                Console.WriteLine($"Bob Balance: {bobRPC.GetBalance()}");
            }
        }

        [Fact]
        public void PSBTTest()
        {
            using var env = NodeBuilder.Create(NodeDownloadData.Bitcoin.v22_0, Network.RegTest);
            // Removing node logs from output
            env.ConfigParameters.Add("printtoconsole", "0");

            var bob = env.CreateNode();
            var miner = env.CreateNode();
            miner.ConfigParameters.Add("txindex", "1"); // So we can query a tx from txid
            env.StartAll();
            Console.WriteLine("Created 3 nodes (alice, bob, miner)");

            Console.WriteLine("Connect nodes to each other");
            miner.Sync(bob, true);

            Console.WriteLine("Generate 101 blocks so miner can spend money");
            var minerRPC = miner.CreateRPCClient();
            miner.Generate(101);

            var bobRPC = bob.CreateRPCClient();
            var bobAddress = bobRPC.GetNewAddress();

            Console.WriteLine("Multisig adress by Alice and Bob gets money from miner");

            var mnemonic1 = new Mnemonic("social mango annual basic work brain economy one safe physical junk other toy valid load cook napkin maple runway island oil fan legend stem");
            var extKey1 = mnemonic1.DeriveExtKey().GetWif(Network.RegTest);

            var accountKey1 = extKey1.Derive(new KeyPath("m/84'/1'/0'"));
            var aliceKey = accountKey1.PrivateKey;
            var mnemonic2 = new Mnemonic("solar goat auto bachelor chronic input twin depth fork scale divorce fury mushroom column image sauce car public artist announce treat spend jacket physical");
            var extKey2 = mnemonic2.DeriveExtKey().GetWif(Network.RegTest);
            var bobKey = extKey2.PrivateKey;

            //2-of-2 multisig by Bob and Alice
            var multisigScriptPubKey = PayToMultiSigTemplate.Instance.GenerateScriptPubKey(2, new[] { aliceKey.PubKey, bobKey.PubKey });

            var multisigAddress = multisigScriptPubKey.WitHash.GetAddress(Network.RegTest);

            var multisigFundCoins = Money.Coins(20m);
            var minerToMultisigTxId = minerRPC.SendToAddress(multisigAddress, multisigFundCoins);

            Console.WriteLine("Mine a block and check that alice is now synched with the miner (same block height)");
            minerRPC.Generate(1);

            var minerToAliceTx = minerRPC.GetRawTransaction(minerToMultisigTxId);
            var multisigUTXOs = minerToAliceTx.Outputs.AsCoins()
                            .Where(c => c.ScriptPubKey == multisigAddress.ScriptPubKey)
                            .ToDictionary(c => c.Outpoint, c => c);

            var txBuilder = Network.RegTest.CreateTransactionBuilder();
            var feesCoins = Money.Coins(0.00001m);
            var channelFundingPSBT = txBuilder
                .AddCoin(multisigUTXOs.First().Value.ToScriptCoin(multisigScriptPubKey))
                .SetChange(multisigScriptPubKey)
                .SetSigningOptions(SigHash.None)
                .SendFees(feesCoins)
                .BuildPSBT(false);

            //TODO Report this issue on github -> hack
            channelFundingPSBT.Settings.SigningOptions.SigHash = SigHash.None;

            channelFundingPSBT.Settings.SigningOptions.SigHash.Should().Be(SigHash.None);

            var psbtBase64 = channelFundingPSBT.ToBase64();

            psbtBase64.Should().NotBeEmpty();

            //Alice and bob signs

            var alicePSBT = channelFundingPSBT.SignWithKeys(aliceKey);

            alicePSBT.Inputs.First().PartialSigs.Count.Should().Be(1);

            var bobPSBT = channelFundingPSBT.SignWithKeys(bobKey);
            bobPSBT.Inputs.First().PartialSigs.Count.Should().Be(2);

            //We combine the PSBTs and finalize it
            var finalizedPSBT = alicePSBT.Combine(bobPSBT).Finalize();

            finalizedPSBT.AssertSanity();

            var channelfundingTx = finalizedPSBT.ExtractTransaction();

            var fundingMoney = new Money(19, MoneyUnit.BTC);

            //Temp tx to calculate the change
            var temptx = txBuilder
                .AddCoin(multisigUTXOs.First().Value.ToScriptCoin(multisigScriptPubKey))
                .AddKeys(new[] { aliceKey, bobKey })
                .SendFees(feesCoins)
                .SendAllRemainingToChange()
                .Send(bobAddress, fundingMoney)
                .SetChange(multisigScriptPubKey)
                .SetSigningOptions(SigHash.None)
                .BuildTransaction(true);

            //We add a the output, this could be a channel funding address in this case bobaddress

            var channelFundingTxOut = new TxOut(fundingMoney, bobAddress);
            channelfundingTx.Outputs.Add(channelFundingTxOut);

            channelfundingTx.Outputs[0].Value = temptx.Outputs[1].Value;
            channelfundingTx.Outputs[1].Value = fundingMoney;

            var check = channelfundingTx.Check();
            check.Should().Be(TransactionCheckResult.Success);

            var bobInitialBalance = bobRPC.GetBalance().Satoshi;

            bobInitialBalance.Should().Be(0L);

            //Channel funding broadcast
            minerRPC.SendRawTransaction(channelfundingTx);
            minerRPC.Generate(1);

            var bobActualBalance = bobRPC.GetBalance();

            bobActualBalance.ToDecimal(MoneyUnit.BTC).Should()
                .Be(19M);
        }

        [Fact]
        public void XPRVtoVPRV()
        {
            var xprv =
                "tprv8ZgxMBicQKsPduvXYAnkop1b1UoAY2pS68pe9jHuJwuMvx6G5sh4C67peYZkRawdBWbMbfoybgQJ3g8nTZAezEeHyaW9A9UjtpTRmSyJwUn";

            var multisigVprv = tprvToVprv(xprv);

            multisigVprv.Should().Contain("Vprv");

            var xprv2 =
                "tprv8ZgxMBicQKsPdCVYPYV98ANgxLfD7MRWP3UjYdVgX3h5NB6J1VD6URUjTQe8Uaj9ifjyVuaWBhrPZnrX5gRRwE26sdDQcRvkqfYnHK3Aqv7";

            var multisigVprv2 = tprvToVprv(xprv2);

            multisigVprv2.Should().Contain("Vprv");
        }

        [Fact]
        public void xprvtovPRV()
        {
            var xprv =
                "tprv8ZgxMBicQKsPduvXYAnkop1b1UoAY2pS68pe9jHuJwuMvx6G5sh4C67peYZkRawdBWbMbfoybgQJ3g8nTZAezEeHyaW9A9UjtpTRmSyJwUn";

            var multisigVprv = tprvToVprv(xprv);

            multisigVprv.Should().Contain("Vprv");

            var xprv2 =
                "tprv8ZgxMBicQKsPdCVYPYV98ANgxLfD7MRWP3UjYdVgX3h5NB6J1VD6URUjTQe8Uaj9ifjyVuaWBhrPZnrX5gRRwE26sdDQcRvkqfYnHK3Aqv7";

            var multisigVprv2 = tprvToVprv(xprv2);

            multisigVprv2.Should().Contain("Vprv");
        }

        private static string tprvToVprv(string xprv)
        {
            var encoder = new Base58Encoder();
            var xprvBytes = encoder.DecodeData(xprv);

            var strippedVersionBytes = new byte[xprvBytes.Length - 4];
            Buffer.BlockCopy(xprvBytes, 4, strippedVersionBytes, 0, strippedVersionBytes.Length);

            var multisigVprvVersionBytes =
                Convert.FromHexString("02575048"); //SLIP-0132 : Registered HD version bytes for BIP-0032

            var newVprvBytes = multisigVprvVersionBytes.Concat(strippedVersionBytes).ToArray();

            var multisigVprv = encoder.EncodeData(newVprvBytes);
            return multisigVprv;
        }

        private static string tprvTovprv(string xprv)
        {
            var encoder = new Base58Encoder();
            var xprvBytes = encoder.DecodeData(xprv);

            var strippedVersionBytes = new byte[xprvBytes.Length - 4];
            Buffer.BlockCopy(xprvBytes, 4, strippedVersionBytes, 0, strippedVersionBytes.Length);

            var multisigVprvVersionBytes =
                Convert.FromHexString("045f18bc"); //SLIP-0132 : Registered HD version bytes for BIP-0032

            var newVprvBytes = multisigVprvVersionBytes.Concat(strippedVersionBytes).ToArray();

            var multisigVprv = encoder.EncodeData(newVprvBytes);
            return multisigVprv;
        }
    }
}