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
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NBitcoin;
using NBitcoin.RPC;
using Nodeguard;
using NodeGuard.Data;
using NodeGuard.Data.Models;
using NodeGuard.Data.Repositories;
using Npgsql;
using Xunit.Abstractions;

namespace NodeGuard.Tests.E2E;

/// <summary>
/// End-to-end guarantee for the withdrawal approval binding: the PSBT repository — the boundary every
/// approval crosses before it is counted — only accepts an approval whose transaction is the one the
/// withdrawal request was raised for, and a rejected approval never advances the signature threshold.
///
/// PSBT approval is reachable only from Withdrawals.razor over the Blazor circuit (no gRPC method exists for
/// it, and no background job picks up requests left in PSBTSignaturesPending), so the furthest this can be
/// exercised from outside is the repository — which is exactly where the server-side check belongs. The
/// requests are created through the real gRPC API and the approvals are stored through the real
/// <see cref="WalletWithdrawalRequestPsbtRepository"/>, with the real EF model evaluating the threshold.
///
/// A cold wallet (E2E_COLD_WALLET_ID) is used throughout, so nothing is ever broadcast and no funds move.
/// Gated by <see cref="E2EFactAttribute"/>; also self-skips when POSTGRES_CONNECTIONSTRING is unset, since
/// it reaches the database directly. Connection via env, all with dev-stack defaults:
///   NODEGUARD_GRPC_ENDPOINT   default http://localhost:50051 (h2c)
///   POSTGRES_CONNECTIONSTRING (required for this suite to run)
///   BITCOIND_RPC_URL/USER/PASS  default http://localhost:18443 / polaruser / polarpass
///   E2E_COLD_WALLET_ID        cold multisig wallet to exercise (default 2)
/// </summary>
[Trait("Category", "E2E")]
[Collection("E2E")]
public class PsbtApprovalBindingE2ETests
{
    private const string RequestTag = "e2e-psbt-binding";

    private readonly ITestOutputHelper _output;
    private readonly List<int> _createdRequestIds = new();

    public PsbtApprovalBindingE2ETests(ITestOutputHelper output)
    {
        _output = output;
        AppContext.SetSwitch("System.Net.Http.SocketsHttpHandler.Http2UnencryptedSupport", true);
    }

    /// <summary>
    /// An approval whose transaction differs from the request's approved template is rejected when stored,
    /// so an approver's signature can never be applied to a transaction the request never described.
    /// </summary>
    [E2EFact]
    public async Task ApprovalForADifferentTransaction_IsRejectedAtInsert()
    {
        if (!DatabaseAvailable()) return;

        var (requestA, requestB) = await CreateTwoWithdrawalRequestsAsync();
        try
        {
            var templateA = await GetTemplatePsbtAsync(requestA);
            var templateB = await GetTemplatePsbtAsync(requestB);

            var hashA = PSBT.Parse(templateA, Network.RegTest).GetGlobalTransaction().GetHash();
            var hashB = PSBT.Parse(templateB, Network.RegTest).GetGlobalTransaction().GetHash();
            hashA.Should().NotBe(hashB, "the two requests must describe different transactions");

            // Submit request B's transaction as an approval of request A — exactly what the Approve button
            // stores, but for a transaction A never approved.
            var result = await CreatePsbtRepository().AddAsync(new WalletWithdrawalRequestPSBT
            {
                WalletWithdrawalRequestId = requestA,
                PSBT = templateB,
                SignerId = await GetAnyUserIdAsync(),
            });

            _output.WriteLine($"AddAsync(foreign PSBT) -> success={result.Item1} message={result.Item2}");

            result.Item1.Should().BeFalse(
                "a PSBT whose transaction differs from the request's template must be rejected at insert");
        }
        finally
        {
            await CleanupAsync();
        }
    }

