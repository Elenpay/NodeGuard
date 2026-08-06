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
/// True end-to-end test driven entirely from .NET (no grpcurl/curl): a generated gRPC client
/// drives a LIVE NodeGuard instance through the whole option-B flow —
///   GetNodes → OpenChannel(Alice→Bob) → mine (NBitcoin RPC) + poll GetChannelOperationRequest
///   until the channel confirms → RequestRebalance(Alice→Bob→Carol→Alice) → assert success.
/// This exercises gRPC auth, channel opening (wallet → PSBT → internal signing → broadcast),
/// channel sync, and the amountless-invoice rebalance against real LND + Postgres.
///
/// Gated by <see cref="E2EFactAttribute"/> (RUN_E2E_TESTS=1). Connection via env:
///   NODEGUARD_GRPC_ENDPOINT  default http://localhost:50051 (h2c)
///   NODEGUARD_API_TOKEN      default the dev "Liquidator" token
///   BITCOIND_RPC_URL/USER/PASS/WALLET  default http://localhost:18443 / polaruser / polarpass / default
///   E2E_HOT_WALLET_ID        NodeGuard hot wallet to fund the channel (default 3)
/// </summary>
[Trait("Category", "E2E")]
[Collection("E2E")]
public class RebalanceE2ETests
{
    private const string DefaultDevToken = "8rvSsUGeyXXdDQrHctcTey/xtHdZQEn945KHwccKp9Q=";

    private readonly ITestOutputHelper _output;

    public RebalanceE2ETests(ITestOutputHelper output)
    {
        _output = output;
        AppContext.SetSwitch("System.Net.Http.SocketsHttpHandler.Http2UnencryptedSupport", true);
    }

    [E2EFact]
    public async Task OpenChannelViaGrpc_ThenCircularRebalance_Succeeds()
    {
        var client = CreateClient(out var headers);
        var rpc = CreateBitcoindRpc();

        // 0. Wait for NodeGuard to be up and to have seeded the three nodes.
        var nodes = await WaitForNodesAsync(client, headers);
        var alice = nodes.Single(n => n.Name == "alice");
        var bob = nodes.Single(n => n.Name == "bob");
        var carol = nodes.Single(n => n.Name == "carol");
        _output.WriteLine($"alice={alice.PubKey} bob={bob.PubKey} carol={carol.PubKey}");

        // 1. Open Alice→Bob THROUGH NodeGuard (option B). Retry briefly in case the dev hot
        //    wallet is still being funded by DbInitializer when we connect.
        var walletId = int.Parse(Env("E2E_HOT_WALLET_ID", "3"));

        // Other E2E tests sharing this collection spend down the wallet's one-time seed, so
        // top it up before relying on it for the channel-funding UTXO.
        await E2EFundingHelper.FundHotWalletAsync(client, headers, rpc, walletId, Money.Coins(1m), _output.WriteLine);

        var openReq = new OpenChannelRequest
        {
            SourcePubKey = alice.PubKey,
            DestinationPubKey = bob.PubKey,
            WalletId = walletId,
            SatsAmount = 16_000_000,
            Private = false,
            Changeless = false,
            MempoolFeeRate = FEES_TYPE.CustomFee,
            CustomFeeRate = 2,
        };
        var opId = await RetryAsync(
            async () => (await client.OpenChannelAsync(openReq, headers)).ChannelOperationRequestId,
            attempts: 10, delay: TimeSpan.FromSeconds(6), what: "OpenChannel");
        _output.WriteLine($"OpenChannel → operation {opId}");

        // 2. Mine + poll until the funding tx confirms and NodeGuard records the channel id.
        long channelId = 0;
        for (var i = 0; i < 40 && channelId == 0; i++)
        {
            await MineAsync(rpc, 2);
            var st = await client.GetChannelOperationRequestAsync(
                new GetChannelOperationRequestRequest { ChannelOperationRequestId = opId }, headers);
            _output.WriteLine($"poll {i}: status={st.Status} channelId={(st.HasChannelId ? st.ChannelId : 0)}");
            if (st.HasChannelId && st.ChannelId > 0) channelId = st.ChannelId;
            else await Task.Delay(TimeSpan.FromSeconds(3));
        }
        channelId.Should().BeGreaterThan(0, "NodeGuard should record the opened channel's id");

        // 3. Mine a few more blocks so LND marks the channel active and gossip propagates.
        await MineAsync(rpc, 6);
        await Task.Delay(TimeSpan.FromSeconds(4));

        // 4. Circular rebalance Alice→Bob→Carol→Alice over the just-opened channel.
        var resp = await client.RequestRebalanceAsync(new RequestRebalanceRequest
        {
            NodePubkey = alice.PubKey,
            SourceChannelId = (int)channelId,
            TargetPubkey = carol.PubKey,
            AmountSats = 500_000,
            MaxFeePct = 0.2,
            MaxAttempts = 1,
        }, headers);

        _output.WriteLine($"rebalance {resp.RebalanceId}: status={resp.Status} feeSats={resp.FeePaidSats} ppm={resp.EffectivePpm}");
        resp.Status.Should().Be(REBALANCE_STATUS.RebalanceSucceeded);
        resp.FeePaidSats.Should().BeGreaterThan(0);
        resp.FeePaidSats.Should().BeLessThanOrEqualTo(1_000); // within 0.2% of 500k
    }

    // ---- helpers -----------------------------------------------------------------------------

    private async Task<IReadOnlyList<Node>> WaitForNodesAsync(
        NodeGuardService.NodeGuardServiceClient client, Metadata headers)
    {
        return await RetryAsync(async () =>
        {
            var resp = await client.GetNodesAsync(new GetNodesRequest(), headers);
            var seeded = resp.Nodes.Where(n => n.Name is "alice" or "bob" or "carol").ToList();
            if (seeded.Count < 3) throw new InvalidOperationException($"only {seeded.Count}/3 nodes seeded");
            return (IReadOnlyList<Node>)seeded;
            // Generous window: a fresh NodeGuard runs migrations + funds its wallet (mining + NBXplorer
            // sync) before serving gRPC, which can take several minutes.
        }, attempts: 90, delay: TimeSpan.FromSeconds(4), what: "GetNodes (NodeGuard readiness)");
    }

    private async Task MineAsync(RPCClient rpc, int blocks) => await E2EFundingHelper.MineAsync(rpc, blocks);

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
