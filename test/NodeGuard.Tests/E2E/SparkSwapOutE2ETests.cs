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

using FluentAssertions;
using Grpc.Core;
using NBitcoin;
using Nodeguard;
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
/// A Spark swap-out through NodeGuard's gRPC API against the local Spark network (docker/spark): alice's
/// LND pays an invoice of NodeGuard's Spark wallet, MonitorSwapsJob claims the transfer and exits the
/// wallet on-chain to the address reserved in a NodeGuard wallet, and the swap completes once that payout
/// confirms.
///
///   E2E_SPARK_WALLET_ID   NodeGuard wallet the swap pays out to (default 2)
/// </summary>
[Trait("Category", "E2E")]
[Collection("E2E")]
public class SparkSwapOutE2ETests : E2ETestBase
{
    public SparkSwapOutE2ETests(ITestOutputHelper output) : base(output)
    {
    }

    [SparkE2EFact]
    public async Task SwapOut_ViaSpark_PaysOutToTheReservedAddressAndCompletes()
    {
        var client = CreateClient(out var headers);
        var rpc = CreateBitcoindRpc();
        var alice = (await WaitForNodesAsync(client, headers)).Single(n => n.Name == "alice");
        var walletId = int.Parse(Env("E2E_SPARK_WALLET_ID", "2"));
        var referenceId = $"spark-e2e-{Guid.NewGuid():N}";
        const long amount = 300_000;

        var request = new RequestSwapOutRequest
        {
            NodeId = alice.Id,
            Provider = SWAP_PROVIDER.Spark,
            AmountSats = amount,
            WalletId = walletId,
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

        _output.WriteLine($"swap {started.SwapId}: {started.Status}, provider id {started.ProviderId}, payout to {started.DestinationAddress}");
        started.Status.Should().Be(SWAP_OUT_STATUS.SwapOutPending, started.Error ?? "alice's payment into Spark should have succeeded");

        // MonitorSwapsJob (every minute in a dev environment) claims and exits; mining confirms the exit
        var done = await PollAsync(
            async () =>
            {
                await MineAsync(rpc, 1);
                return await client.GetSwapOutAsync(new GetSwapOutRequest { ReferenceId = referenceId }, headers);
            },
            s => s.Status != SWAP_OUT_STATUS.SwapOutPending,
            attempts: 60, delay: TimeSpan.FromSeconds(10), what: $"swap {started.SwapId} completion");
        _output.WriteLine($"swap {done.SwapId}: {done.Status}, payout tx {done.TxId}, fees ln={done.LightningFeeSats} ssp={done.ServiceFeeSats} {done.Error}");

        done.Status.Should().Be(SWAP_OUT_STATUS.SwapOutCompleted, done.Error);
        done.SwapId.Should().Be(started.SwapId);
        done.DestinationAddress.Should().Be(started.DestinationAddress);
        done.HasPaymentHash.Should().BeTrue();

        var destination = BitcoinAddress.Create(done.DestinationAddress, Network.RegTest);
        var payoutTx = await rpc.GetRawTransactionAsync(uint256.Parse(done.TxId));
        var payout = payoutTx.Outputs.Where(o => o.ScriptPubKey == destination.ScriptPubKey).Sum(o => o.Value.Satoshi);
        payout.Should().Be(amount - done.ServiceFeeSats, "the exit pays the swapped amount minus the SSP's fee");

        // Asking again with the same reference_id returns the same swap rather than paying again
        var again = await client.RequestSwapOutAsync(request, headers);
        again.SwapId.Should().Be(started.SwapId);
        again.Status.Should().Be(SWAP_OUT_STATUS.SwapOutCompleted);
    }
}
