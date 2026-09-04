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
using NBitcoin.RPC;
using Nodeguard;
using Xunit.Abstractions;

namespace NodeGuard.Tests.E2E;

/// <summary>
/// End-to-end coverage for RBF fee bumping of a hot-wallet withdrawal against a LIVE NodeGuard + bitcoind, driven over
/// gRPC: RequestWithdrawal broadcasts an RBF-signalling transaction; BumpWithdrawal replaces it with a higher-fee version
/// of the same payment; bitcoind evicts the original; NodeGuard marks the original WITHDRAWAL_BUMPED and, once mined,
/// settles the replacement. The UI and the RPC share WithdrawalRequestService, so this scenario exercises the same
/// orchestration the Withdrawals page runs.
///
/// Shared plumbing in <see cref="E2ETestBase"/>; gated by <see cref="E2EFactAttribute"/>. Env: see
/// <see cref="DustUtxoWithdrawalE2ETests"/>. Never mine between the withdrawal and the bump — the original must still be
/// unconfirmed. Settlement needs MonitorWithdrawalsJob to run promptly (MONITOR_WITHDRAWALS_CRON is 10s in the e2e stack).
/// </summary>
[Trait("Category", "E2E")]
[Collection("E2E")]
public class WithdrawalRbfBumpE2ETests : E2ETestBase
{
    private const long WithdrawalAmountSats = 1_000_000;
    private const int OriginalFeeRateSatPerVb = 2;
    private const int BumpedFeeRateSatPerVb = 10;
    private const long ProbeAmountSats = 2_000_000;

    public WithdrawalRbfBumpE2ETests(ITestOutputHelper output) : base(output)
    {
    }