    /// <summary>
    /// A rejected approval is not stored, so it does not advance the request toward "all required signatures
    /// collected". Submitting the request's own (unsigned) template as an approval is refused and leaves the
    /// threshold unmet.
    /// </summary>
    [E2EFact]
    public async Task RejectedApproval_DoesNotAdvanceTheThreshold()
    {
        if (!DatabaseAvailable()) return;

        var (requestA, requestB) = await CreateTwoWithdrawalRequestsAsync();
        try
        {
            var templateA = await GetTemplatePsbtAsync(requestA);
            var signerId = await GetAnyUserIdAsync();

            for (var i = 1; i <= 2; i++)
            {
                var stored = await CreatePsbtRepository().AddAsync(new WalletWithdrawalRequestPSBT
                {
                    WalletWithdrawalRequestId = requestA,
                    PSBT = templateA,
                    SignerId = signerId,
                });
                _output.WriteLine($"AddAsync attempt {i}: success={stored.Item1} message={stored.Item2}");
            }

            var (collected, threshold, satisfied) = await ReadApprovalStateAsync(requestA);
            _output.WriteLine($"collected={collected} MofN={threshold} allRequiredCollected={satisfied}");

            satisfied.Should().BeFalse(
                "approvals the repository rejects must not be counted toward the multisig threshold");
        }
        finally
        {
            await CleanupAsync();
        }
    }

    /// <summary>
    /// A request is never marked fully approved on the strength of PSBTs describing a different transaction:
    /// even repeated submissions of a foreign transaction leave the request short of its threshold.
    /// </summary>
    [E2EFact]
    public async Task SubstitutedApprovals_DoNotMarkTheRequestAsFullyApproved()
    {
        if (!DatabaseAvailable()) return;

        var (requestA, requestB) = await CreateTwoWithdrawalRequestsAsync();
        try
        {
            var templateB = await GetTemplatePsbtAsync(requestB);
            var signerId = await GetAnyUserIdAsync();

            for (var i = 1; i <= 2; i++)
            {
                await CreatePsbtRepository().AddAsync(new WalletWithdrawalRequestPSBT
                {
                    WalletWithdrawalRequestId = requestA,
                    PSBT = templateB,
                    SignerId = signerId,
                });
            }

            var (collected, threshold, satisfied) = await ReadApprovalStateAsync(requestA);
            _output.WriteLine($"collected={collected} MofN={threshold} allRequiredCollected={satisfied}");

            satisfied.Should().BeFalse(
                "a request must never reach the fully-approved state from PSBTs describing a different " +
                "transaction");
        }
        finally
        {
            await CleanupAsync();
        }
    }

    // ---- environment ---------------------------------------------------------------------------------

    private static string Env(string name, string fallback)
        => Environment.GetEnvironmentVariable(name) is { Length: > 0 } v ? v : fallback;

    private static string? PostgresConnectionString
        => Environment.GetEnvironmentVariable("POSTGRES_CONNECTIONSTRING") is { Length: > 0 } v ? v : null;

    private static int ColdWalletId => int.Parse(Env("E2E_COLD_WALLET_ID", "2"));

    private bool DatabaseAvailable()
    {
        if (PostgresConnectionString is not null) return true;
        _output.WriteLine("POSTGRES_CONNECTIONSTRING not set — this suite reaches the database directly. Skipping.");
        return false;
    }

    private static NodeGuardService.NodeGuardServiceClient CreateGrpcClient(out Metadata headers)
    {
        headers = new Metadata { { "auth-token", Env("NODEGUARD_API_TOKEN", DefaultDevToken) } };
        var endpoint = Env("NODEGUARD_GRPC_ENDPOINT", "http://localhost:50051");
        return new NodeGuardService.NodeGuardServiceClient(GrpcChannel.ForAddress(endpoint));
    }

    private const string DefaultDevToken = "8rvSsUGeyXXdDQrHctcTey/xtHdZQEn945KHwccKp9Q=";

    private static RPCClient CreateBitcoindRpc()
    {
        var credential = new NetworkCredential(Env("BITCOIND_RPC_USER", "polaruser"), Env("BITCOIND_RPC_PASS", "polarpass"));
        return new RPCClient(credential, new Uri(Env("BITCOIND_RPC_URL", "http://localhost:18443")), Network.RegTest);
    }

    // ---- database access -----------------------------------------------------------------------------

    private static NpgsqlDataSource? _dataSource;
    private static readonly object DataSourceGate = new();

    private static IDbContextFactory<ApplicationDbContext> DbContextFactory()
    {
        var cs = PostgresConnectionString!;
        AppContext.SetSwitch("Npgsql.EnableLegacyTimestampBehavior", true);
        lock (DataSourceGate)
        {
            _dataSource ??= new NpgsqlDataSourceBuilder(cs).EnableDynamicJson().Build();
        }
        return new LocalDbContextFactory(_dataSource);
    }

