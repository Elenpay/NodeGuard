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
/// End-to-end coverage for FeeSats: as soon as a withdrawal's tx is finalised and broadcast,
/// NodeGuard must compute the mining fee from the finalised PSBT and surface it via
/// GetWithdrawalsRequestStatus. Verified against the actual mining fee (sum of inputs' prevout
/// values minus sum of the tx's output values) read from bitcoind.
/// Exercised against a LIVE NodeGuard instance + bitcoind.
/// Gated by <see cref="E2EFactAttribute"/> (RUN_E2E_TESTS=1). Connection via env:
///   NODEGUARD_GRPC_ENDPOINT  default http://localhost:50051 (h2c)
///   NODEGUARD_API_TOKEN      default the dev "Liquidator" token
///   BITCOIND_RPC_URL/USER/PASS/WALLET  default http://localhost:18443 / polaruser / polarpass / default
///   E2E_HOT_WALLET_ID        NodeGuard hot wallet to withdraw from (default 3)
/// </summary>
[Trait("Category", "E2E")]
[Collection("E2E")]
public class WithdrawalFeeSatsE2ETests
{
    private const string DefaultDevToken = "8rvSsUGeyXXdDQrHctcTey/xtHdZQEn945KHwccKp9Q=";

    private readonly ITestOutputHelper _output;

    public WithdrawalFeeSatsE2ETests(ITestOutputHelper output)
    {
        _output = output;
        AppContext.SetSwitch("System.Net.Http.SocketsHttpHandler.Http2UnencryptedSupport", true);
    }

    [E2EFact]
    public async Task Withdrawal_Broadcast_ReportsFeeSats()
    {
        var client = CreateClient(out var headers);
        var rpc = CreateBitcoindRpc();
        var walletId = int.Parse(Env("E2E_HOT_WALLET_ID", "3"));

        // 0. Wait for NodeGuard to be up and for the dev hot wallet to have spendable funds.
        await RetryAsync(async () =>
        {
            var resp = await client.GetNodesAsync(new GetNodesRequest(), headers);
            if (resp.Nodes.Count == 0) throw new InvalidOperationException("no nodes seeded yet");
            return true;
        }, attempts: 90, delay: TimeSpan.FromSeconds(4), what: "GetNodes (NodeGuard readiness)");

        // Other E2E tests sharing this collection spend down the wallet's one-time dev seed, so
        // top it up before relying on it for this withdrawal.
        await E2EFundingHelper.FundHotWalletAsync(client, headers, rpc, walletId, Money.Coins(1m), _output.WriteLine);

        // 1. Request an automatic withdrawal from the hot wallet.
        var destination = await rpc.GetNewAddressAsync();
        var withdrawal = await client.RequestWithdrawalAsync(new RequestWithdrawalRequest
        {
            WalletId = walletId,
            Description = "E2E fee paid test",
            Destinations = { new Destination { Address = destination.ToString(), AmountSats = 500_000 } },
            MempoolFeeRate = FEES_TYPE.CustomFee,
            CustomFeeRate = 2,
        }, headers);
        _output.WriteLine($"withdrawal request {withdrawal.RequestId} -> txid {withdrawal.Txid}");
        withdrawal.IsHotWallet.Should().BeTrue();

        // 2. The hot wallet signs and broadcasts asynchronously; wait for the tx to hit the mempool.
        var withdrawalTx = await RetryAsync(async () =>
        {
            var tx = await rpc.GetRawTransactionAsync(uint256.Parse(withdrawal.Txid), throwIfNotFound: false);
            return tx ?? throw new InvalidOperationException("withdrawal tx not broadcast yet");
        }, attempts: 30, delay: TimeSpan.FromSeconds(4), what: "GetRawTransaction (withdrawal broadcast)");

        // 3. Compute the real mining fee from bitcoind: sum(input prevout values) - sum(output values).
        long inputSats = 0;
        foreach (var input in withdrawalTx.Inputs)
        {
            var prevTx = await rpc.GetRawTransactionAsync(input.PrevOut.Hash);
            inputSats += prevTx.Outputs[input.PrevOut.N].Value.Satoshi;
        }
        var outputSats = withdrawalTx.Outputs.Sum(o => o.Value.Satoshi);
        var expectedFeeSats = inputSats - outputSats;
        _output.WriteLine($"expected fee from bitcoind: {expectedFeeSats} sats");
        expectedFeeSats.Should().BePositive();

        // 4. FeeSats is computed from the finalised PSBT as soon as the tx is broadcast — the API
        //    does not gate it on confirmations, so it must already be set here.
        var status = await client.GetWithdrawalsRequestStatusAsync(
            new GetWithdrawalsRequestStatusRequest { RequestIds = { withdrawal.RequestId } }, headers);
        var request = status.WithdrawalRequests.Single();

        _output.WriteLine($"FeeSats={request.FeeSats} (HasFeeSats={request.HasFeeSats}) status={request.Status}");
        request.HasFeeSats.Should().BeTrue("FeeSats is set as soon as the tx is broadcast, regardless of confirmations");
        request.FeeSats.Should().Be(expectedFeeSats,
            "FeeSats must match the actual mining fee paid by the broadcast transaction");
    }

    // ---- helpers -----------------------------------------------------------------------------

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
