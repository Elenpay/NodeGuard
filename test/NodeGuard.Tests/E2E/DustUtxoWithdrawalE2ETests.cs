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
using FluentAssertions;
using Grpc.Core;
using Grpc.Net.Client;
using NBitcoin;
using NBitcoin.RPC;
using Nodeguard;
using Xunit.Abstractions;

namespace NodeGuard.Tests.E2E;

/// <summary>
/// End-to-end coverage for the dust UTXO protection (MINIMUM_UTXO_VALUE_SATS): a confirmed
/// 546-sat output sent to a NodeGuard hot wallet must be hidden from GetAvailableUtxos and must
/// never be auto-selected as an input of a withdrawal, even though the coin selection picks the
/// newest confirmed UTXO first (which the freshly-mined dust output would otherwise be).
/// Exercised against a LIVE NodeGuard instance + bitcoind.
/// Gated by <see cref="E2EFactAttribute"/> (RUN_E2E_TESTS=1). Connection via env:
///   NODEGUARD_GRPC_ENDPOINT  default http://localhost:50051 (h2c)
///   NODEGUARD_API_TOKEN      default the dev "Liquidator" token
///   BITCOIND_RPC_URL/USER/PASS/WALLET  default http://localhost:18443 / polaruser / polarpass / default
///   E2E_HOT_WALLET_ID        NodeGuard hot wallet to withdraw from (default 3)
/// </summary>
[Trait("Category", "E2E")]
[Collection("E2E")]
public class DustUtxoWithdrawalE2ETests
{
    private const string DefaultDevToken = "8rvSsUGeyXXdDQrHctcTey/xtHdZQEn945KHwccKp9Q=";
    private const long DustAmountSats = 546;
    // The custom NBXplorer selectutxos backend behind GetAvailableUtxos picks UTXOs toward a
    // target amount (amount=0 always yields an empty selection), so every call must request one.
    private const long ProbeAmountSats = 2_000_000;

    private readonly ITestOutputHelper _output;

    public DustUtxoWithdrawalE2ETests(ITestOutputHelper output)
    {
        _output = output;
        AppContext.SetSwitch("System.Net.Http.SocketsHttpHandler.Http2UnencryptedSupport", true);
    }

