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
using System.Net.Http;
using System.Net.Sockets;
using FluentAssertions;
using Grpc.Core;
using Grpc.Net.Client;
using NBitcoin;
using NBitcoin.RPC;
using Nodeguard;
using Npgsql;
using Xunit.Abstractions;

namespace NodeGuard.Tests.E2E;

/// <summary>
/// End-to-end proof of the HTLC-subscription self-healing fix: a managed forwarding node's lnd is
/// restarted mid-test, and NodeGuard must resubscribe and keep persisting the HTLCs it forwards.
///
/// Flow:
///   GetNodes → OpenChannel(Alice→Bob) → circular rebalance (Alice→Bob→Carol→Alice) so Bob forwards
///   HTLCs → assert Bob's ForwardingHtlcEvents appear in Postgres → RESTART Bob's lnd container →
///   rebalance again → assert Bob's ForwardingHtlcEvents count INCREASED.
///
/// Bob is the node under test because in a circular rebalance only the intermediate hops (Bob,
/// Carol) emit lnd <c>Forward</c> events — the only kind <c>NodeHtlcSubscribeJob</c> persists.
/// Without the reconnect fix, Bob's subscription would exit on the clean stream-end its lnd restart
/// produces and stay dead until NodeGuard itself restarts, so the post-restart forward would never
/// be persisted and the final assertion would fail.
///
/// Gated by <see cref="E2EFactAttribute"/> (RUN_E2E_TESTS=1). Extra env beyond the other e2e tests:
///   POSTGRES_CONNECTIONSTRING        NodeGuard DB (there is no gRPC to read forwarding events)
///   E2E_FORWARDING_NODE_CONTAINER    LND container to restart (default polar-n1-bob)
///   DOCKER_SOCKET                    Docker Engine API socket (default /var/run/docker.sock)
/// </summary>
[Trait("Category", "E2E")]
[Collection("E2E")]
public class HtlcSubscriptionReconnectE2ETests
{
    private const string DefaultDevToken = "8rvSsUGeyXXdDQrHctcTey/xtHdZQEn945KHwccKp9Q=";

    private readonly ITestOutputHelper _output;

    public HtlcSubscriptionReconnectE2ETests(ITestOutputHelper output)
    {
        _output = output;
        AppContext.SetSwitch("System.Net.Http.SocketsHttpHandler.Http2UnencryptedSupport", true);
    }

    [E2EFact]
    public async Task HtlcSubscription_SurvivesForwardingNodeRestart_AndKeepsPersistingEvents()
    {
        var client = CreateClient(out var headers);
        var rpc = CreateBitcoindRpc();

        // 0. Wait for NodeGuard and its seeded, managed nodes (alice/bob/carol).
        var nodes = await WaitForNodesAsync(client, headers);
        var alice = nodes.Single(n => n.Name == "alice");
        var bob = nodes.Single(n => n.Name == "bob");
        var carol = nodes.Single(n => n.Name == "carol");
        _output.WriteLine($"alice={alice.PubKey} bob={bob.PubKey} carol={carol.PubKey}");

        // 1. Open Alice→Bob so the circular rebalance forwards HTLCs through Bob.
        var channelId = await OpenAliceBobChannelAsync(client, headers, rpc, alice, bob);

        // 2. First rebalance → Bob forwards → Bob's subscription persists the forward event(s).
        await RetryAsync(async () => { await RebalanceAsync(client, headers, alice, carol, channelId); return true; },
            attempts: 10, delay: TimeSpan.FromSeconds(6), what: "initial rebalance");

        // 3. Baseline: Bob's forwarding events must be visible in Postgres before we restart it —
        //    this proves the subscription pipeline works pre-restart.
        var baseline = await PollUntilAsync(
            () => CountForwardingEventsAsync(bob.PubKey), c => c > 0,
            attempts: 30, delay: TimeSpan.FromSeconds(2), what: "baseline Bob forwarding events > 0");
        _output.WriteLine($"baseline Bob forwarding events = {baseline}");

        // 4. Restart Bob's lnd → its HTLC stream drops. The fix must make NodeGuard resubscribe.
        var container = Env("E2E_FORWARDING_NODE_CONTAINER", "polar-n1-bob");
        _output.WriteLine($"restarting {container} to drop Bob's HTLC subscription");
        await RestartContainerAsync(container);

        // 5. Rebalance again once Bob's lnd is back and its channels reactivate. Retry while that
        //    happens (and while NodeGuard resubscribes — backoff up to 30s). Route failures while
        //    Bob is down fail fast (no route), so the loop just spins cheaply until it recovers.
        await RetryAsync(async () =>
        {
            await MineAsync(rpc, 2); // nudge channel reactivation / gossip
            await RebalanceAsync(client, headers, alice, carol, channelId);
            return true;
        }, attempts: 40, delay: TimeSpan.FromSeconds(6), what: "post-restart rebalance");

        // 6. The real assertion: Bob's forwarding events increased after the restart. That can only
        //    happen if Bob's HTLC subscription reconnected and captured the post-restart forward.
        var after = await PollUntilAsync(
            () => CountForwardingEventsAsync(bob.PubKey), c => c > baseline,
            attempts: 30, delay: TimeSpan.FromSeconds(2), what: $"Bob forwarding events > baseline ({baseline})");
        _output.WriteLine($"post-reconnect Bob forwarding events = {after}");

        after.Should().BeGreaterThan(baseline,
            "Bob's HTLC subscription must resubscribe after its lnd restarts and keep persisting forwarded HTLCs");
    }

