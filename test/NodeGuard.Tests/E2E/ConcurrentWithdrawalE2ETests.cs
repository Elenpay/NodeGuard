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
using System.Net.Http.Json;
using FluentAssertions;
using Grpc.Core;
using Grpc.Net.Client;
using NBitcoin;
using NBitcoin.RPC;
using Nodeguard;
using Xunit.Abstractions;

namespace NodeGuard.Tests.E2E;

/// <summary>
/// Coin selection must be safe for concurrent withdrawals: multiple withdrawal requests from the
/// SAME wallet to DIFFERENT destinations, submitted at the same time, must each end up with their
/// own disjoint set of UTXOs. If two of them selected the same UTXO, one transaction would
/// double-spend/RBF-replace the other on the network. This covers both how a UTXO can end up
/// selected: automatically (coin selection picks it) and explicitly (a caller names its outpoint).
/// The automatic-selection test funds the wallet with one distinct confirmed UTXO per withdrawal,
/// fires all the withdrawal requests concurrently, and decodes each resulting transaction's inputs
/// straight from the mempool (no mining/confirmation needed, since NodeGuard signs and broadcasts
/// hot-wallet withdrawals synchronously) to assert no input is ever picked by two of them. The
/// explicit-selection test funds a single UTXO and fires two concurrent requests that both name
/// its exact outpoint, asserting exactly one wins and the other is rejected.
/// Exercised against a LIVE NodeGuard instance + bitcoind.
/// Gated by <see cref="E2EFactAttribute"/> (RUN_E2E_TESTS=1). Connection via env:
///   NODEGUARD_GRPC_ENDPOINT  default http://localhost:50051 (h2c)
///   NODEGUARD_API_TOKEN      default the dev "Liquidator" token
///   BITCOIND_RPC_URL/USER/PASS/WALLET  default http://localhost:18443 / polaruser / polarpass / default
///   NBXPLORER_URI            default http://localhost:32838, used to poll NBXplorer's own sync status
///   E2E_HOT_WALLET_ID        NodeGuard hot wallet to withdraw from (default 3, shared with the
///                            other E2E tests in this collection). This test sweeps its own change
///                            outputs to an external address once it's done, so it doesn't leave
///                            behind UTXOs that could affect other tests' assertions.
/// </summary>
[Trait("Category", "E2E")]
[Collection("E2E")]
public class ConcurrentWithdrawalE2ETests
{
    private const string DefaultDevToken = "8rvSsUGeyXXdDQrHctcTey/xtHdZQEn945KHwccKp9Q=";
    private const int ConcurrentWithdrawals = 10;
    private const long AmountPerWithdrawalSats = 1_000_000;

    private readonly ITestOutputHelper _output;

    public ConcurrentWithdrawalE2ETests(ITestOutputHelper output)
    {
        _output = output;
        AppContext.SetSwitch("System.Net.Http.SocketsHttpHandler.Http2UnencryptedSupport", true);
    }