    private sealed class LocalDbContextFactory : IDbContextFactory<ApplicationDbContext>
    {
        private readonly NpgsqlDataSource _dataSource;
        public LocalDbContextFactory(NpgsqlDataSource dataSource) => _dataSource = dataSource;

        public ApplicationDbContext CreateDbContext()
            => new(new DbContextOptionsBuilder<ApplicationDbContext>()
                .UseNpgsql(_dataSource, o => o.UseQuerySplittingBehavior(QuerySplittingBehavior.SingleQuery))
                .Options);
    }

    private static WalletWithdrawalRequestPsbtRepository CreatePsbtRepository()
        => new(
            new Repository<WalletWithdrawalRequestPSBT>(NullLogger<WalletWithdrawalRequestPSBT>.Instance),
            NullLogger<WalletWithdrawalRequestPsbtRepository>.Instance,
            DbContextFactory());

    private static async Task<string> GetTemplatePsbtAsync(int requestId)
    {
        await using var context = DbContextFactory().CreateDbContext();
        var template = await context.WalletWithdrawalRequestPSBTs
            .Where(x => x.WalletWithdrawalRequestId == requestId && x.IsTemplatePSBT)
            .Select(x => x.PSBT)
            .FirstOrDefaultAsync();

        return template ?? throw new InvalidOperationException(
            $"Request {requestId} has no template PSBT; it may not have reached PSBT generation.");
    }

    /// <summary>Reloads the request with the includes the production repository uses, so the NotMapped model
    /// logic (NumberOfSignaturesCollected / AreAllRequiredHumanSignaturesCollected) is evaluated as it is
    /// in the application.</summary>
    private static async Task<(int Collected, int Threshold, bool Satisfied)> ReadApprovalStateAsync(int requestId)
    {
        await using var context = DbContextFactory().CreateDbContext();
        var request = await context.WalletWithdrawalRequests
            .Include(x => x.Wallet).ThenInclude(x => x.InternalWallet)
            .Include(x => x.Wallet).ThenInclude(x => x.Keys)
            .Include(x => x.WalletWithdrawalRequestPSBTs)
            .Include(x => x.WalletWithdrawalRequestDestinations)
            .SingleOrDefaultAsync(x => x.Id == requestId)
            ?? throw new InvalidOperationException($"request {requestId} must still exist");

        return (request.NumberOfSignaturesCollected, request.Wallet.MofN,
                request.AreAllRequiredHumanSignaturesCollected);
    }

