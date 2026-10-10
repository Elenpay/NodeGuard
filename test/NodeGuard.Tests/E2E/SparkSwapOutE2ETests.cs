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

using System.Net.Sockets;
using System.Text.Json;
using FluentAssertions;
using Google.Protobuf;
using Grpc.Core;
using Grpc.Net.Client;
using NBitcoin;
using Nodeguard;
using Npgsql;
using Xunit.Abstractions;

namespace NodeGuard.Tests.E2E;

/// <summary>
/// Runs only with the local Spark network: SPARK_E2E=1 (with the spark compose profile up and
/// SPARK_ENABLED=true on NodeGuard) on top of the usual E2E gate.
/// </summary>
public sealed class SparkE2EFactAttribute : FactAttribute
{
    public SparkE2EFactAttribute()
    {
        var e2e = new E2EFactAttribute();
        if (e2e.Skip is not null)
        {
            Skip = e2e.Skip;
            return;
        }

        if (Environment.GetEnvironmentVariable("SPARK_E2E") != "1")
        {
            Skip = "Spark E2E disabled: set SPARK_E2E=1 with the spark compose profile up and SPARK_ENABLED=true on NodeGuard.";
        }
    }
}

/// <summary>
/// A Spark swap-out through NodeGuard's gRPC API against the local Spark network (docker/spark):
/// <list type="number">
/// <item>a Spark wallet is created over gRPC and made alice's Spark wallet, with Max swaps in flight 2 (node
/// liquidity config, set in the database as the Nodes page would);</item>
/// <item>NodeGuard restarts, so the wallet's seed has to decrypt with the Data Protection key ring in Postgres;</item>
/// <item>two swaps of different amounts start at once. For each, alice's LND pays an invoice of the Spark wallet,
/// MonitorSwapsJob follows the payment until it settles, SparkSwapExitJob records the transfer that brought it in
/// and exits exactly its leaves on-chain to the swap's own reserved address, and the swap completes once that
/// payout confirms, recording it;</item>
/// <item>the Spark wallet is empty again, and can't be archived while alice uses it.</item>
/// </list>
/// A second test ages a swap's leaves to their timelock floor before its exit, with the local operators' test RPC,
/// and checks the exit renews them and moves exactly them, under their own ids.
///
///   E2E_SPARK_PAYOUT_WALLET_ID   NodeGuard on-chain wallet the swap pays out to (default 2)
///   POSTGRES_CONNECTIONSTRING    NodeGuard's database, to give alice the Spark wallet and read a swap's leaves
///   DOCKER_SOCKET                Docker Engine API socket (default /var/run/docker.sock), to restart NodeGuard
///                                and to stop and start NBXplorer
///   E2E_NODEGUARD_CONTAINER      NodeGuard's container (default: the nodeguard service of the runner's compose project)
///   SPARK_OPERATOR_URLS          the local operators, comma-separated (default https://spark-operator-{0,1,2}:8535)
///   SPARK_OPERATOR_POSTGRES      operator 0's database (default Host=spark-postgres;Database=sparkoperator_0;Username=postgres)
/// </summary>
[Trait("Category", "E2E")]
[Collection("E2E")]
public class SparkSwapOutE2ETests : E2ETestBase
{
    public SparkSwapOutE2ETests(ITestOutputHelper output) : base(output)
    {
    }