    // ---- helpers -----------------------------------------------------------------------------

    private async Task<long> OpenAliceBobChannelAsync(
        NodeGuardService.NodeGuardServiceClient client, Metadata headers, RPCClient rpc, Node alice, Node bob)
    {
        var walletId = int.Parse(Env("E2E_HOT_WALLET_ID", "3"));
        var openReq = new OpenChannelRequest
        {
            SourcePubKey = alice.PubKey,
            DestinationPubKey = bob.PubKey,
            WalletId = walletId,
            SatsAmount = 5_000_000,
            Private = false,
            Changeless = false,
            MempoolFeeRate = FEES_TYPE.CustomFee,
            CustomFeeRate = 2,
        };
        var opId = await RetryAsync(
            async () => (await client.OpenChannelAsync(openReq, headers)).ChannelOperationRequestId,
            attempts: 10, delay: TimeSpan.FromSeconds(6), what: "OpenChannel");
        _output.WriteLine($"OpenChannel → operation {opId}");

        long channelId = 0;
        for (var i = 0; i < 40 && channelId == 0; i++)
        {
            await MineAsync(rpc, 2);
            var st = await client.GetChannelOperationRequestAsync(
                new GetChannelOperationRequestRequest { ChannelOperationRequestId = opId }, headers);
            if (st.HasChannelId && st.ChannelId > 0) channelId = st.ChannelId;
            else await Task.Delay(TimeSpan.FromSeconds(3));
        }
        channelId.Should().BeGreaterThan(0, "NodeGuard should record the opened Alice→Bob channel id");

        await MineAsync(rpc, 6); // let lnd mark the channel active and gossip propagate
        await Task.Delay(TimeSpan.FromSeconds(4));
        return channelId;
    }

    private async Task RebalanceAsync(
        NodeGuardService.NodeGuardServiceClient client, Metadata headers, Node alice, Node carol, long channelId)
    {
        var resp = await client.RequestRebalanceAsync(new RequestRebalanceRequest
        {
            NodePubkey = alice.PubKey,
            SourceChannelId = (int)channelId,
            TargetPubkey = carol.PubKey,
            AmountSats = 200_000,
            MaxFeePct = 0.5,
            MaxAttempts = 2,
        }, headers);
        _output.WriteLine($"rebalance {resp.RebalanceId}: status={resp.Status} feeSats={resp.FeePaidSats}");
        resp.Status.Should().Be(REBALANCE_STATUS.RebalanceSucceeded);
    }

    /// <summary>Counts persisted forwarding HTLC events for a managed node, read straight from
    /// Postgres because NodeGuard exposes no gRPC to list them.</summary>
    private static async Task<long> CountForwardingEventsAsync(string managedNodePubKey)
    {
        var connString = Env("POSTGRES_CONNECTIONSTRING",
            "Host=localhost;Port=5432;Database=nodeguard;User ID=postgres;");
        await using var conn = new NpgsqlConnection(connString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(
            "SELECT COUNT(*) FROM \"ForwardingHtlcEvents\" WHERE \"ManagedNodePubKey\" = @pk", conn);
        cmd.Parameters.AddWithValue("pk", managedNodePubKey);
        return Convert.ToInt64(await cmd.ExecuteScalarAsync());
    }

    /// <summary>Restarts a container via the Docker Engine API over its unix socket (the runner
    /// mounts /var/run/docker.sock) — no docker CLI needed in the image.</summary>
    private async Task RestartContainerAsync(string containerName)
    {
        var socketPath = Env("DOCKER_SOCKET", "/var/run/docker.sock");
        using var handler = new SocketsHttpHandler
        {
            ConnectCallback = async (_, ct) =>
            {
                var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
                await socket.ConnectAsync(new UnixDomainSocketEndPoint(socketPath), ct);
                return new NetworkStream(socket, ownsSocket: true);
            }
        };
        using var http = new HttpClient(handler) { BaseAddress = new Uri("http://localhost") };
        // t = seconds to wait for a graceful stop before SIGKILL; either way lnd's stream drops.
        using var resp = await http.PostAsync($"/containers/{containerName}/restart?t=15", content: null);
        if (!resp.IsSuccessStatusCode)
        {
            var body = await resp.Content.ReadAsStringAsync();
            throw new InvalidOperationException(
                $"docker restart {containerName} failed: {(int)resp.StatusCode} {body}");
        }
    }

    private async Task<long> PollUntilAsync(
        Func<Task<long>> read, Func<long, bool> predicate, int attempts, TimeSpan delay, string what)
    {
        long last = 0;
        for (var i = 0; i < attempts; i++)
        {
            last = await read();
            if (predicate(last)) return last;
            await Task.Delay(delay);
        }
        throw new InvalidOperationException($"{what} not met after {attempts} attempts (last={last})");
    }

    private async Task<IReadOnlyList<Node>> WaitForNodesAsync(
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
