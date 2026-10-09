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
/// <item>a Spark wallet is created over gRPC and made alice's Spark wallet (node liquidity config, set in the
/// database as the Nodes page would);</item>
/// <item>NodeGuard restarts, so the wallet's seed has to decrypt with the Data Protection key ring in Postgres;</item>
/// <item>alice's LND pays an invoice of the Spark wallet, MonitorSwapsJob follows the payment until it settles,
/// SparkSwapExitJob records the transfer that brought it in and exits exactly its leaves on-chain to the address
/// reserved in a NodeGuard wallet, and the swap completes once that payout confirms;</item>
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
    public async Task SwapOut_ThroughASparkWallet_PaysOutToTheReservedAddressAndCompletes()
    {
        var client = CreateClient(out var headers);
        var rpc = CreateBitcoindRpc();
        var alice = (await WaitForNodesAsync(client, headers)).Single(n => n.Name == "alice");
        var payoutWalletId = int.Parse(Env("E2E_SPARK_PAYOUT_WALLET_ID", "2"));
        var referenceId = $"spark-e2e-{Guid.NewGuid():N}";
        const long amount = 300_000;

        // A Spark wallet for alice's swaps
        var sparkWallet = await client.CreateSparkWalletAsync(
            new CreateSparkWalletRequest { Name = $"e2e transit {Guid.NewGuid():N}"[..20], MaxBalanceSats = 2_000_000 }, headers);
        _output.WriteLine($"Spark wallet {sparkWallet.WalletId}: identity {sparkWallet.IdentityPublicKey}");
        await SetSparkWalletAsync(alice.Id, sparkWallet.WalletId);

        // Its seed must still decrypt after a restart, with the key ring NodeGuard keeps in Postgres
        await RestartNodeGuardAsync();
        await WaitForNodesAsync(client, headers);

        var request = new RequestSwapOutRequest
        {
            NodeId = alice.Id,
            Provider = SWAP_PROVIDER.Spark,
            AmountSats = amount,
            WalletId = payoutWalletId,
            ReferenceId = referenceId
        };

        // Creating a swap pays, so it is never retried blindly. Unavailable (the Spark wallet still
        // connecting after NodeGuard's start) is refused before anything happens, so only that is waited out
        RequestSwapOutResponse started = null!;
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                started = await client.RequestSwapOutAsync(request, headers);
                break;
            }
            catch (RpcException e) when (e.StatusCode == StatusCode.Unavailable && attempt < 12)
            {
                _output.WriteLine($"Spark not ready yet ({e.Status.Detail}), attempt {attempt}");
                await Task.Delay(TimeSpan.FromSeconds(10));
            }
        }

        _output.WriteLine($"swap {started.SwapId}: {started.Status}, provider id {started.ProviderId}");
        started.Status.Should().Be(SWAP_OUT_STATUS.SwapOutPending, started.Error ?? "alice's payment into Spark should have succeeded");
        started.DestinationAddress.Should().BeEmpty("a Spark swap reserves its address when it exits");

        // MonitorSwapsJob sees the payment settle and SparkSwapExitJob exits the swap's leaves (each every minute in a
        // dev environment); mining confirms the exit
        var done = await PollAsync(
            async () =>
            {
                await MineAsync(rpc, 1);
                return await client.GetSwapOutAsync(new GetSwapOutRequest { ReferenceId = referenceId }, headers);
            },
            s => s.Status != SWAP_OUT_STATUS.SwapOutPending,
            attempts: 60, delay: TimeSpan.FromSeconds(10), what: $"swap {started.SwapId} completion");
        _output.WriteLine($"swap {done.SwapId}: {done.Status}, payout tx {done.TxId} to {done.DestinationAddress}, fees ln={done.LightningFeeSats} ssp={done.ServiceFeeSats} {done.Error}");

        done.Status.Should().Be(SWAP_OUT_STATUS.SwapOutCompleted, done.Error);
        done.SwapId.Should().Be(started.SwapId);
        done.DestinationAddress.Should().NotBeNullOrEmpty("the swap reserved its address when it exited");
        done.HasPaymentHash.Should().BeTrue();

        var destination = BitcoinAddress.Create(done.DestinationAddress, Network.RegTest);
        var payoutTx = await rpc.GetRawTransactionAsync(uint256.Parse(done.TxId));
        var payout = payoutTx.Outputs.Where(o => o.ScriptPubKey == destination.ScriptPubKey).Sum(o => o.Value.Satoshi);
        payout.Should().Be(amount - done.ServiceFeeSats, "the exit pays the swapped amount minus the SSP's fee");

        // Asking again with the same reference_id returns the same swap rather than paying again
        var again = await client.RequestSwapOutAsync(request, headers);
        again.SwapId.Should().Be(started.SwapId);
        again.Status.Should().Be(SWAP_OUT_STATUS.SwapOutCompleted);

        // The swap went through alice's Spark wallet, which it left empty
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

    /// <summary>A node's Spark wallet is liquidity config, set on the Nodes page; there is no RPC for it</summary>
    private static async Task SetSparkWalletAsync(int nodeId, int sparkWalletId)
    {
        await using var connection = new NpgsqlConnection(Env("POSTGRES_CONNECTIONSTRING",
            "Host=localhost;Port=5432;Database=nodeguard;User ID=postgres;"));
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand("UPDATE \"Nodes\" SET \"SparkWalletId\" = @wallet WHERE \"Id\" = @node", connection);
        command.Parameters.AddWithValue("wallet", sparkWalletId);
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