    [SparkE2EFact]
    public async Task SwapOuts_ThroughASparkWallet_EachPayOutToTheirOwnAddressAndComplete()
    {
        var client = CreateClient(out var headers);
        var rpc = CreateBitcoindRpc();
        var alice = (await WaitForNodesAsync(client, headers)).Single(n => n.Name == "alice");
        var payoutWalletId = int.Parse(Env("E2E_SPARK_PAYOUT_WALLET_ID", "2"));
        long[] amounts = [300_000, 200_000];

        // A Spark wallet for alice's swaps, two of which may run at once
        var sparkWallet = await client.CreateSparkWalletAsync(
            new CreateSparkWalletRequest { Name = $"e2e transit {Guid.NewGuid():N}"[..20], MaxBalanceSats = 2_000_000 }, headers);
        _output.WriteLine($"Spark wallet {sparkWallet.WalletId}: identity {sparkWallet.IdentityPublicKey}");
        await SetSparkConfigAsync(alice.Id, sparkWallet.WalletId, maxSwapsInFlight: 2);

        // Its seed must still decrypt after a restart, with the key ring NodeGuard keeps in Postgres
        await RestartNodeGuardAsync();
        await WaitForNodesAsync(client, headers);

        var requests = amounts.Select(amount => new RequestSwapOutRequest
        {
            NodeId = alice.Id,
            Provider = SWAP_PROVIDER.Spark,
            AmountSats = amount,
            WalletId = payoutWalletId,
            ReferenceId = $"spark-e2e-{Guid.NewGuid():N}"
        }).ToList();

        // Both at once: alice pays both invoices into the same Spark wallet in parallel
        var started = await Task.WhenAll(requests.Select(request => StartSwapAsync(client, headers, request)));
        foreach (var swap in started)
        {
            _output.WriteLine($"swap {swap.SwapId}: {swap.Status}, provider id {swap.ProviderId}");
            swap.Status.Should().Be(SWAP_OUT_STATUS.SwapOutPending, swap.Error ?? "alice's payment into Spark should have succeeded");
            swap.DestinationAddress.Should().BeEmpty("a Spark swap reserves its address when it exits");
        }

        // MonitorSwapsJob sees the payments settle and SparkSwapExitJob exits each swap's leaves (each every minute in
        // a dev environment); mining confirms the exits
        var done = await PollAsync(
            async () =>
            {
                await MineAsync(rpc, 1);
                return await Task.WhenAll(requests.Select(request =>
                    client.GetSwapOutAsync(new GetSwapOutRequest { ReferenceId = request.ReferenceId }, headers).ResponseAsync));
            },
            swaps => swaps.All(s => s.Status != SWAP_OUT_STATUS.SwapOutPending),
            attempts: 60, delay: TimeSpan.FromSeconds(10), what: "both swaps' completion");

        for (var i = 0; i < done.Length; i++)
        {
            var swap = done[i];
            _output.WriteLine($"swap {swap.SwapId}: {swap.Status}, payout {swap.PayoutSats} to {swap.DestinationAddress} in {swap.TxId}, " +
                              $"fees ln={swap.LightningFeeSats} service={swap.ServiceFeeSats} onchain={swap.OnchainFeeSats} {swap.Error}");

            swap.Status.Should().Be(SWAP_OUT_STATUS.SwapOutCompleted, swap.Error);
            swap.SwapId.Should().Be(started[i].SwapId);
            swap.DestinationAddress.Should().NotBeNullOrEmpty("the swap reserved its address when it exited");
            swap.HasPaymentHash.Should().BeTrue();

            // What landed on-chain is what the swap delivered. The exit from Spark to L1 is the on-chain fee, and any
            // shortfall of the payment the service fee (Spark's own 15 bps is in the routing fee)
            var destination = BitcoinAddress.Create(swap.DestinationAddress, Network.RegTest);
            var payoutTx = await rpc.GetRawTransactionAsync(uint256.Parse(swap.TxId));
            var payout = payoutTx.Outputs.Where(o => o.ScriptPubKey == destination.ScriptPubKey).Sum(o => o.Value.Satoshi);
            swap.HasPayoutSats.Should().BeTrue();
            swap.PayoutSats.Should().Be(payout);
            (swap.OnchainFeeSats + swap.ServiceFeeSats).Should().Be(amounts[i] - payout,
                "the on-chain and service fees are everything the swap did not deliver");
        }

        done.Select(s => s.DestinationAddress).Should().OnlyHaveUniqueItems("each swap pays out to its own address");
        done.Select(s => s.TxId).Should().OnlyHaveUniqueItems("each swap exits on its own");

        // Asking again with the same reference_id returns the same swap rather than paying again
        var again = await client.RequestSwapOutAsync(requests[0], headers);
        again.SwapId.Should().Be(started[0].SwapId);
        again.Status.Should().Be(SWAP_OUT_STATUS.SwapOutCompleted);

        // The swaps went through alice's Spark wallet, which they left empty
        var balance = await PollAsync(
            async () => await client.GetSparkWalletBalanceAsync(new GetSparkWalletBalanceRequest { WalletId = sparkWallet.WalletId }, headers),
            b => b.OwnedSats + b.IncomingSats == 0,
            attempts: 12, delay: TimeSpan.FromSeconds(5), what: $"Spark wallet {sparkWallet.WalletId} drained");
        _output.WriteLine($"Spark wallet {sparkWallet.WalletId}: {balance.OwnedSats} owned, {balance.IncomingSats} incoming");

        var wallets = await client.GetSparkWalletsAsync(new GetSparkWalletsRequest(), headers);
        wallets.Wallets.Single(w => w.Id == sparkWallet.WalletId).NodeIds.Should().Contain(alice.Id);

        // alice still uses it, so it can't be archived
        var archive = async () => await client.ArchiveSparkWalletAsync(new ArchiveSparkWalletRequest { WalletId = sparkWallet.WalletId }, headers);
        var refusal = (await archive.Should().ThrowAsync<RpcException>()).Which;
        refusal.StatusCode.Should().Be(StatusCode.FailedPrecondition);
        refusal.Status.Detail.Should().Contain("alice");
    }