    [E2EFact]
    public async Task ManyConcurrentWithdrawals_FromSameWallet_NeverSelectTheSameUtxo()
    {
        var client = CreateClient(out var headers);
        var rpc = CreateBitcoindRpc();
        var walletId = int.Parse(Env("E2E_HOT_WALLET_ID", "3"));

        await RetryAsync(async () =>
        {
            var resp = await client.GetNodesAsync(new GetNodesRequest(), headers);
            if (resp.Nodes.Count == 0) throw new InvalidOperationException("no nodes seeded yet");
            return true;
        }, attempts: 90, delay: TimeSpan.FromSeconds(4), what: "GetNodes (NodeGuard readiness)");

        // Give the wallet one distinct confirmed UTXO per withdrawal: coin selection picks the
        // oldest UTXO(s) first, so with only a single large UTXO every concurrent request would
        // just contend for that one input (and correctly fail for all but one), rather than each
        // picking its own input as they would with a realistic, multi-UTXO wallet.
        for (var i = 0; i < ConcurrentWithdrawals; i++)
        {
            var fundingAddressResponse = await client.GetNewWalletAddressAsync(
                new GetNewWalletAddressRequest { WalletId = walletId, Skip = 0, Reserve = true }, headers);
            await rpc.SendToAddressAsync(
                BitcoinAddress.Create(fundingAddressResponse.Address, Network.RegTest),
                Money.Satoshis(AmountPerWithdrawalSats * 3));
        }
        await MineAsync(rpc, 6);

        await RetryAsync(async () =>
        {
            var available = await client.GetAvailableUtxosAsync(
                new GetAvailableUtxosRequest { WalletId = walletId, Amount = AmountPerWithdrawalSats * ConcurrentWithdrawals },
                headers);
            if (available.Confirmed.Count < ConcurrentWithdrawals)
                throw new InvalidOperationException(
                    $"expected {ConcurrentWithdrawals} distinct confirmed UTXOs, got {available.Confirmed.Count}");
            return true;
        }, attempts: 60, delay: TimeSpan.FromSeconds(4), what: "GetAvailableUtxos (hot wallet funded with distinct UTXOs)");

        await WaitForNbxplorerFullySynchedAsync();

        // Fire all withdrawal requests concurrently so their coin selections overlap in time.
        var requestTasks = new List<Task<RequestWithdrawalResponse>>();
        var destinations = new List<BitcoinAddress>();
        for (var i = 0; i < ConcurrentWithdrawals; i++)
        {
            var destination = await rpc.GetNewAddressAsync();
            destinations.Add(destination);
            requestTasks.Add(client.RequestWithdrawalAsync(new RequestWithdrawalRequest
            {
                WalletId = walletId,
                Description = $"E2E concurrent withdrawal {i}",
                Destinations = { new Destination { Address = destination.ToString(), AmountSats = AmountPerWithdrawalSats } },
                MempoolFeeRate = FEES_TYPE.CustomFee,
                CustomFeeRate = 2,
            }, headers).ResponseAsync);
        }

        var withdrawals = await Task.WhenAll(requestTasks);
        foreach (var withdrawal in withdrawals)
        {
            _output.WriteLine($"withdrawal {withdrawal.RequestId} -> txid {withdrawal.Txid}");
            withdrawal.IsHotWallet.Should().BeTrue();
        }

        withdrawals.Select(w => w.Txid).Distinct().Should().HaveCount(ConcurrentWithdrawals,
            "every concurrent withdrawal must produce its own transaction, not replace another's");

        // NodeGuard signs and broadcasts hot-wallet withdrawals synchronously, so the txid is
        // already a valid, decodable mempool transaction - no need to mine/confirm anything to
        // inspect its inputs.
        var withdrawalTxs = await Task.WhenAll(withdrawals.Select(async w =>
        {
            var tx = await RetryAsync(async () =>
            {
                var t = await rpc.GetRawTransactionAsync(uint256.Parse(w.Txid), throwIfNotFound: false);
                return t ?? throw new InvalidOperationException($"withdrawal {w.RequestId} tx not broadcast yet");
            }, attempts: 30, delay: TimeSpan.FromSeconds(4), what: $"GetRawTransaction (withdrawal {w.RequestId})");
            return tx;
        }));

        // Across ALL concurrent withdrawals, no prevout may ever be spent twice. A shared input
        // between any two of them is exactly what would let bitcoind treat one as an RBF
        // replacement of the other.
        var allInputs = withdrawalTxs.SelectMany(tx => tx.Inputs.Select(input => input.PrevOut)).ToList();
        allInputs.Should().OnlyHaveUniqueItems(
            "concurrent withdrawals from the same wallet must select disjoint UTXOs");

        await SweepChangeOutputsAsync(client, headers, rpc, walletId, withdrawalTxs, destinations);
    }