    [E2EFact]
    public async Task BumpWithdrawal_ReplacesTheUnconfirmedHotWalletTransaction()
    {
        var client = CreateClient(out var headers);
        var rpc = CreateBitcoindRpc();
        var walletId = int.Parse(Env("E2E_HOT_WALLET_ID", "3"));

        // 0. NodeGuard up and the dev hot wallet spendable (DbInitializer funds it; another e2e may hold it briefly).
        await RetryAsync(async () =>
        {
            var resp = await client.GetNodesAsync(new GetNodesRequest(), headers);
            if (resp.Nodes.Count == 0) throw new InvalidOperationException("no nodes seeded yet");
            return true;
        }, attempts: 90, delay: TimeSpan.FromSeconds(4), what: "GetNodes (NodeGuard readiness)");

        await RetryAsync(async () =>
        {
            var available = await client.GetAvailableUtxosAsync(
                new GetAvailableUtxosRequest { WalletId = walletId, Amount = ProbeAmountSats }, headers);
            if (available.Confirmed.Sum(u => u.Amount) < ProbeAmountSats)
                throw new InvalidOperationException("hot wallet has no spendable UTXO yet");
            return true;
        }, attempts: 60, delay: TimeSpan.FromSeconds(4), what: "GetAvailableUtxos (hot wallet funded)");

        // 1. A hot-wallet withdrawal at a low fee rate. NodeGuard signs and broadcasts it asynchronously.
        var destination = await rpc.GetNewAddressAsync();
        var withdrawal = await client.RequestWithdrawalAsync(new RequestWithdrawalRequest
        {
            WalletId = walletId,
            Description = "E2E RBF bump test",
            Destinations = { new Destination { Address = destination.ToString(), AmountSats = WithdrawalAmountSats } },
            MempoolFeeRate = FEES_TYPE.CustomFee,
            CustomFeeRate = OriginalFeeRateSatPerVb,
        }, headers);
        _output.WriteLine($"withdrawal request {withdrawal.RequestId} → txid {withdrawal.Txid}");
        withdrawal.IsHotWallet.Should().BeTrue();
        var originalTxId = uint256.Parse(withdrawal.Txid);

        var originalTx = await WaitForBroadcastAsync(rpc, originalTxId, "original withdrawal broadcast");
        originalTx.RBF.Should().BeTrue("withdrawals must signal BIP125 replaceability, or they could never be bumped");
        var originalEntry = await rpc.GetMempoolEntryAsync(originalTxId);

        await PollAsync(() => GetStatusesAsync(client, headers, withdrawal.RequestId),
            s => s[withdrawal.RequestId].Status == WITHDRAWAL_REQUEST_STATUS.WithdrawalPendingConfirmation,
            attempts: 30, delay: TimeSpan.FromSeconds(2), what: "original pending confirmation");

        // 2. Bump it. Nothing has been mined, so the original is still replaceable.
        var bump = await client.BumpWithdrawalAsync(new BumpWithdrawalRequest
        {
            RequestId = withdrawal.RequestId,
            MempoolFeeRate = FEES_TYPE.CustomFee,
            CustomFeeRate = BumpedFeeRateSatPerVb,
        }, headers);
        _output.WriteLine($"bump request {bump.RequestId} → txid {bump.Txid}");
        bump.IsHotWallet.Should().BeTrue();
        bump.RequestId.Should().NotBe(withdrawal.RequestId, "a bump is a new withdrawal request pointing at the one it replaces");
        bump.Txid.Should().NotBe(withdrawal.Txid, "a higher fee shrinks the change output, hence a different txid");
        var bumpTxId = uint256.Parse(bump.Txid);

        // 3. The replacement is in the mempool, the original was evicted, and the replacement is the same payment at a
        //    higher fee: same inputs, same destination output, smaller change.
        var bumpTx = await WaitForBroadcastAsync(rpc, bumpTxId, "replacement broadcast");
        await RetryAsync(async () =>
        {
            var evicted = await rpc.GetMempoolEntryAsync(originalTxId, throwIfNotFound: false);
            return evicted == null ? true : throw new InvalidOperationException("original still in the mempool");
        }, attempts: 15, delay: TimeSpan.FromSeconds(2), what: "original evicted by the replacement");

        bumpTx.Inputs.Select(i => i.PrevOut).Should().BeEquivalentTo(originalTx.Inputs.Select(i => i.PrevOut),
            "RBF replaces the very same inputs");
        bumpTx.Outputs.Should().ContainSingle(
            o => o.ScriptPubKey == destination.ScriptPubKey && o.Value == Money.Satoshis(WithdrawalAmountSats),
            "the destination and its amount are untouched");
        ChangeSats(bumpTx, destination).Should().BeLessThan(ChangeSats(originalTx, destination),
            "the extra fee comes out of the change");

        var bumpEntry = await rpc.GetMempoolEntryAsync(bumpTxId);
        bumpEntry.BaseFee.Satoshi.Should().BeGreaterThan(originalEntry.BaseFee.Satoshi, "BIP125 requires the replacement to pay more");
        new FeeRate(bumpEntry.BaseFee, bumpTx.GetVirtualSize()).SatoshiPerByte.Should()
            .BeGreaterThanOrEqualTo(BumpedFeeRateSatPerVb * 0.8m,
                "the replacement targets the requested rate (the fee is estimated on the unsigned size)");

        // NodeGuard's view: the bump is pending confirmation with the replacement txid, the original is bumped.
        var statuses = await PollAsync(() => GetStatusesAsync(client, headers, withdrawal.RequestId, bump.RequestId),
            s => s[bump.RequestId].Status == WITHDRAWAL_REQUEST_STATUS.WithdrawalPendingConfirmation
                 && s[withdrawal.RequestId].Status == WITHDRAWAL_REQUEST_STATUS.WithdrawalBumped,
            attempts: 30, delay: TimeSpan.FromSeconds(2), what: "bump pending confirmation, original bumped");
        statuses[bump.RequestId].TxId.Should().Be(bump.Txid);

        // 4. Guards: a bumped request cannot be bumped again, and a bump must raise the fee rate.
        var bumpAgain = () => client.BumpWithdrawalAsync(new BumpWithdrawalRequest
        {
            RequestId = withdrawal.RequestId,
            MempoolFeeRate = FEES_TYPE.CustomFee,
            CustomFeeRate = BumpedFeeRateSatPerVb + 5,
        }, headers).ResponseAsync;
        (await bumpAgain.Should().ThrowAsync<RpcException>()).Which.StatusCode.Should().Be(StatusCode.FailedPrecondition,
            "the original is WITHDRAWAL_BUMPED, not pending confirmation");

        var bumpLower = () => client.BumpWithdrawalAsync(new BumpWithdrawalRequest
        {
            RequestId = bump.RequestId,
            MempoolFeeRate = FEES_TYPE.CustomFee,
            CustomFeeRate = BumpedFeeRateSatPerVb - 5,
        }, headers).ResponseAsync;
        (await bumpLower.Should().ThrowAsync<RpcException>()).Which.StatusCode.Should().Be(StatusCode.InvalidArgument,
            "a replacement must pay a higher fee rate than the transaction it replaces");

        // 5. Mine: the replacement confirms, the original never does, and NodeGuard settles the bump.
        await MineAsync(rpc, 6);

        var bumpInfo = await rpc.GetRawTransactionInfoAsync(bumpTxId);
        ((uint?)bumpInfo.Confirmations ?? 0).Should().BeGreaterThan(0u, "the replacement was mined");
        (await rpc.GetRawTransactionAsync(originalTxId, throwIfNotFound: false)).Should().BeNull(
            "the replaced transaction was evicted and can never confirm");

        var settled = await PollAsync(() => GetStatusesAsync(client, headers, withdrawal.RequestId, bump.RequestId),
            s => s[bump.RequestId].Status == WITHDRAWAL_REQUEST_STATUS.WithdrawalSettled,
            attempts: 40, delay: TimeSpan.FromSeconds(5), what: "bump settled by MonitorWithdrawalsJob");
        settled[withdrawal.RequestId].Status.Should().Be(WITHDRAWAL_REQUEST_STATUS.WithdrawalBumped,
            "the replaced request stays bumped");
    }

    private async Task<Transaction> WaitForBroadcastAsync(RPCClient rpc, uint256 txId, string what)
    {
        return await RetryAsync(async () =>
        {
            var tx = await rpc.GetRawTransactionAsync(txId, throwIfNotFound: false);
            return tx ?? throw new InvalidOperationException($"{txId} not broadcast yet");
        }, attempts: 30, delay: TimeSpan.FromSeconds(4), what: what);
    }

    private static async Task<Dictionary<int, WithdrawalRequest>> GetStatusesAsync(
        NodeGuardService.NodeGuardServiceClient client, Metadata headers, params int[] requestIds)
    {
        var response = await client.GetWithdrawalsRequestStatusAsync(
            new GetWithdrawalsRequestStatusRequest { RequestIds = { requestIds } }, headers);
        return response.WithdrawalRequests.ToDictionary(r => r.RequestId);
    }

    private static long ChangeSats(Transaction tx, BitcoinAddress destination)
        => tx.Outputs.Where(o => o.ScriptPubKey != destination.ScriptPubKey).Sum(o => o.Value.Satoshi);
}