    [SparkE2EFact]
    public async Task SwapOut_LeavesAtTheirTimelockFloor_AreRenewedAndExitedUnderTheirOwnIds()
    {
        var client = CreateClient(out var headers);
        var rpc = CreateBitcoindRpc();
        var alice = (await WaitForNodesAsync(client, headers)).Single(n => n.Name == "alice");
        const long amount = 300_000;

        var sparkWallet = await client.CreateSparkWalletAsync(
            new CreateSparkWalletRequest { Name = $"e2e renew {Guid.NewGuid():N}"[..20], MaxBalanceSats = 2_000_000 }, headers);
        _output.WriteLine($"Spark wallet {sparkWallet.WalletId}: identity {sparkWallet.IdentityPublicKey}");
        await SetSparkConfigAsync(alice.Id, sparkWallet.WalletId, maxSwapsInFlight: 1);

        var request = new RequestSwapOutRequest
        {
            NodeId = alice.Id,
            Provider = SWAP_PROVIDER.Spark,
            AmountSats = amount,
            WalletId = int.Parse(Env("E2E_SPARK_PAYOUT_WALLET_ID", "2")),
            ReferenceId = $"spark-e2e-renew-{Guid.NewGuid():N}"
        };

        // With NBXplorer down, the exit job attributes the swap's transfer but can't reserve its exit address: the swap
        // holds its own leaves, not exited yet, while they are aged
        string[] leafIds;
        var parentsBefore = new Dictionary<string, string?>();
        var splicedAbove = new HashSet<string>();
        await ComposeServiceAsync("nbxplorer", "stop");
        try
        {
            var started = await StartSwapAsync(client, headers, request);
            started.Status.Should().Be(SWAP_OUT_STATUS.SwapOutPending, started.Error ?? "alice's payment into Spark should have succeeded");

            var attributed = await PollAsync(() => SwapLeavesAsync(request.ReferenceId), s => s.LeafIds.Length > 0,
                attempts: 30, delay: TimeSpan.FromSeconds(10), what: "the swap's transfer attributed");
            attributed.TxId.Should().BeNull("its exit can't be sent while NBXplorer is down");
            leafIds = attributed.LeafIds;
            _output.WriteLine($"swap {started.SwapId}: leaves {string.Join(", ", leafIds)}");

            // As if moved ~18 times: refund timelocks at 150, where the operators refuse to move a leaf until it is
            // renewed. Each leaf keeps the node transaction its place in the tree gives it, which picks its renewal: a
            // root leaf (straight from a deposit, node timelock 0) gets a zero-timelock renewal and a leaf under a
            // parent a node renewal for the first one (node timelock 150), a refund renewal for the rest (node
            // timelock 2000). The first two splice a split node above the leaf
            var nodeRenewalDone = false;
            foreach (var leafId in leafIds)
            {
                var before = await OperatorLeafAsync(leafId);
                parentsBefore[leafId] = before.ParentId;
                uint nodeTimelock;
                if (before.ParentId is null)
                {
                    nodeTimelock = 0;
                    splicedAbove.Add(leafId);
                }
                else if (!nodeRenewalDone)
                {
                    nodeTimelock = 150;
                    nodeRenewalDone = true;
                    splicedAbove.Add(leafId);
                }
                else
                {
                    nodeTimelock = 2000;
                }

                await AgeLeafAsync(leafId, nodeTimelock, refundTimelock: 150);
                (await OperatorLeafAsync(leafId)).RefundTimelock.Should().Be(150);
            }
        }
        finally
        {
            await ComposeServiceAsync("nbxplorer", "start");
        }

        // The next exit renews the leaves and moves them; mining confirms the exit
        var done = await PollAsync(
            async () =>
            {
                await MineAsync(rpc, 1);
                return await client.GetSwapOutAsync(new GetSwapOutRequest { ReferenceId = request.ReferenceId }, headers);
            },
            s => s.Status != SWAP_OUT_STATUS.SwapOutPending,
            attempts: 60, delay: TimeSpan.FromSeconds(10), what: "the swap's completion");
        _output.WriteLine($"swap {done.SwapId}: {done.Status}, payout {done.PayoutSats} to {done.DestinationAddress} in {done.TxId}, " +
                          $"fees ln={done.LightningFeeSats} service={done.ServiceFeeSats} onchain={done.OnchainFeeSats} {done.Error}");
        done.Status.Should().Be(SWAP_OUT_STATUS.SwapOutCompleted, done.Error);

        // Exactly its own leaves left, under their own ids, each renewed first
        (await SwapLeavesAsync(request.ReferenceId)).LeafIds.Should().Equal(leafIds, "a renewal keeps a leaf's id");
        foreach (var leafId in leafIds)
        {
            var after = await OperatorLeafAsync(leafId);
            after.RefundTimelock.Should().BeGreaterThanOrEqualTo(1800,
                "it was renewed (refunds back to 2000) before the exit moved it; at 150 the operators would not have moved it");
            if (splicedAbove.Contains(leafId))
            {
                after.ParentId.Should().NotBe(parentsBefore[leafId], "its renewal spliced a split node above it, keeping the leaf's id");
            }
        }

        // And the amounts add up: what landed on-chain is what the swap delivered, the fees everything it did not
        var destination = BitcoinAddress.Create(done.DestinationAddress, Network.RegTest);
        var payoutTx = await rpc.GetRawTransactionAsync(uint256.Parse(done.TxId));
        var payout = payoutTx.Outputs.Where(o => o.ScriptPubKey == destination.ScriptPubKey).Sum(o => o.Value.Satoshi);
        done.PayoutSats.Should().Be(payout);
        (done.OnchainFeeSats + done.ServiceFeeSats).Should().Be(amount - payout);
    }

