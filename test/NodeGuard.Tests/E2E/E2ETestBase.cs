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
/// Shared plumbing for the container e2e tests (gated by <see cref="E2EFactAttribute"/>): the gRPC
/// client + auth header, the bitcoind RPC client, mining, retry/poll loops, NodeGuard readiness, and
/// the common "open source→dest through NodeGuard and wait for it to confirm" flow. Concrete test
/// classes add their own <c>[E2EFact]</c> methods and assertions on top.
///
/// This is pure code-sharing only — it is NOT a collection fixture and assigns no xUnit collection or
/// trait, so each concrete test keeps its own <c>[Trait("Category", …)]</c> and its own (default)
/// parallelisation behaviour. In particular the fee-engine e2e stays under a separate category so it
/// never runs in the same pass as the rebalance e2e.
///
/// Connection via env (all with dev-friendly defaults):
///   NODEGUARD_GRPC_ENDPOINT            default http://localhost:50051 (h2c)
///   NODEGUARD_API_TOKEN                default the dev "Liquidator" token
///   BITCOIND_RPC_URL/USER/PASS/WALLET  default http://localhost:18443 / polaruser / polarpass / default
///   E2E_HOT_WALLET_ID                  NodeGuard hot wallet to fund the channel (default 3)
/// </summary>
public abstract class E2ETestBase
{
    private const string DefaultDevToken = "8rvSsUGeyXXdDQrHctcTey/xtHdZQEn945KHwccKp9Q=";

    protected readonly ITestOutputHelper _output;

    protected E2ETestBase(ITestOutputHelper output)
    {
        _output = output;
        AppContext.SetSwitch("System.Net.Http.SocketsHttpHandler.Http2UnencryptedSupport", true);
    }

    /// <summary>
    /// Waits for NodeGuard to serve gRPC and to have seeded alice/bob/carol. Generous window: a fresh
    /// NodeGuard runs migrations + funds its wallet (mining + NBXplorer sync) before serving gRPC.
    /// </summary>
    protected async Task<IReadOnlyList<Node>> WaitForNodesAsync(
        NodeGuardService.NodeGuardServiceClient client, Metadata headers)
    {
        return await RetryAsync(async () =>
        {
            var resp = await client.GetNodesAsync(new GetNodesRequest(), headers);
            var seeded = resp.Nodes.Where(n => n.Name is "alice" or "bob" or "carol").ToList();
            if (seeded.Count < 3) throw new InvalidOperationException($"only {seeded.Count}/3 nodes seeded");
            return (IReadOnlyList<Node>)seeded;
        }, attempts: 90, delay: TimeSpan.FromSeconds(4), what: "GetNodes (NodeGuard readiness)");
    }

    /// <summary>
    /// Opens <paramref name="sourcePubKey"/>→<paramref name="destPubKey"/> THROUGH NodeGuard
    /// (wallet → PSBT → internal signing → broadcast), mines until the funding tx confirms and
    /// NodeGuard records the channel id, then mines a few more so LND marks the channel active and
    /// gossip propagates. Returns NodeGuard's channel id.
    /// </summary>
    protected async Task<long> OpenChannelAndConfirmAsync(
        NodeGuardService.NodeGuardServiceClient client, Metadata headers, RPCClient rpc,
        string sourcePubKey, string destPubKey, long satsAmount = 16_000_000)
    {
        // Retry briefly in case the dev hot wallet is still being funded by DbInitializer when we connect.
        var walletId = int.Parse(Env("E2E_HOT_WALLET_ID", "3"));
        var openReq = new OpenChannelRequest
        {
            SourcePubKey = sourcePubKey,
            DestinationPubKey = destPubKey,
            WalletId = walletId,
            SatsAmount = satsAmount,
            Private = false,
            Changeless = false,
            MempoolFeeRate = FEES_TYPE.CustomFee,
            CustomFeeRate = 2,
        };
        var opId = await RetryAsync(
            async () => (await client.OpenChannelAsync(openReq, headers)).ChannelOperationRequestId,
            attempts: 10, delay: TimeSpan.FromSeconds(6), what: "OpenChannel");
        _output.WriteLine($"OpenChannel → operation {opId}");

        // Mine + poll until the funding tx confirms and NodeGuard records the channel id.
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

        // Mine more so LND marks the channel active and gossip propagates.
        await MineAsync(rpc, 6);
        await Task.Delay(TimeSpan.FromSeconds(4));
        return channelId;
    }

    protected async Task MineAsync(RPCClient rpc, int blocks)
    {
        var addr = await rpc.GetNewAddressAsync();
        await rpc.GenerateToAddressAsync(blocks, addr);
    }

    /// <summary>Retries <paramref name="action"/> until it succeeds or the attempts are exhausted.</summary>
    protected async Task<T> RetryAsync<T>(Func<Task<T>> action, int attempts, TimeSpan delay, string what)
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

    /// <summary>Polls <paramref name="read"/> until <paramref name="done"/> holds or attempts are exhausted.</summary>
    protected async Task<T> PollAsync<T>(Func<Task<T>> read, Func<T, bool> done, int attempts, TimeSpan delay, string what)
    {
        T last = default!;
        for (var i = 0; i < attempts; i++)
        {
            last = await read();
            if (done(last)) return last;
            _output.WriteLine($"{what} attempt {i + 1}/{attempts}: not ready");
            await Task.Delay(delay);
        }
        throw new InvalidOperationException($"{what} not satisfied after {attempts} attempts");
    }

    protected static NodeGuardService.NodeGuardServiceClient CreateClient(out Metadata headers)
    {
        var endpoint = Env("NODEGUARD_GRPC_ENDPOINT", "http://localhost:50051");
        headers = new Metadata { { "auth-token", Env("NODEGUARD_API_TOKEN", DefaultDevToken) } };
        return new NodeGuardService.NodeGuardServiceClient(GrpcChannel.ForAddress(endpoint));
    }

    protected static RPCClient CreateBitcoindRpc()
    {
        var url = Env("BITCOIND_RPC_URL", "http://localhost:18443");
        var cred = new NetworkCredential(Env("BITCOIND_RPC_USER", "polaruser"), Env("BITCOIND_RPC_PASS", "polarpass"));
        var rpc = new RPCClient(cred, new Uri(url), Network.RegTest);
        return rpc.SetWalletContext(Env("BITCOIND_RPC_WALLET", "default"));
    }

    protected static string Env(string name, string fallback)
        => Environment.GetEnvironmentVariable(name) is { Length: > 0 } v ? v : fallback;
}
