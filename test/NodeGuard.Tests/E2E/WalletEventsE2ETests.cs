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
using Channel = System.Threading.Channels.Channel;

namespace NodeGuard.Tests.E2E;

/// <summary>
/// End-to-end coverage for SubscribeWalletEvents against a LIVE NodeGuard + bitcoind, driven over gRPC: a hot-wallet
/// withdrawal is followed from creation to confirmation on the stream, and an RBF bump links the original and its
/// replacement. Shared plumbing in <see cref="E2ETestBase"/>; gated by <see cref="E2EFactAttribute"/>. Confirmation
/// needs MonitorWithdrawalsJob to run promptly (MONITOR_WITHDRAWALS_CRON is 10s in the e2e stack).
/// </summary>
[Trait("Category", "E2E")]
[Collection("E2E")]
public class WalletEventsE2ETests : E2ETestBase
{
    private const long WithdrawalAmountSats = 1_000_000;
    private const int OriginalFeeRateSatPerVb = 2;
    private const int BumpedFeeRateSatPerVb = 10;
    private const long ProbeAmountSats = 2_000_000;

    public WalletEventsE2ETests(ITestOutputHelper output) : base(output)
    {
    }

    [E2EFact]
    public async Task SubscribeWalletEvents_FollowsAHotWithdrawalFromCreationToConfirmation()
    {
        var client = CreateClient(out var headers);
        var rpc = CreateBitcoindRpc();
        var walletId = await EnsureHotWalletReadyAsync(client, headers);
        await using var events = await WalletEventStream.OpenAsync(client, headers, walletId, _output);

        var withdrawal = await RequestHotWithdrawalAsync(client, headers, walletId, await rpc.GetNewAddressAsync(),
            "E2E wallet events");

        var created = await events.NextAsync(withdrawal.RequestId, WALLET_EVENT_TYPE.WalletEventWithdrawalCreated);
        created.Withdrawal.AmountSats.Should().Be(WithdrawalAmountSats);
        created.Withdrawal.Destinations.Should().ContainSingle(d => d.AmountSats == WithdrawalAmountSats);

        var broadcast = await events.NextAsync(withdrawal.RequestId, WALLET_EVENT_TYPE.WalletEventWithdrawalBroadcast);
        broadcast.Withdrawal.TxId.Should().Be(withdrawal.Txid);
        broadcast.Withdrawal.Status.Should().Be(WITHDRAWAL_REQUEST_STATUS.WithdrawalPendingConfirmation);
        (await rpc.GetRawTransactionAsync(uint256.Parse(withdrawal.Txid), throwIfNotFound: false))
            .Should().NotBeNull("the broadcast event follows the real broadcast");

        await MineAsync(rpc, 6);

        var confirmed = await events.NextAsync(withdrawal.RequestId, WALLET_EVENT_TYPE.WalletEventWithdrawalConfirmed,
            TimeSpan.FromMinutes(3));
        confirmed.Withdrawal.Status.Should().Be(WITHDRAWAL_REQUEST_STATUS.WithdrawalSettled);
        confirmed.Withdrawal.PreviousStatus.Should().Be(WITHDRAWAL_REQUEST_STATUS.WithdrawalPendingConfirmation);

        var seen = events.Seen(withdrawal.RequestId);
        seen.Should().ContainInOrder(
            WALLET_EVENT_TYPE.WalletEventWithdrawalCreated,
            WALLET_EVENT_TYPE.WalletEventWithdrawalSigning,
            WALLET_EVENT_TYPE.WalletEventWithdrawalBroadcast,
            WALLET_EVENT_TYPE.WalletEventWithdrawalConfirmed);
        seen.SkipWhile(t => t != WALLET_EVENT_TYPE.WalletEventWithdrawalBroadcast).Skip(1)
            .Should().Equal([WALLET_EVENT_TYPE.WalletEventWithdrawalConfirmed],
                "nothing may move a broadcast withdrawal backwards before it confirms");
    }

    [E2EFact]
    public async Task SubscribeWalletEvents_LinksABumpedWithdrawalAndItsReplacement()
    {
        var client = CreateClient(out var headers);
        var rpc = CreateBitcoindRpc();
        var walletId = await EnsureHotWalletReadyAsync(client, headers);
        await using var events = await WalletEventStream.OpenAsync(client, headers, walletId, _output);

        var withdrawal = await RequestHotWithdrawalAsync(client, headers, walletId, await rpc.GetNewAddressAsync(),
            "E2E wallet events bump");
        await events.NextAsync(withdrawal.RequestId, WALLET_EVENT_TYPE.WalletEventWithdrawalBroadcast);

        var bump = await client.BumpWithdrawalAsync(new BumpWithdrawalRequest
        {
            RequestId = withdrawal.RequestId,
            MempoolFeeRate = FEES_TYPE.CustomFee,
            CustomFeeRate = BumpedFeeRateSatPerVb,
        }, headers);
        _output.WriteLine($"bump request {bump.RequestId} → txid {bump.Txid}");

        var replacementCreated = await events.NextAsync(bump.RequestId, WALLET_EVENT_TYPE.WalletEventWithdrawalCreated);
        replacementCreated.Withdrawal.BumpingRequestId.Should().Be(withdrawal.RequestId);
        replacementCreated.Withdrawal.IsRbfCancellation.Should().BeFalse();

        var bumped = await events.NextAsync(withdrawal.RequestId, WALLET_EVENT_TYPE.WalletEventWithdrawalBumped);
        bumped.Withdrawal.ReplacedByRequestId.Should().Be(bump.RequestId);
        bumped.Withdrawal.Status.Should().Be(WITHDRAWAL_REQUEST_STATUS.WithdrawalBumped);

        var replacementBroadcast = await events.NextAsync(bump.RequestId, WALLET_EVENT_TYPE.WalletEventWithdrawalBroadcast);
        replacementBroadcast.Withdrawal.TxId.Should().Be(bump.Txid);

        // Confirm so the hot wallet's UTXOs are free for the next test
        await MineAsync(rpc, 6);
        await events.NextAsync(bump.RequestId, WALLET_EVENT_TYPE.WalletEventWithdrawalConfirmed, TimeSpan.FromMinutes(3));
    }