    private static async Task<string> GetAnyUserIdAsync()
    {
        await using var connection = new NpgsqlConnection(PostgresConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand("SELECT \"Id\" FROM \"AspNetUsers\" ORDER BY \"Id\" LIMIT 1", connection);
        var userId = (string?)await command.ExecuteScalarAsync();
        return userId ?? throw new InvalidOperationException("No users in the database.");
    }

    // ---- request creation & cleanup ------------------------------------------------------------------

    /// <summary>
    /// Creates two cold-wallet withdrawal requests to different destinations through the real gRPC API,
    /// funding the wallet from bitcoind first if needed (the seeded cold wallet is not funded by
    /// DbInitializer). Cold, so nothing is broadcast — each stays pending until approvals arrive.
    /// </summary>
    private async Task<(int RequestA, int RequestB)> CreateTwoWithdrawalRequestsAsync()
    {
        await EnsureColdWalletFundedAsync();

        var client = CreateGrpcClient(out var headers);
        var rpc = CreateBitcoindRpc();
        var sink = (await rpc.GetNewAddressAsync()).ToString();

        async Task<int> CreateAsync(string label, long amountSats)
        {
            try
            {
                var response = await client.RequestWithdrawalAsync(new RequestWithdrawalRequest
                {
                    WalletId = ColdWalletId,
                    Description = $"{RequestTag}-{label}-{Guid.NewGuid():N}",
                    Destinations = { new Destination { Address = sink, AmountSats = amountSats } },
                    MempoolFeeRate = FEES_TYPE.CustomFee,
                    CustomFeeRate = 2,
                }, headers);

                _output.WriteLine($"created request {label} id={response.RequestId}");
                _createdRequestIds.Add(response.RequestId);
                return response.RequestId;
            }
            catch (RpcException e)
            {
                throw new InvalidOperationException(
                    $"Could not create withdrawal request {label} on wallet {ColdWalletId} " +
                    $"({e.StatusCode}: {e.Status.Detail}). Check E2E_COLD_WALLET_ID is a funded cold " +
                    "multisig wallet.", e);
            }
        }

        // Different amounts guarantee different transactions, hence different template PSBTs.
        return (await CreateAsync("A", 100_000), await CreateAsync("B", 150_000));
    }

    private async Task EnsureColdWalletFundedAsync()
    {
        var client = CreateGrpcClient(out var headers);
        const long probeSats = 500_000;

        // Two UTXOs are needed: coin selection excludes outpoints already used by another pending request,
        // so with a single UTXO the second request cannot be built.
        const int requiredUtxos = 2;

        var available = await client.GetAvailableUtxosAsync(
            new GetAvailableUtxosRequest { WalletId = ColdWalletId, Amount = probeSats }, headers);
        if (available.Confirmed.Count >= requiredUtxos && available.Confirmed.Sum(u => u.Amount) >= probeSats)
            return;

        var rpc = CreateBitcoindRpc();
        for (var i = 0; i < 4; i++)
        {
            var address = await client.GetNewWalletAddressAsync(
                new GetNewWalletAddressRequest { WalletId = ColdWalletId, Skip = 0, Reserve = true }, headers);
            await rpc.SendToAddressAsync(BitcoinAddress.Create(address.Address, Network.RegTest), Money.Coins(0.25m));
        }

        await rpc.GenerateToAddressAsync(6, await rpc.GetNewAddressAsync());

        await RetryAsync(async () =>
        {
            var utxos = await client.GetAvailableUtxosAsync(
                new GetAvailableUtxosRequest { WalletId = ColdWalletId, Amount = probeSats }, headers);
            if (utxos.Confirmed.Count < requiredUtxos)
                throw new InvalidOperationException($"only {utxos.Confirmed.Count} confirmed UTXOs indexed so far");
            return true;
        }, attempts: 30, delay: TimeSpan.FromSeconds(4), what: "cold wallet funding indexed");
    }

    /// <summary>Removes the requests this suite created (children first) so a shared dev stack is left clean.</summary>
    private async Task CleanupAsync()
    {
        try
        {
            await using var connection = new NpgsqlConnection(PostgresConnectionString);
            await connection.OpenAsync();

            const string sql = """
                WITH doomed AS (SELECT "Id" FROM "WalletWithdrawalRequests" WHERE "Description" LIKE @prefix)
                DELETE FROM "FMUTXOWalletWithdrawalRequest" WHERE "WalletWithdrawalRequestsId" IN (SELECT "Id" FROM doomed);
                WITH doomed AS (SELECT "Id" FROM "WalletWithdrawalRequests" WHERE "Description" LIKE @prefix)
                DELETE FROM "WalletWithdrawalRequestPSBTs" WHERE "WalletWithdrawalRequestId" IN (SELECT "Id" FROM doomed);
                WITH doomed AS (SELECT "Id" FROM "WalletWithdrawalRequests" WHERE "Description" LIKE @prefix)
                DELETE FROM "WalletWithdrawalRequestDestinations" WHERE "WalletWithdrawalRequestId" IN (SELECT "Id" FROM doomed);
                DELETE FROM "WalletWithdrawalRequests" WHERE "Description" LIKE @prefix;
                """;
            await using var command = new NpgsqlCommand(sql, connection);
            command.Parameters.AddWithValue("prefix", RequestTag + "%");
            await command.ExecuteNonQueryAsync();
        }
        catch (Exception ex)
        {
            _output.WriteLine(
                $"cleanup failed: {ex.Message}. Remove WalletWithdrawalRequests whose Description starts " +
                $"with '{RequestTag}'.");
        }
    }

    private async Task<T> RetryAsync<T>(Func<Task<T>> action, int attempts, TimeSpan delay, string what)
    {
        Exception? last = null;
        for (var i = 0; i < attempts; i++)
        {
            try { return await action(); }
            catch (Exception ex) { last = ex; _output.WriteLine($"{what} attempt {i + 1}/{attempts}: {ex.Message}"); }
            await Task.Delay(delay);
        }
        throw new InvalidOperationException($"{what} did not succeed after {attempts} attempts", last);
    }
}