    /// <summary>
    /// Manual/explicit UTXO selection (Changeless withdrawals) is a completely different code
    /// path from the automatic coin selection covered above - it never went through
    /// GetAvailableUTXOsAsync's filtering, so CoinSelectionService.LockUTXOs itself has to check
    /// an explicitly-named outpoint isn't already locked before committing to it. This test funds a
    /// single UTXO and fires two concurrent Changeless withdrawals that both explicitly name that
    /// same outpoint: exactly one must succeed, and the other must fail with FailedPrecondition
    /// rather than both silently locking the same UTXO to two different requests.
    /// </summary>
    [E2EFact]
    public async Task TwoConcurrentWithdrawals_ExplicitlySelectingTheSameUtxo_OnlyOneSucceeds()
    {
        var client = CreateClient(out var headers);
        var rpc = CreateBitcoindRpc();
        var walletId = int.Parse(Env("E2E_HOT_WALLET_ID", "3"));
        const long amountSats = 1_000_000;

        await RetryAsync(async () =>
        {
            var resp = await client.GetNodesAsync(new GetNodesRequest(), headers);
            if (resp.Nodes.Count == 0) throw new InvalidOperationException("no nodes seeded yet");
            return true;
        }, attempts: 90, delay: TimeSpan.FromSeconds(4), what: "GetNodes (NodeGuard readiness)");

        var fundingAddressResponse = await client.GetNewWalletAddressAsync(
            new GetNewWalletAddressRequest { WalletId = walletId, Skip = 0, Reserve = true }, headers);
        var fundingTxId = await rpc.SendToAddressAsync(
            BitcoinAddress.Create(fundingAddressResponse.Address, Network.RegTest), Money.Satoshis(amountSats));
        await MineAsync(rpc, 6);

        string outpoint = null!;
        await RetryAsync(async () =>
        {
            var available = await client.GetAvailableUtxosAsync(
                new GetAvailableUtxosRequest { WalletId = walletId, Amount = amountSats }, headers);
            var match = available.Confirmed.FirstOrDefault(u => u.Outpoint.StartsWith(fundingTxId.ToString()));
            if (match == null) throw new InvalidOperationException("funded UTXO not confirmed/indexed yet");
            outpoint = match.Outpoint;
            return true;
        }, attempts: 30, delay: TimeSpan.FromSeconds(4), what: "GetAvailableUtxos (dedicated UTXO funded)");

        await WaitForNbxplorerFullySynchedAsync();

        var destinationA = await rpc.GetNewAddressAsync();
        var destinationB = await rpc.GetNewAddressAsync();

        // Both requests explicitly name the exact same outpoint.
        var requestA = TryRequestWithdrawalAsync(client, headers, walletId, outpoint, destinationA, amountSats, "A");
        var requestB = TryRequestWithdrawalAsync(client, headers, walletId, outpoint, destinationB, amountSats, "B");

        var (resultA, resultB) = (await requestA, await requestB);
        _output.WriteLine($"request A: {(resultA.Response != null ? $"txid {resultA.Response.Txid}" : resultA.Error!.Status)}");
        _output.WriteLine($"request B: {(resultB.Response != null ? $"txid {resultB.Response.Txid}" : resultB.Error!.Status)}");

        var successes = new[] { resultA, resultB }.Where(r => r.Response != null).ToList();
        var failures = new[] { resultA, resultB }.Where(r => r.Response == null).ToList();

        successes.Should().ContainSingle(
            "exactly one of the two requests explicitly naming the same outpoint must win the lock");
        failures.Should().ContainSingle();
        failures[0].Error!.StatusCode.Should().Be(StatusCode.FailedPrecondition,
            "the losing request must be rejected for the outpoint already being locked, not fail some other way");

        // The winning withdrawal spends the UTXO changelessly to an external address, so nothing
        // is left behind in the shared wallet for other tests to trip over.
        await RetryAsync(async () =>
        {
            var tx = await rpc.GetRawTransactionAsync(uint256.Parse(successes[0].Response!.Txid), throwIfNotFound: false);
            return tx ?? throw new InvalidOperationException("winning withdrawal tx not broadcast yet");
        }, attempts: 30, delay: TimeSpan.FromSeconds(4), what: "GetRawTransaction (winning withdrawal broadcast)");
        await MineAsync(rpc, 6);
    }

    private async Task<(RequestWithdrawalResponse? Response, RpcException? Error)> TryRequestWithdrawalAsync(
        NodeGuardService.NodeGuardServiceClient client, Metadata headers, int walletId, string outpoint,
        BitcoinAddress destination, long amountSats, string label)
    {
        try
        {
            var response = await client.RequestWithdrawalAsync(new RequestWithdrawalRequest
            {
                WalletId = walletId,
                Description = $"E2E explicit-outpoint conflict test {label}",
                Changeless = true,
                UtxosOutpoints = { outpoint },
                Destinations = { new Destination { Address = destination.ToString(), AmountSats = amountSats } },
                MempoolFeeRate = FEES_TYPE.CustomFee,
                CustomFeeRate = 2,
            }, headers);
            return (response, null);
        }
        catch (RpcException e)
        {
            return (null, e);
        }
    }

