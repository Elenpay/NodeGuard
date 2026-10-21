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
/// End-to-end coverage for the RBF replacements of a hot-wallet withdrawal against a LIVE NodeGuard + bitcoind, driven
/// over gRPC. Fee bump: RequestWithdrawal broadcasts an RBF-signalling transaction, BumpWithdrawal replaces it with a
/// higher-fee version of the same payment, bitcoind evicts the original, NodeGuard keeps reporting the original
/// pending confirmation (pointing at the bump) and stores WITHDRAWAL_BUMPED once the replacement is mined and settled.
/// Cancellation: CancelWithdrawal replaces it with a transaction that returns the funds to the wallet, the destination
/// is never paid and the original ends up WITHDRAWAL_CANCELLED. Mined original: either replacement loses the race to
/// the original, which NodeGuard must then settle while failing every replacement, also when the original was bumped
/// twice. Only confirmations change an RBF chain, so no status reported here ever flips once it is final. The UI and
/// the RPCs share WithdrawalRequestService, so these scenarios exercise the same orchestration the Withdrawals page runs.
///
/// Shared plumbing in <see cref="E2ETestBase"/>; gated by <see cref="E2EFactAttribute"/>. Env: see
/// <see cref="DustUtxoWithdrawalE2ETests"/>. Never mine between the withdrawal and its replacement — the original must
/// still be unconfirmed. Settlement needs MonitorWithdrawalsJob to run promptly (MONITOR_WITHDRAWALS_CRON is 10s in the
/// e2e stack).
/// </summary>
[Trait("Category", "E2E")]
[Collection("E2E")]
public class WithdrawalRbfBumpE2ETests : E2ETestBase
{
    private const long WithdrawalAmountSats = 1_000_000;
    private const int OriginalFeeRateSatPerVb = 2;
    private const int BumpedFeeRateSatPerVb = 10;
    private const int SecondBumpFeeRateSatPerVb = 20;
    private const long ProbeAmountSats = 2_000_000;

    public WithdrawalRbfBumpE2ETests(ITestOutputHelper output) : base(output)
    {
    }