    [E2EFact]
    public async Task SubscribeWalletEvents_RefusesCallsWithoutAToken()
    {
        var client = CreateClient(out _);

        using var call = client.SubscribeWalletEvents(new Metadata());
        var act = async () =>
        {
            await call.RequestStream.WriteAsync(new WalletEventsClientMessage
            {
                Subscribe = new SubscribeWalletEventsRequest { WalletId = 1 }
            });
            await call.ResponseStream.MoveNext();
        };

        (await act.Should().ThrowAsync<RpcException>()).Which.StatusCode.Should().Be(StatusCode.Unauthenticated);
    }

    [E2EFact]
    public async Task SubscribeWalletEvents_UnknownWallet_IsNotFound()
    {
        var client = CreateClient(out var headers);

        using var call = client.SubscribeWalletEvents(headers);
        await call.RequestStream.WriteAsync(new WalletEventsClientMessage
        {
            Subscribe = new SubscribeWalletEventsRequest { WalletId = int.MaxValue }
        });
        var act = () => call.ResponseStream.MoveNext();

        (await act.Should().ThrowAsync<RpcException>()).Which.StatusCode.Should().Be(StatusCode.NotFound);
    }

    // ---- plumbing ------------------------------------------------------------------------------------------------

    /// <summary>
    /// An open SubscribeWalletEvents call whose events are read in the background, so the test can wait for one
    /// </summary>
    private sealed class WalletEventStream : IAsyncDisposable
    {
        private readonly AsyncDuplexStreamingCall<WalletEventsClientMessage, WalletEvent> _call;
        private readonly CancellationTokenSource _cts = new();
        private readonly System.Threading.Channels.Channel<WalletEvent> _received = Channel.CreateUnbounded<WalletEvent>();
        private readonly List<WalletEvent> _all = [];
        private readonly ITestOutputHelper _output;
        private readonly Task _reader;

        private WalletEventStream(AsyncDuplexStreamingCall<WalletEventsClientMessage, WalletEvent> call, ITestOutputHelper output)
        {
            _call = call;
            _output = output;
            _reader = Task.Run(async () =>
            {
                try
                {
                    await foreach (var e in _call.ResponseStream.ReadAllAsync(_cts.Token))
                    {
                        _output.WriteLine($"event {e.Type} request {e.Withdrawal?.RequestId} tx {e.Withdrawal?.TxId}");
                        lock (_all) _all.Add(e);
                        _received.Writer.TryWrite(e);
                    }
                    _received.Writer.TryComplete();
                }
                catch (Exception ex)
                {
                    _received.Writer.TryComplete(ex);
                }
            });
        }

        public static async Task<WalletEventStream> OpenAsync(NodeGuardService.NodeGuardServiceClient client,
            Metadata headers, int walletId, ITestOutputHelper output)
        {
            var stream = new WalletEventStream(client.SubscribeWalletEvents(headers), output);
            await stream._call.RequestStream.WriteAsync(new WalletEventsClientMessage
            {
                Subscribe = new SubscribeWalletEventsRequest { WalletId = walletId }
            });
            var subscribed = await stream.NextAsync(e => e.Type == WALLET_EVENT_TYPE.WalletEventSubscribed,
                TimeSpan.FromSeconds(30), "subscribed");
            subscribed.WalletId.Should().Be(walletId);
            return stream;
        }

        public Task<WalletEvent> NextAsync(int requestId, WALLET_EVENT_TYPE type, TimeSpan? timeout = null) =>
            NextAsync(e => e.Type == type && e.Withdrawal?.RequestId == requestId,
                timeout ?? TimeSpan.FromMinutes(2), $"{type} for request {requestId}");

        public List<WALLET_EVENT_TYPE> Seen(int requestId)
        {
            lock (_all) return _all.Where(e => e.Withdrawal?.RequestId == requestId).Select(e => e.Type).ToList();
        }

        private async Task<WalletEvent> NextAsync(Func<WalletEvent, bool> match, TimeSpan timeout, string what)
        {
            using var cts = new CancellationTokenSource(timeout);
            try
            {
                while (true)
                {
                    var e = await _received.Reader.ReadAsync(cts.Token);
                    if (match(e))
                        return e;
                }
            }
            catch (OperationCanceledException)
            {
                throw new TimeoutException($"No {what} event within {timeout}");
            }
        }

        public async ValueTask DisposeAsync()
        {
            await _cts.CancelAsync();
            try
            {
                await _reader;
            }
            catch
            {
                // The call was cancelled on purpose
            }

            _call.Dispose();
            _cts.Dispose();
        }
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
        Metadata headers, int walletId, BitcoinAddress destination, string description)
    {
        var withdrawal = await client.RequestWithdrawalAsync(new RequestWithdrawalRequest
        {
            WalletId = walletId,
            Description = description,
            Destinations = { new Destination { Address = destination.ToString(), AmountSats = WithdrawalAmountSats } },
            MempoolFeeRate = FEES_TYPE.CustomFee,
            CustomFeeRate = OriginalFeeRateSatPerVb,
        }, headers);
        _output.WriteLine($"withdrawal request {withdrawal.RequestId} → txid {withdrawal.Txid}");
        withdrawal.IsHotWallet.Should().BeTrue();
        return withdrawal;
    }
}
