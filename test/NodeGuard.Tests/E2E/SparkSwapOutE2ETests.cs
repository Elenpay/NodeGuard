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
using Grpc.Core;
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
///
///   E2E_SPARK_PAYOUT_WALLET_ID   NodeGuard on-chain wallet the swap pays out to (default 2)
///   POSTGRES_CONNECTIONSTRING    NodeGuard's database, to give alice the Spark wallet
///   DOCKER_SOCKET                Docker Engine API socket (default /var/run/docker.sock), to restart NodeGuard
///   E2E_NODEGUARD_CONTAINER      NodeGuard's container (default: the nodeguard service of the runner's compose project)
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

    /// <summary>
    /// Restarts NodeGuard's container through the Docker Engine API (the runner mounts the socket): the
    /// E2E_NODEGUARD_CONTAINER container, else the nodeguard service of the runner's own compose project.
    /// </summary>
    private async Task RestartNodeGuardAsync()
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

        var container = Environment.GetEnvironmentVariable("E2E_NODEGUARD_CONTAINER");
        if (string.IsNullOrEmpty(container))
        {
            // The runner's hostname is its container id
            using var self = JsonDocument.Parse(await docker.GetStringAsync($"/containers/{Environment.MachineName}/json"));
            var project = self.RootElement.GetProperty("Config").GetProperty("Labels").GetProperty("com.docker.compose.project").GetString();
            var filters = Uri.EscapeDataString(JsonSerializer.Serialize(new
            {
                label = new[] { $"com.docker.compose.project={project}", "com.docker.compose.service=nodeguard" }
            }));
            using var found = JsonDocument.Parse(await docker.GetStringAsync($"/containers/json?filters={filters}"));
            container = found.RootElement.EnumerateArray().Single().GetProperty("Id").GetString();
        }

        _output.WriteLine($"Restarting NodeGuard ({container})");
        using var restarted = await docker.PostAsync($"/containers/{container}/restart?t=15", content: null);
        if (!restarted.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(
                $"docker restart {container} failed: {(int)restarted.StatusCode} {await restarted.Content.ReadAsStringAsync()}");
        }
    }
}