    // The wallet used above is shared with other E2E tests in this collection, so the change
    // outputs these withdrawals produced need to be swept out to an external address rather than
    // left behind - otherwise later tests could see UTXOs of a shape they don't expect.
    private async Task SweepChangeOutputsAsync(
        NodeGuardService.NodeGuardServiceClient client, Metadata headers, RPCClient rpc, int walletId,
        Transaction[] withdrawalTxs, List<BitcoinAddress> destinations)
    {
        var destinationScripts = destinations.Select(d => d.ScriptPubKey).ToHashSet();
        var changeOutpoints = withdrawalTxs
            .SelectMany(tx => tx.Outputs.AsIndexedOutputs()
                .Where(o => !destinationScripts.Contains(o.TxOut.ScriptPubKey))
                .Select(o => new OutPoint(tx.GetHash(), o.N)))
            .ToList();
        changeOutpoints.Should().HaveCount(ConcurrentWithdrawals, "each withdrawal must have produced exactly one change output");

        await MineAsync(rpc, 6);
        await RetryAsync(async () =>
        {
            var available = await client.GetAvailableUtxosAsync(
                new GetAvailableUtxosRequest { WalletId = walletId, Amount = changeOutpoints.Count }, headers);
            var confirmedOutpoints = available.Confirmed.Select(u => u.Outpoint).ToHashSet();
            if (!changeOutpoints.All(o => confirmedOutpoints.Contains(o.ToString())))
                throw new InvalidOperationException("change outputs not confirmed/indexed yet");
            return true;
        }, attempts: 30, delay: TimeSpan.FromSeconds(4), what: "GetAvailableUtxos (change outputs confirmed for sweep)");

        // Same NBXplorer post-mining sync race as after the initial funding round above.
        await WaitForNbxplorerFullySynchedAsync();

        var sweepAddress = await rpc.GetNewAddressAsync();
        var sweepResponse = await client.RequestWithdrawalAsync(new RequestWithdrawalRequest
        {
            WalletId = walletId,
            Description = "E2E concurrent withdrawal cleanup sweep",
            Changeless = true,
            UtxosOutpoints = { changeOutpoints.Select(o => o.ToString()) },
            Destinations = { new Destination { Address = sweepAddress.ToString(), AmountSats = 1 } },
            MempoolFeeRate = FEES_TYPE.CustomFee,
            CustomFeeRate = 2,
        }, headers);
        _output.WriteLine($"cleanup sweep -> txid {sweepResponse.Txid}");

        await RetryAsync(async () =>
        {
            var tx = await rpc.GetRawTransactionAsync(uint256.Parse(sweepResponse.Txid), throwIfNotFound: false);
            return tx ?? throw new InvalidOperationException("sweep tx not broadcast yet");
        }, attempts: 30, delay: TimeSpan.FromSeconds(4), what: "GetRawTransaction (cleanup sweep broadcast)");
        await MineAsync(rpc, 6);
    }

    // ---- helpers -----------------------------------------------------------------------------

    private static readonly HttpClient NbxplorerHttp = new();

    // NBXplorer can still be finishing a background rescan of blocks just mined even though it
    // already has enough indexed to satisfy a GetAvailableUtxos check, so a withdrawal request
    // fired immediately after mining can race NBXplorer's own "fully synced" flag and fail with
    // NBXplorerNotFullySyncedException. Poll NBXplorer's own status endpoint directly - the same
    // flag GenerateTemplatePSBT itself checks server-side - rather than inferring it. Call this
    // after every MineAsync that precedes a withdrawal request.
    private async Task WaitForNbxplorerFullySynchedAsync()
    {
        await RetryAsync(async () =>
        {
            var isFullySynched = await IsNbxplorerFullySynchedAsync();
            if (!isFullySynched)
                throw new InvalidOperationException("NBXplorer is not fully synched yet");
            return true;
        }, attempts: 30, delay: TimeSpan.FromSeconds(2), what: "NBXplorer status (fully synched)");
    }

    private async Task<bool> IsNbxplorerFullySynchedAsync()
    {
        var baseUrl = Env("NBXPLORER_URI", "http://localhost:32838");
        var status = await NbxplorerHttp.GetFromJsonAsync<NbxplorerStatus>($"{baseUrl}/v1/cryptos/btc/status");
        return status?.IsFullySynched ?? false;
    }

    private sealed class NbxplorerStatus
    {
        public bool IsFullySynched { get; set; }
    }

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