    /// <summary>
    /// Creating a swap pays, so it is never retried blindly. Unavailable (the Spark wallet still connecting after
    /// NodeGuard's start) is refused before anything happens, so only that is waited out
    /// </summary>
    private async Task<RequestSwapOutResponse> StartSwapAsync(NodeGuardService.NodeGuardServiceClient client, Metadata headers,
        RequestSwapOutRequest request)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return await client.RequestSwapOutAsync(request, headers);
            }
            catch (RpcException e) when (e.StatusCode == StatusCode.Unavailable && attempt < 12)
            {
                _output.WriteLine($"Spark not ready yet ({e.Status.Detail}), attempt {attempt}");
                await Task.Delay(TimeSpan.FromSeconds(10));
            }
        }
    }

    /// <summary>A node's Spark wallet and Max swaps in flight are liquidity config, set on the Nodes page; there is no RPC for them</summary>
    private static async Task SetSparkConfigAsync(int nodeId, int sparkWalletId, int maxSwapsInFlight)
    {
        await using var connection = new NpgsqlConnection(Env("POSTGRES_CONNECTIONSTRING",
            "Host=localhost;Port=5432;Database=nodeguard;User ID=postgres;"));
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            "UPDATE \"Nodes\" SET \"SparkWalletId\" = @wallet, \"MaxSwapsInFlight\" = @maxSwapsInFlight WHERE \"Id\" = @node", connection);
        command.Parameters.AddWithValue("wallet", sparkWalletId);
        command.Parameters.AddWithValue("maxSwapsInFlight", maxSwapsInFlight);
        command.Parameters.AddWithValue("node", nodeId);
        (await command.ExecuteNonQueryAsync()).Should().Be(1);
    }

    /// <summary>The leaves NodeGuard recorded for a swap (once its transfer is attributed) and its exit's txid</summary>
    private static async Task<(string[] LeafIds, string? TxId)> SwapLeavesAsync(string referenceId)
    {
        await using var connection = new NpgsqlConnection(Env("POSTGRES_CONNECTIONSTRING",
            "Host=localhost;Port=5432;Database=nodeguard;User ID=postgres;"));
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            "SELECT \"SparkLeafIds\", \"TxId\" FROM \"SwapOuts\" WHERE \"ReferenceId\" = @reference", connection);
        command.Parameters.AddWithValue("reference", referenceId);
        await using var reader = await command.ExecuteReaderAsync();
        if (!await reader.ReadAsync()) return ([], null);

        var leafIds = reader.IsDBNull(0) ? [] : reader.GetString(0).Split(',', StringSplitOptions.RemoveEmptyEntries);
        return (leafIds, reader.IsDBNull(1) ? null : reader.GetString(1));
    }

    /// <summary>A leaf as operator 0 has it: its refund transaction's timelock and its parent node (none for a tree's root)</summary>
    private static async Task<(long RefundTimelock, string? ParentId)> OperatorLeafAsync(string leafId)
    {
        await using var connection = new NpgsqlConnection(Env("SPARK_OPERATOR_POSTGRES",
            "Host=spark-postgres;Port=5432;Database=sparkoperator_0;Username=postgres"));
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            "SELECT raw_refund_tx, tree_node_parent FROM tree_nodes WHERE id = @id", connection);
        command.Parameters.AddWithValue("id", Guid.Parse(leafId));
        await using var reader = await command.ExecuteReaderAsync();
        (await reader.ReadAsync()).Should().BeTrue($"operator 0 should have leaf {leafId}");

        var refund = Transaction.Load((byte[])reader[0], Network.RegTest);
        return (refund.Inputs[0].Sequence.Value & 0xFFFF, reader.IsDBNull(1) ? null : reader.GetGuid(1).ToString());
    }

    /// <summary>
    /// Sets a leaf's node and refund timelocks on every operator, through the test RPC local operators serve
    /// (mock.MockService/modify_node_timelock, anonymous when they run with -local)
    /// </summary>
    private async Task AgeLeafAsync(string leafId, uint nodeTimelock, uint refundTimelock)
    {
        var method = new Method<byte[], byte[]>(MethodType.Unary, "mock.MockService", "modify_node_timelock",
            Marshallers.Create(b => b, b => b), Marshallers.Create(b => b, b => b));

        // ModifyNodeTimelockRequest { string node_id = 1; uint32 node_timelock = 2; uint32 refund_timelock = 3; }
        using var buffer = new MemoryStream();
        var message = new CodedOutputStream(buffer);
        message.WriteTag(1, WireFormat.WireType.LengthDelimited);
        message.WriteString(leafId);
        message.WriteTag(2, WireFormat.WireType.Varint);
        message.WriteUInt32(nodeTimelock);
        message.WriteTag(3, WireFormat.WireType.Varint);
        message.WriteUInt32(refundTimelock);
        message.Flush();

        var operators = Env("SPARK_OPERATOR_URLS", "https://spark-operator-0:8535,https://spark-operator-1:8535,https://spark-operator-2:8535");
        foreach (var url in operators.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            // Their TLS certificates are throwaway regtest ones, made when the stack starts
            using var channel = GrpcChannel.ForAddress(url, new GrpcChannelOptions
            {
                HttpHandler = new SocketsHttpHandler { SslOptions = { RemoteCertificateValidationCallback = (_, _, _, _) => true } }
            });
            await channel.CreateCallInvoker().AsyncUnaryCall(method, null, new CallOptions(), buffer.ToArray());
        }

        _output.WriteLine($"leaf {leafId}: node timelock {nodeTimelock}, refund timelock {refundTimelock} on every operator");
    }

    /// <summary>
    /// Restarts NodeGuard's container: the E2E_NODEGUARD_CONTAINER container, else the nodeguard service of the
    /// runner's own compose project
    /// </summary>
    private Task RestartNodeGuardAsync() =>
        ComposeServiceAsync("nodeguard", "restart?t=15", Environment.GetEnvironmentVariable("E2E_NODEGUARD_CONTAINER"));

    /// <summary>
    /// Stops, starts or restarts a service of the runner's own compose project (or <paramref name="container"/>) through
    /// the Docker Engine API; the runner mounts the socket
    /// </summary>
    private async Task ComposeServiceAsync(string service, string action, string? container = null)
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
        using var docker = new HttpClient(handler) { BaseAddress = new Uri("http://localhost") };

        if (string.IsNullOrEmpty(container))
        {
            // The runner's hostname is its container id
            using var self = JsonDocument.Parse(await docker.GetStringAsync($"/containers/{Environment.MachineName}/json"));
            var project = self.RootElement.GetProperty("Config").GetProperty("Labels").GetProperty("com.docker.compose.project").GetString();
            var filters = Uri.EscapeDataString(JsonSerializer.Serialize(new
            {
                label = new[] { $"com.docker.compose.project={project}", $"com.docker.compose.service={service}" }
            }));
            using var found = JsonDocument.Parse(await docker.GetStringAsync($"/containers/json?all=true&filters={filters}"));
            container = found.RootElement.EnumerateArray().Single().GetProperty("Id").GetString();
        }

        _output.WriteLine($"docker {action} {service} ({container})");
        using var response = await docker.PostAsync($"/containers/{container}/{action}", content: null);
        // 304: already in that state
        if (!response.IsSuccessStatusCode && response.StatusCode != System.Net.HttpStatusCode.NotModified)
        {
            throw new InvalidOperationException(
                $"docker {action} {container} failed: {(int)response.StatusCode} {await response.Content.ReadAsStringAsync()}");
        }
    }
}