    [E2EFact]
    public async Task RequestWithdrawal_DoesNotSelectDustUtxo()
    {
        var client = CreateClient(out var headers);
        var rpc = CreateBitcoindRpc();
        var walletId = int.Parse(Env("E2E_HOT_WALLET_ID", "3"));

        // 0. Wait for NodeGuard to be up and for the dev hot wallet to have a spendable UTXO
        //    (DbInitializer funds it with 20 BTC; another E2E test may have it locked temporarily).
        await RetryAsync(async () =>
        {
            var resp = await client.GetNodesAsync(new GetNodesRequest(), headers);
            if (resp.Nodes.Count == 0) throw new InvalidOperationException("no nodes seeded yet");
            return true;
        }, attempts: 90, delay: TimeSpan.FromSeconds(4), what: "GetNodes (NodeGuard readiness)");

        await RetryAsync(async () =>
        {
            var available = await client.GetAvailableUtxosAsync(
                new GetAvailableUtxosRequest { WalletId = walletId, Amount = ProbeAmountSats }, headers);
            if (available.Confirmed.Sum(u => u.Amount) < ProbeAmountSats)
                throw new InvalidOperationException("hot wallet has no spendable UTXO yet");
            return true;
        }, attempts: 60, delay: TimeSpan.FromSeconds(4), what: "GetAvailableUtxos (hot wallet funded)");

        // 1. Send a 546-sat dust output to a fresh address of the wallet and confirm it.
        var addressResponse = await client.GetNewWalletAddressAsync(
            new GetNewWalletAddressRequest { WalletId = walletId, Skip = 0, Reserve = true }, headers);
        var dustAddress = BitcoinAddress.Create(addressResponse.Address, Network.RegTest);
        var dustTxId = await rpc.SendToAddressAsync(dustAddress, Money.Satoshis(DustAmountSats));
        await MineAsync(rpc, 6);

        var dustFundingTx = await rpc.GetRawTransactionAsync(dustTxId);
        var dustVout = dustFundingTx.Outputs.AsIndexedOutputs()
            .Single(o => o.TxOut.ScriptPubKey == dustAddress.ScriptPubKey).N;
        var dustOutpoint = new OutPoint(dustTxId, dustVout);
        _output.WriteLine($"dust UTXO: {dustOutpoint}");

        // 2. The dust UTXO must show up in the raw GetUtxos listing (NBXplorer indexed it)
        //    but must be hidden from the available-for-coin-selection listing.
        await RetryAsync(async () =>
        {
            var all = await client.GetUtxosAsync(new GetUtxosRequest(), headers);
            if (all.Confirmed.All(u => u.Outpoint != dustOutpoint.ToString()))
                throw new InvalidOperationException("dust UTXO not indexed by NBXplorer yet");
            return true;
        }, attempts: 30, delay: TimeSpan.FromSeconds(4), what: "GetUtxos (dust UTXO indexed)");

        // No selection strategy may surface the dust UTXO. SmallestFirst would rank it first, and
        // ClosestToTargetFirst targeting exactly 546 sats is the most adversarial case: the dust
        // UTXO would be the top candidate if it were not filtered out.
        // UpToAmount selects UTXOs whose sum stays UNDER the target, so it gets a ceiling above
        // the wallet balance: with a small target the dust UTXO would be the only one that fits
        // (and the correct result once it is filtered out is an empty selection).
        var strategyProbes = new (COIN_SELECTION_STRATEGY Strategy, long AmountSats)[]
        {
            (COIN_SELECTION_STRATEGY.SmallestFirst, ProbeAmountSats),
            (COIN_SELECTION_STRATEGY.BiggestFirst, ProbeAmountSats),
            (COIN_SELECTION_STRATEGY.ClosestToTargetFirst, ProbeAmountSats),
            (COIN_SELECTION_STRATEGY.UpToAmount, Money.Coins(25m).Satoshi),
        };
        foreach (var (strategy, amountSats) in strategyProbes)
        {
            var availableUtxos = await client.GetAvailableUtxosAsync(new GetAvailableUtxosRequest
            {
                WalletId = walletId,
                Strategy = strategy,
                Amount = amountSats,
                ClosestTo = DustAmountSats,
            }, headers);
            availableUtxos.Confirmed.Should().NotBeEmpty(
                $"the wallet's non-dust UTXOs must still be available with strategy {strategy}");
            availableUtxos.Confirmed.Select(u => u.Outpoint).Should().NotContain(dustOutpoint.ToString(),
                $"dust UTXOs must not be offered for coin selection with strategy {strategy}");
        }

        // With a target below any real UTXO, UpToAmount's only fitting candidate is the dust
        // UTXO — the selection must come back empty rather than surface it.
        var upToDustOnly = await client.GetAvailableUtxosAsync(new GetAvailableUtxosRequest
        {
            WalletId = walletId,
            Strategy = COIN_SELECTION_STRATEGY.UpToAmount,
            Amount = ProbeAmountSats,
        }, headers);
        upToDustOnly.Confirmed.Should().BeEmpty(
            "no UTXO other than the (filtered) dust fits an UpToAmount target below the smallest real UTXO");

        // 3. Request an automatic withdrawal: no explicit outpoints, so coin selection runs.
        var destination = await rpc.GetNewAddressAsync();
        var withdrawal = await client.RequestWithdrawalAsync(new RequestWithdrawalRequest
        {
            WalletId = walletId,
            Description = "E2E dust protection test",
            Destinations = { new Destination { Address = destination.ToString(), AmountSats = 1_000_000 } },
            MempoolFeeRate = FEES_TYPE.CustomFee,
            CustomFeeRate = 2,
        }, headers);
        _output.WriteLine($"withdrawal request {withdrawal.RequestId} → txid {withdrawal.Txid}");
        withdrawal.IsHotWallet.Should().BeTrue();

        // 4. The hot wallet signs and broadcasts asynchronously (PerformWithdrawalJob); wait for
        //    the tx to hit the mempool and assert the dust outpoint is not among its inputs.
        var withdrawalTx = await RetryAsync(async () =>
        {
            var tx = await rpc.GetRawTransactionAsync(uint256.Parse(withdrawal.Txid), throwIfNotFound: false);
            return tx ?? throw new InvalidOperationException("withdrawal tx not broadcast yet");
        }, attempts: 30, delay: TimeSpan.FromSeconds(4), what: "GetRawTransaction (withdrawal broadcast)");

        withdrawalTx.Inputs.Select(i => i.PrevOut).Should().NotContain(dustOutpoint,
            "a freshly-confirmed dust UTXO would be the first pick of SelectUTXOsByOldest if it were not filtered out");

        // 5. The dust UTXO must remain unspent (and unlocked) after the withdrawal.
        var allUtxos = await client.GetUtxosAsync(new GetUtxosRequest(), headers);
        allUtxos.Confirmed.Select(u => u.Outpoint).Should().Contain(dustOutpoint.ToString(),
            "the dust UTXO must be left untouched in the wallet");
    }

    // ---- helpers -----------------------------------------------------------------------------

    private async Task MineAsync(RPCClient rpc, int blocks)
    {
        var addr = await rpc.GetNewAddressAsync();
        await rpc.GenerateToAddressAsync(blocks, addr);
    }

    private async Task<T> RetryAsync<T>(Func<Task<T>> action, int attempts, TimeSpan delay, string what)
    {
        Exception? last = null;
        for (var i = 0; i < attempts; i++)
        {
            try { return await action(); }
            catch (Exception ex) { last = ex; _output.WriteLine($"{what} attempt {i + 1}/{attempts} failed: {ex.Message}"); }
            await Task.Delay(delay);
        }
        throw new InvalidOperationException($"{what} did not succeed after {attempts} attempts", last);
    }

    private static NodeGuardService.NodeGuardServiceClient CreateClient(out Metadata headers)
    {
        var endpoint = Env("NODEGUARD_GRPC_ENDPOINT", "http://localhost:50051");
        headers = new Metadata { { "auth-token", Env("NODEGUARD_API_TOKEN", DefaultDevToken) } };
        return new NodeGuardService.NodeGuardServiceClient(GrpcChannel.ForAddress(endpoint));
    }

    private static RPCClient CreateBitcoindRpc()
    {
        var url = Env("BITCOIND_RPC_URL", "http://localhost:18443");
        var cred = new NetworkCredential(Env("BITCOIND_RPC_USER", "polaruser"), Env("BITCOIND_RPC_PASS", "polarpass"));
        var rpc = new RPCClient(cred, new Uri(url), Network.RegTest);
        return rpc.SetWalletContext(Env("BITCOIND_RPC_WALLET", "default"));
    }

    private static string Env(string name, string fallback)
        => Environment.GetEnvironmentVariable(name) is { Length: > 0 } v ? v : fallback;
}