    [E2EFact]
    public async Task BumpWithdrawal_ReplacesTheUnconfirmedHotWalletTransaction()
    {
        var client = CreateClient(out var headers);
        var rpc = CreateBitcoindRpc();
        var walletId = await EnsureHotWalletReadyAsync(client, headers);

        // 1. A hot-wallet withdrawal at a low fee rate. NodeGuard signs and broadcasts it asynchronously.
        var destination = await rpc.GetNewAddressAsync();
        var withdrawal = await RequestHotWithdrawalAsync(client, headers, walletId, destination, "E2E RBF bump test");
        var originalTxId = uint256.Parse(withdrawal.Txid);

        var originalTx = await WaitForBroadcastAsync(rpc, originalTxId, "original withdrawal broadcast");
        originalTx.RBF.Should().BeTrue("withdrawals must signal BIP125 replaceability, or they could never be bumped");
        var originalEntry = await rpc.GetMempoolEntryAsync(originalTxId);

        await PollAsync(() => GetStatusesAsync(client, headers, withdrawal.RequestId),
            s => s[withdrawal.RequestId].Status == WITHDRAWAL_REQUEST_STATUS.WithdrawalPendingConfirmation,
            attempts: 30, delay: TimeSpan.FromSeconds(2), what: "original pending confirmation");

        // 2. Bump it, addressed by the txid RequestWithdrawal returned (the id form is exercised by the guards below).
        //    Nothing has been mined, so the original is still replaceable.
        var bump = await client.BumpWithdrawalAsync(new BumpWithdrawalRequest
        {
            TxId = withdrawal.Txid,
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
        await WaitUntilEvictedAsync(rpc, originalTxId);

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

        // NodeGuard's view: the bump is pending confirmation with the replacement txid; the original stays pending
        // confirmation too, pointing at the bump, since nothing final is reported before a confirmation.
        var statuses = await PollAsync(() => GetStatusesAsync(client, headers, withdrawal.RequestId, bump.RequestId),
            s => s[bump.RequestId].Status == WITHDRAWAL_REQUEST_STATUS.WithdrawalPendingConfirmation
                 && s[withdrawal.RequestId].ReplacedByRequestId == bump.RequestId,
            attempts: 30, delay: TimeSpan.FromSeconds(2), what: "bump pending confirmation, original pointing at it");
        statuses[bump.RequestId].TxId.Should().Be(bump.Txid);
        statuses[withdrawal.RequestId].Status.Should().Be(WITHDRAWAL_REQUEST_STATUS.WithdrawalPendingConfirmation,
            "only a confirmation changes an RBF chain: the original could still be mined instead");

        // 4. Guards: a bumped request cannot be bumped again, a bump must raise the fee rate, and an unknown txid is not found.
        var bumpAgain = () => client.BumpWithdrawalAsync(new BumpWithdrawalRequest
        {
            RequestId = withdrawal.RequestId,
            MempoolFeeRate = FEES_TYPE.CustomFee,
            CustomFeeRate = BumpedFeeRateSatPerVb + 5,
        }, headers).ResponseAsync;
        (await bumpAgain.Should().ThrowAsync<RpcException>()).Which.StatusCode.Should().Be(StatusCode.FailedPrecondition,
            "the original already has a live replacement");

        var bumpLower = () => client.BumpWithdrawalAsync(new BumpWithdrawalRequest
        {
            RequestId = bump.RequestId,
            MempoolFeeRate = FEES_TYPE.CustomFee,
            CustomFeeRate = BumpedFeeRateSatPerVb - 5,
        }, headers).ResponseAsync;
        (await bumpLower.Should().ThrowAsync<RpcException>()).Which.StatusCode.Should().Be(StatusCode.InvalidArgument,
            "a replacement must pay a higher fee rate than the transaction it replaces");

        var unknownTxId = () => client.BumpWithdrawalAsync(new BumpWithdrawalRequest
        {
            TxId = uint256.One.ToString(),
            MempoolFeeRate = FEES_TYPE.CustomFee,
            CustomFeeRate = BumpedFeeRateSatPerVb,
        }, headers).ResponseAsync;
        (await unknownTxId.Should().ThrowAsync<RpcException>()).Which.StatusCode.Should().Be(StatusCode.NotFound,
            "a txid NodeGuard never broadcast cannot be bumped");

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
            "once the replacement settled, Bumped is the replaced request's stored, terminal status");
        settled[withdrawal.RequestId].RejectOrCancelReason.Should().Contain(bump.RequestId.ToString());
    }

    [E2EFact]
    public async Task CancelWithdrawal_ReturnsTheFundsOfTheUnconfirmedHotWalletTransaction()
    {
        var client = CreateClient(out var headers);
        var rpc = CreateBitcoindRpc();
        var walletId = await EnsureHotWalletReadyAsync(client, headers);

        // 1. A hot-wallet withdrawal at a low fee rate, broadcast but unconfirmed.
        var destination = await rpc.GetNewAddressAsync();
        var withdrawal = await RequestHotWithdrawalAsync(client, headers, walletId, destination, "E2E RBF cancel test");
        var originalTxId = uint256.Parse(withdrawal.Txid);

        var originalTx = await WaitForBroadcastAsync(rpc, originalTxId, "original withdrawal broadcast");
        var originalEntry = await rpc.GetMempoolEntryAsync(originalTxId);

        await PollAsync(() => GetStatusesAsync(client, headers, withdrawal.RequestId),
            s => s[withdrawal.RequestId].Status == WITHDRAWAL_REQUEST_STATUS.WithdrawalPendingConfirmation,
            attempts: 30, delay: TimeSpan.FromSeconds(2), what: "original pending confirmation");

        // 2. Cancel it by txid: same inputs, the funds come back to the wallet at a higher fee rate.
        var cancel = await client.CancelWithdrawalAsync(new CancelWithdrawalRequest
        {
            TxId = withdrawal.Txid,
            MempoolFeeRate = FEES_TYPE.CustomFee,
            CustomFeeRate = BumpedFeeRateSatPerVb,
        }, headers);
        _output.WriteLine($"cancel request {cancel.RequestId} → txid {cancel.Txid}");
        cancel.IsHotWallet.Should().BeTrue();
        cancel.RequestId.Should().NotBe(withdrawal.RequestId);
        cancel.Txid.Should().NotBe(withdrawal.Txid);
        var cancelTxId = uint256.Parse(cancel.Txid);

        // 3. The replacement evicted the original and pays nobody but the wallet itself.
        var cancelTx = await WaitForBroadcastAsync(rpc, cancelTxId, "cancellation broadcast");
        await WaitUntilEvictedAsync(rpc, originalTxId);

        cancelTx.Inputs.Select(i => i.PrevOut).Should().BeEquivalentTo(originalTx.Inputs.Select(i => i.PrevOut),
            "RBF replaces the very same inputs");
        cancelTx.Outputs.Should().ContainSingle("everything goes back to one fresh address of the wallet");
        cancelTx.Outputs.Should().NotContain(o => o.ScriptPubKey == destination.ScriptPubKey,
            "the original destination must never be paid");

        var cancelEntry = await rpc.GetMempoolEntryAsync(cancelTxId);
        cancelEntry.BaseFee.Satoshi.Should().BeGreaterThan(originalEntry.BaseFee.Satoshi, "BIP125 requires the replacement to pay more");
        (cancelTx.TotalOut + cancelEntry.BaseFee).Should().Be(originalTx.TotalOut + originalEntry.BaseFee,
            "both transactions spend the same inputs: whatever is not fee comes back to the wallet");

        // NodeGuard's view: the cancellation is pending confirmation; the original stays pending confirmation too,
        // pointing at it and with no reason yet. A cancellation is only reported once it can no longer be outrun.
        var statuses = await PollAsync(() => GetStatusesAsync(client, headers, withdrawal.RequestId, cancel.RequestId),
            s => s[cancel.RequestId].Status == WITHDRAWAL_REQUEST_STATUS.WithdrawalPendingConfirmation
                 && s[withdrawal.RequestId].ReplacedByRequestId == cancel.RequestId,
            attempts: 30, delay: TimeSpan.FromSeconds(2), what: "cancellation pending confirmation, original pointing at it");
        statuses[cancel.RequestId].TxId.Should().Be(cancel.Txid);
        statuses[withdrawal.RequestId].Status.Should().Be(WITHDRAWAL_REQUEST_STATUS.WithdrawalPendingConfirmation,
            "an integrator that refunds on WITHDRAWAL_CANCELLED must never see it before the cancellation confirmed");
        statuses[withdrawal.RequestId].RejectOrCancelReason.Should().BeEmpty();

        // 4. Guard: a cancelled request cannot be replaced again.
        var cancelAgain = () => client.CancelWithdrawalAsync(new CancelWithdrawalRequest
        {
            RequestId = withdrawal.RequestId,
            MempoolFeeRate = FEES_TYPE.CustomFee,
            CustomFeeRate = BumpedFeeRateSatPerVb + 5,
        }, headers).ResponseAsync;
        (await cancelAgain.Should().ThrowAsync<RpcException>()).Which.StatusCode.Should().Be(StatusCode.FailedPrecondition);

        // 5. Mine: the cancellation confirms, the original never does, the funds are a spendable UTXO of the wallet again.
        await MineAsync(rpc, 6);
        (await rpc.GetRawTransactionAsync(originalTxId, throwIfNotFound: false)).Should().BeNull(
            "the cancelled transaction was evicted and can never confirm");

        var settled = await PollAsync(() => GetStatusesAsync(client, headers, withdrawal.RequestId, cancel.RequestId),
            s => s[cancel.RequestId].Status == WITHDRAWAL_REQUEST_STATUS.WithdrawalSettled,
            attempts: 40, delay: TimeSpan.FromSeconds(5), what: "cancellation settled by MonitorWithdrawalsJob");
        settled[withdrawal.RequestId].Status.Should().Be(WITHDRAWAL_REQUEST_STATUS.WithdrawalCancelled,
            "once the cancellation settled, Cancelled is the replaced request's stored, terminal status");
        settled[withdrawal.RequestId].RejectOrCancelReason.Should().Contain($"request {cancel.RequestId}");

        var returnedOutpoint = new OutPoint(cancelTxId, 0).ToString();
        await RetryAsync(async () =>
        {
            var utxos = await client.GetUtxosAsync(new GetUtxosRequest(), headers);
            return utxos.Confirmed.Any(u => u.Outpoint == returnedOutpoint)
                ? true
                : throw new InvalidOperationException("returned funds not indexed as a confirmed UTXO of the wallet yet");
        }, attempts: 30, delay: TimeSpan.FromSeconds(4), what: "returned funds spendable again");
    }

    /// <summary>
    /// A replacement is not guaranteed to win: a miner that never saw it can still mine the original. The original's
    /// payment is then the one that happened, so NodeGuard must settle the original and fail the replacement, instead of
    /// leaving the original Bumped / Cancelled while its transaction is confirmed on-chain.
    /// </summary>
    [E2ETheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Replacement_WhenTheOriginalIsMinedInstead_SettlesTheOriginalAndFailsTheReplacement(bool cancellation)
    {
        var client = CreateClient(out var headers);
        var rpc = CreateBitcoindRpc();
        var walletId = await EnsureHotWalletReadyAsync(client, headers);

        // 1. A hot-wallet withdrawal at a low fee rate. Its signed transaction is kept: it is what the other miner mines.
        var destination = await rpc.GetNewAddressAsync();
        var referenceId = $"e2e-rbf-mined-original-{Guid.NewGuid():N}";
        var withdrawal = await RequestHotWithdrawalAsync(client, headers, walletId, destination,
            $"E2E RBF {(cancellation ? "cancel" : "bump")} mined-original test", referenceId);
        var originalTxId = uint256.Parse(withdrawal.Txid);
        var originalTx = await WaitForBroadcastAsync(rpc, originalTxId, "original withdrawal broadcast");

        await PollAsync(() => GetStatusesAsync(client, headers, withdrawal.RequestId),
            s => s[withdrawal.RequestId].Status == WITHDRAWAL_REQUEST_STATUS.WithdrawalPendingConfirmation,
            attempts: 30, delay: TimeSpan.FromSeconds(2), what: "original pending confirmation");

        // 2. Replace it: bitcoind evicts the original; NodeGuard keeps it pending confirmation, pointing at the replacement.
        var (replacementId, replacementTxId) = await ReplaceAsync(client, headers, withdrawal.Txid, cancellation);

        await WaitForBroadcastAsync(rpc, replacementTxId, "replacement broadcast");
        await WaitUntilEvictedAsync(rpc, originalTxId);
        var inFlight = await PollAsync(() => GetStatusesAsync(client, headers, withdrawal.RequestId, replacementId),
            s => s[replacementId].Status == WITHDRAWAL_REQUEST_STATUS.WithdrawalPendingConfirmation
                 && s[withdrawal.RequestId].ReplacedByRequestId == replacementId,
            attempts: 30, delay: TimeSpan.FromSeconds(2), what: "replacement pending confirmation, original pointing at it");
        inFlight[withdrawal.RequestId].Status.Should().Be(WITHDRAWAL_REQUEST_STATUS.WithdrawalPendingConfirmation,
            "nothing final is reported while the original could still be mined, which is exactly what happens next");

        // 3. The original is mined instead, which evicts the replacement as a double spend.
        await MineBypassingTheMempoolAsync(rpc, originalTx);
        await MineAsync(rpc, 5);

        var originalInfo = await rpc.GetRawTransactionInfoAsync(originalTxId);
        ((uint?)originalInfo.Confirmations ?? 0).Should().BeGreaterThan(0u, "the original was mined");
        (await rpc.GetRawTransactionAsync(replacementTxId, throwIfNotFound: false)).Should().BeNull(
            "the replacement double-spends a confirmed transaction and can never confirm");

        // 4. NodeGuard's view must follow the chain: the original settled, the replacement failed.
        var settled = await PollAsync(() => GetStatusesAsync(client, headers, withdrawal.RequestId, replacementId),
            s =>
            {
                _output.WriteLine($"original {s[withdrawal.RequestId].Status}, replacement {s[replacementId].Status}");
                return s[withdrawal.RequestId].Status == WITHDRAWAL_REQUEST_STATUS.WithdrawalSettled
                       && s[replacementId].Status == WITHDRAWAL_REQUEST_STATUS.WithdrawalFailed;
            },
            attempts: 40, delay: TimeSpan.FromSeconds(5), what: "original settled, replacement failed");

        settled[withdrawal.RequestId].TxId.Should().Be(withdrawal.Txid, "the original's own transaction is the one that confirmed");
        settled[withdrawal.RequestId].Confirmations.Should().BeGreaterThan(0UL);
        if (cancellation)
        {
            settled[withdrawal.RequestId].RejectOrCancelReason.Should().BeEmpty(
                "the cancellation never took effect: the destination was paid");
        }

        await AssertReferenceReturnsTheChainAsync(client, headers, referenceId, withdrawal.RequestId, withdrawal.RequestId, replacementId);
    }

    /// <summary>
    /// The same race one bump deeper: A is bumped to B, B is bumped to C, and A is mined. NodeGuard must settle A and fail
    /// both B and C: each double-spends the confirmed transaction and can never confirm.
    /// </summary>
    [E2EFact]
    public async Task BumpOfABump_WhenTheFirstTransactionIsMined_SettlesItAndFailsBothReplacements()
    {
        var client = CreateClient(out var headers);
        var rpc = CreateBitcoindRpc();
        var walletId = await EnsureHotWalletReadyAsync(client, headers);

        // 1. A: a hot-wallet withdrawal at a low fee rate. Its signed transaction is kept: it is what gets mined.
        var destination = await rpc.GetNewAddressAsync();
        var referenceId = $"e2e-rbf-bump-of-a-bump-{Guid.NewGuid():N}";
        var first = await RequestHotWithdrawalAsync(client, headers, walletId, destination,
            "E2E RBF bump-of-a-bump mined-first test", referenceId);
        var firstTxId = uint256.Parse(first.Txid);
        var firstTx = await WaitForBroadcastAsync(rpc, firstTxId, "first withdrawal broadcast");

        await PollAsync(() => GetStatusesAsync(client, headers, first.RequestId),
            s => s[first.RequestId].Status == WITHDRAWAL_REQUEST_STATUS.WithdrawalPendingConfirmation,
            attempts: 30, delay: TimeSpan.FromSeconds(2), what: "first pending confirmation");

        // 2. B bumps A.
        var (secondId, secondTxId) = await ReplaceAsync(client, headers, first.Txid, cancellation: false);
        await WaitForBroadcastAsync(rpc, secondTxId, "first bump broadcast");
        await WaitUntilEvictedAsync(rpc, firstTxId);

        await PollAsync(() => GetStatusesAsync(client, headers, first.RequestId, secondId),
            s => s[secondId].Status == WITHDRAWAL_REQUEST_STATUS.WithdrawalPendingConfirmation
                 && s[first.RequestId].ReplacedByRequestId == secondId,
            attempts: 30, delay: TimeSpan.FromSeconds(2), what: "first bump pending confirmation, first pointing at it");

        // 3. C bumps B at a higher fee rate again: three transactions over the same inputs, only C in the mempool.
        var (thirdId, thirdTxId) = await ReplaceAsync(client, headers, secondTxId.ToString(), cancellation: false,
            SecondBumpFeeRateSatPerVb);
        var thirdTx = await WaitForBroadcastAsync(rpc, thirdTxId, "second bump broadcast");
        await WaitUntilEvictedAsync(rpc, secondTxId);

        thirdTx.Inputs.Select(i => i.PrevOut).Should().BeEquivalentTo(firstTx.Inputs.Select(i => i.PrevOut),
            "every replacement spends the inputs of the first transaction");
        var inFlight = await PollAsync(() => GetStatusesAsync(client, headers, first.RequestId, secondId, thirdId),
            s => s[thirdId].Status == WITHDRAWAL_REQUEST_STATUS.WithdrawalPendingConfirmation
                 && s[secondId].ReplacedByRequestId == thirdId,
            attempts: 30, delay: TimeSpan.FromSeconds(2), what: "second bump pending confirmation, first bump pointing at it");
        inFlight.Values.Select(r => r.Status).Should().OnlyContain(s => s == WITHDRAWAL_REQUEST_STATUS.WithdrawalPendingConfirmation,
            "every transaction of the chain is still a candidate until one confirms");

        // 4. The oldest transaction, A, is mined, which evicts C as a double spend.
        await MineBypassingTheMempoolAsync(rpc, firstTx);
        await MineAsync(rpc, 5);

        var firstInfo = await rpc.GetRawTransactionInfoAsync(firstTxId);
        ((uint?)firstInfo.Confirmations ?? 0).Should().BeGreaterThan(0u, "the first transaction was mined");
        (await rpc.GetRawTransactionAsync(secondTxId, throwIfNotFound: false)).Should().BeNull("B can never confirm");
        (await rpc.GetRawTransactionAsync(thirdTxId, throwIfNotFound: false)).Should().BeNull("C can never confirm");

        // 5. NodeGuard's view must follow the chain: A settled, B and C failed.
        var settled = await PollAsync(() => GetStatusesAsync(client, headers, first.RequestId, secondId, thirdId),
            s =>
            {
                _output.WriteLine($"A {s[first.RequestId].Status}, B {s[secondId].Status}, C {s[thirdId].Status}");
                return s[first.RequestId].Status == WITHDRAWAL_REQUEST_STATUS.WithdrawalSettled
                       && s[secondId].Status == WITHDRAWAL_REQUEST_STATUS.WithdrawalFailed
                       && s[thirdId].Status == WITHDRAWAL_REQUEST_STATUS.WithdrawalFailed;
            },
            attempts: 40, delay: TimeSpan.FromSeconds(5), what: "first settled, both bumps failed");

        settled[first.RequestId].TxId.Should().Be(first.Txid, "A's own transaction is the one that confirmed");
        settled[first.RequestId].Confirmations.Should().BeGreaterThan(0UL);
        settled[secondId].RejectOrCancelReason.Should().Contain(first.RequestId.ToString(),
            "B double-spends the confirmed transaction; the failure names the request that was mined instead");
        settled[thirdId].RejectOrCancelReason.Should().Contain(first.RequestId.ToString(),
            "the failure names the request that was mined instead");

        await AssertReferenceReturnsTheChainAsync(client, headers, referenceId, first.RequestId, first.RequestId, secondId, thirdId);
    }

    // ---- plumbing ------------------------------------------------------------------------------------------------

    /// <summary>Bumps or cancels <paramref name="txId"/> at <paramref name="feeRate"/> sat/vB.</summary>
    private async Task<(int RequestId, uint256 TxId)> ReplaceAsync(NodeGuardService.NodeGuardServiceClient client,
        Metadata headers, string txId, bool cancellation, int feeRate = BumpedFeeRateSatPerVb)
    {
        if (cancellation)
        {
            var cancel = await client.CancelWithdrawalAsync(new CancelWithdrawalRequest
            {
                TxId = txId,
                MempoolFeeRate = FEES_TYPE.CustomFee,
                CustomFeeRate = feeRate,
            }, headers);
            _output.WriteLine($"cancel request {cancel.RequestId} → txid {cancel.Txid}");
            return (cancel.RequestId, uint256.Parse(cancel.Txid));
        }

        var bump = await client.BumpWithdrawalAsync(new BumpWithdrawalRequest
        {
            TxId = txId,
            MempoolFeeRate = FEES_TYPE.CustomFee,
            CustomFeeRate = feeRate,
        }, headers);
        _output.WriteLine($"bump request {bump.RequestId} → txid {bump.Txid}");
        return (bump.RequestId, uint256.Parse(bump.Txid));
    }

    /// <summary>
    /// Mines <paramref name="tx"/> as a miner that never saw its replacements would: generateblock takes the raw
    /// transaction, bypassing the mempool the replacement sits in.
    /// </summary>
    private async Task MineBypassingTheMempoolAsync(RPCClient rpc, Transaction tx)
    {
        var minerAddress = await rpc.GetNewAddressAsync();
        var block = await rpc.SendCommandAsync("generateblock", minerAddress.ToString(), new[] { tx.ToHex() });
        _output.WriteLine($"{tx.GetHash()} mined in block {block.Result["hash"]}");
    }

    /// <summary>NodeGuard up and the dev hot wallet spendable (DbInitializer funds it; another e2e may hold it briefly).</summary>
    private async Task<int> EnsureHotWalletReadyAsync(NodeGuardService.NodeGuardServiceClient client, Metadata headers)
    {
        var walletId = int.Parse(Env("E2E_HOT_WALLET_ID", "3"));

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

        return walletId;
    }

    private async Task<RequestWithdrawalResponse> RequestHotWithdrawalAsync(NodeGuardService.NodeGuardServiceClient client,
        Metadata headers, int walletId, BitcoinAddress destination, string description, string? referenceId = null)
    {
        var request = new RequestWithdrawalRequest
        {
            WalletId = walletId,
            Description = description,
            Destinations = { new Destination { Address = destination.ToString(), AmountSats = WithdrawalAmountSats } },
            MempoolFeeRate = FEES_TYPE.CustomFee,
            CustomFeeRate = OriginalFeeRateSatPerVb,
        };
        if (referenceId != null) request.ReferenceId = referenceId;

        var withdrawal = await client.RequestWithdrawalAsync(request, headers);
        _output.WriteLine($"withdrawal request {withdrawal.RequestId} → txid {withdrawal.Txid}");
        withdrawal.IsHotWallet.Should().BeTrue();
        return withdrawal;
    }

    private async Task<Transaction> WaitForBroadcastAsync(RPCClient rpc, uint256 txId, string what)
    {
        return await RetryAsync(async () =>
        {
            var tx = await rpc.GetRawTransactionAsync(txId, throwIfNotFound: false);
            return tx ?? throw new InvalidOperationException($"{txId} not broadcast yet");
        }, attempts: 30, delay: TimeSpan.FromSeconds(4), what: what);
    }

    private async Task WaitUntilEvictedAsync(RPCClient rpc, uint256 txId)
    {
        await RetryAsync(async () =>
        {
            var entry = await rpc.GetMempoolEntryAsync(txId, throwIfNotFound: false);
            return entry == null ? true : throw new InvalidOperationException("original still in the mempool");
        }, attempts: 15, delay: TimeSpan.FromSeconds(2), what: "original evicted by the replacement");
    }

    /// <summary>
    /// What a caller tracking the withdrawal by reference id sees: every request of the chain, oldest first, each with
    /// its own status. The one whose transaction confirmed reads as settled, and every replacement of it reads as
    /// failed, so a paid withdrawal never reads as failed and the caller can tell which transaction paid it.
    /// </summary>
    private static async Task AssertReferenceReturnsTheChainAsync(NodeGuardService.NodeGuardServiceClient client,
        Metadata headers, string referenceId, int settledRequestId, params int[] chainRequestIds)
    {
        var response = await client.GetWithdrawalsRequestStatusByReferenceIdsAsync(
            new GetWithdrawalsRequestStatusByReferenceIdsRequest { ReferenceIds = { referenceId } }, headers);

        response.WithdrawalRequests.Select(r => r.RequestId).Should().Equal(chainRequestIds,
            "every request carrying the reference id is returned, oldest first");
        response.WithdrawalRequests.Single(r => r.RequestId == settledRequestId).Status
            .Should().Be(WITHDRAWAL_REQUEST_STATUS.WithdrawalSettled, "the request whose transaction confirmed is the payment that happened");
        response.WithdrawalRequests.Where(r => r.RequestId != settledRequestId)
            .Should().OnlyContain(r => r.Status == WITHDRAWAL_REQUEST_STATUS.WithdrawalFailed,
                "every replacement double-spends the confirmed transaction");
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
