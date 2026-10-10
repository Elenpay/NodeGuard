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

using Lnrpc;
using NBitcoin;
using NBXplorer.DerivationStrategy;
using NBXplorer.Models;
using NodeGuard.Data.Models;
using NodeGuard.Data.Repositories.Interfaces;
using NSpark;
using NSpark.Exceptions;
using NSpark.Models;
using NSpark.Services;

namespace NodeGuard.Services.Spark;

/// <summary>Where a paid Spark swap's exit stands.</summary>
public enum SparkExitStep
{
    /// <summary>Its transfer has not been found yet, or its leaves can't move yet.</summary>
    Waiting,

    /// <summary>The exit was sent; its payout has not confirmed yet.</summary>
    ExitSent,

    /// <summary>The payout confirmed: the swap is completed.</summary>
    Completed
}

/// <summary>
/// Spark as a swap-out provider:
/// <list type="number">
/// <item>creation: NodeGuard's Spark wallet issues a BOLT11 invoice (through the SSP), the swap is
/// recorded, and then the node pays the invoice. A node has at most its Max swaps in flight (at least 1)
/// Spark swaps open at once;</item>
/// <item>payment (<see cref="AdvanceSwapAsync"/>, MonitorSwapsJob): the payment is followed on the node
/// until it settles, which hands the swap to SparkSwapExitJob;</item>
/// <item>exit (<see cref="AttributeAsync"/> and <see cref="ExitAsync"/>, SparkSwapExitJob): the inbound
/// transfer that brought the payment in is found and its leaves are recorded on the swap. Exactly those
/// leaves exit on-chain to the swap's own destination address, with no leaf swap, so the remote signer can
/// sign it. The swap completes when that address receives a confirmed payout, which is what it delivered.</item>
/// </list>
/// Each step is saved before the next and can be retried after a crash: the swap is recorded before the
/// payment, the payment is looked up by its hash, a transfer is given to one swap only, and the payout is
/// found by its address (the SSP may fee-bump the exit, changing its txid).
/// </summary>
public interface ISparkSwapService
{
    /// <summary>Creates, records and pays a Spark swap; see <see cref="ISwapsService.CreateSwapOutAsync"/>.</summary>
    /// <exception cref="SparkUnavailableException">Spark is disabled or unavailable.</exception>
    /// <exception cref="InvalidOperationException">The node already has its Max swaps in flight of Spark swaps.</exception>
    Task<SwapOutCreation> CreateSwapOutAsync(Node node, SwapOut swapOut, SwapOutRequest request, CancellationToken ct = default);

    /// <summary>
    /// Follows a pending Spark swap's Lightning payment and returns its state. Once the payment settled, its fee
    /// is saved on <paramref name="swap"/> and the swap is SparkSwapExitJob's to exit.
    /// </summary>
    Task<SwapResponse> AdvanceSwapAsync(Node node, SwapOut swap, CancellationToken ct = default);

    /// <summary>
    /// Finds the inbound transfer that brought a paid swap's sats in and saves it, with its leaves, on the swap.
    /// Returns whether the swap has its transfer.
    /// </summary>
    Task<bool> AttributeAsync(SwapOut swap, CancellationToken ct = default);

    /// <summary>
    /// Moves a paid swap's exit one step forward: exits its leaves to its destination address, or completes it
    /// once that address has a confirmed payout. Progress is saved on <paramref name="swap"/>.
    /// </summary>
    Task<SparkExitStep> ExitAsync(SwapOut swap, CancellationToken ct = default);
}

public sealed class SparkSwapService : ISparkSwapService
{
    public const int InvoiceExpirySeconds = 1800;

    /// <summary>
    /// Spark's fixed fee for a Lightning payment into Spark, in basis points: 15 (0.15%), charged to the sender on the
    /// Lightning route through the invoice's route hints (https://docs.spark.money/wallets/estimate-fees). It is part of
    /// the swap's routing fee, so it is shown but never added to the swap's fees.
    /// </summary>
    public const int ServiceFeeBps = 15;
    private const int PaymentTimeoutSeconds = 120;
    private const decimal DefaultMaxRoutingFeePercent = 0.5m;
    private const string LightningTransferType = "PreimageSwap";
    /// <summary>The status of a transfer this wallet has claimed: its leaves are the wallet's.</summary>
    private const string ClaimedTransferStatus = "Completed";
    private const string ExitTransferType = "CooperativeExit";
    private const int MaxLeafSearchSteps = 100_000;
    private static readonly TimeSpan PaymentLookupTimeout = TimeSpan.FromSeconds(30);

    /// <summary>When nothing has arrived by then, the invoice has expired and the payment never will.</summary>
    private static readonly TimeSpan PaymentDeadline = TimeSpan.FromSeconds(InvoiceExpirySeconds) + TimeSpan.FromMinutes(10);

    /// <summary>Transfers are looked for from this long before the swap was created, for clock skew.</summary>
    private static readonly TimeSpan TransferLookback = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Serializes the in-flight check and the record of a swap, so concurrent creations can't exceed a node's
    /// limit (NodeGuard runs as one process).
    /// </summary>
    private static readonly SemaphoreSlim Reservation = new(1, 1);

    private readonly ILogger<SparkSwapService> _logger;
    private readonly SparkSettings _settings;
    private readonly ISparkWalletService _spark;
    private readonly ILightningService _lightningService;
    private readonly ISwapOutRepository _swapOutRepository;
    private readonly IWalletRepository _walletRepository;
    private readonly INBXplorerService _nbXplorerService;
    private readonly TimeProvider _time;

    public SparkSwapService(ILogger<SparkSwapService> logger, SparkSettings settings, ISparkWalletService spark,
        ILightningService lightningService, ISwapOutRepository swapOutRepository, IWalletRepository walletRepository,
        INBXplorerService nbXplorerService, TimeProvider time)
    {
        _logger = logger;
        _settings = settings;
        _spark = spark;
        _lightningService = lightningService;
        _swapOutRepository = swapOutRepository;
        _walletRepository = walletRepository;
        _nbXplorerService = nbXplorerService;
        _time = time;
    }

    private Network Network => _settings.IsMainnet ? Network.Main : Network.RegTest;

    public async Task<SwapOutCreation> CreateSwapOutAsync(Node node, SwapOut swapOut, SwapOutRequest request,
        CancellationToken ct = default)
    {
        if (request.Amount <= 0) throw new ArgumentException("The swap amount must be greater than zero.", nameof(request));

        // The payout address is reserved only when the swap exits, but the wallet it comes from must be usable now
        if (await DestinationStrategyAsync(swapOut) is null)
        {
            throw new ArgumentException("A Spark swap needs a destination wallet to exit to.", nameof(swapOut));
        }

        var status = await _spark.EnsureReadyAsync(ct);
        if (!status.IsReady) throw new SparkUnavailableException(status.Reason ?? "Spark is not available.");

        string paymentRequest;
        await Reservation.WaitAsync(ct);
        try
        {
            // Each swap exits its own leaves, so several can be in flight: up to the node's Max swaps in flight
            var limit = Math.Max(node.MaxSwapsInFlight, 1);
            var inFlight = (await _swapOutRepository.GetAllPending())
                .Count(s => s.Provider == SwapProvider.Spark && s.NodeId == node.Id);
            if (inFlight >= limit)
            {
                throw new InvalidOperationException(
                    $"{node.Name} already has {inFlight} Spark swaps in flight, its limit (Max swaps in flight: {limit}).");
            }

            var invoice = await _spark.CreateInvoiceAsync(request.Amount, $"NodeGuard swap-out from {node.Name}",
                InvoiceExpirySeconds, ct);
            paymentRequest = invoice.PaymentRequest;

            swapOut.Provider = SwapProvider.Spark;
            swapOut.ProviderId = invoice.RequestId ?? invoice.PaymentHash;
            swapOut.Status = SwapOutStatus.Pending;
            swapOut.SatsAmount = request.Amount;
            swapOut.PaymentHash = invoice.PaymentHash;
            swapOut.SparkIdentity = status.IdentityPublicKey;

            // Recorded before paying: from here on the monitor can resolve the swap whatever happens next
            var (saved, error) = await _swapOutRepository.AddAsync(swapOut);
            if (!saved)
            {
                return new SwapOutCreation(Response(swapOut), false, error);
            }
        }
        finally
        {
            Reservation.Release();
        }

        // Not cancellable: once the payment starts it must be followed to its end (or left to the monitor)
        var maxFeePercent = request.MaxRoutingFeesPercent ?? DefaultMaxRoutingFeePercent;
        var feeLimitMsat = (long)(request.Amount * 1000m * maxFeePercent / 100m);
        var payment = await _lightningService.SendPaymentV2Async(node, paymentRequest, 0, feeLimitMsat,
            request.ChannelsOut, null, PaymentTimeoutSeconds, CancellationToken.None);

        if (payment.Status == Payment.Types.PaymentStatus.Succeeded)
        {
            swapOut.LightningFeeSats = payment.FeeSat;
            _logger.LogInformation("Spark swap {SwapId}: {Node} paid {Amount} sats into Spark (routing fee {Fee} sats)",
                swapOut.Id, node.Name, request.Amount, payment.FeeSat);
        }
        else if (payment.Status == Payment.Types.PaymentStatus.Failed &&
                 payment.FailureReason != PaymentFailureReason.FailureReasonError)
        {
            swapOut.Status = SwapOutStatus.Failed;
            swapOut.ErrorDetails = $"Lightning payment failed: {payment.FailureReason}";
            _logger.LogWarning("Spark swap {SwapId}: the payment from {Node} failed: {Reason}",
                swapOut.Id, node.Name, payment.FailureReason);
        }
        else
        {
            // Unknown outcome (a stream error is reported as FailureReasonError): the monitor looks the payment up
            _logger.LogWarning("Spark swap {SwapId}: the payment from {Node} has no final outcome yet ({Status}, {Reason})",
                swapOut.Id, node.Name, payment.Status, payment.FailureReason);
        }

        Save(swapOut);
        return new SwapOutCreation(Response(swapOut), true, null);
    }

    public async Task<SwapResponse> AdvanceSwapAsync(Node node, SwapOut swap, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(swap.PaymentHash))
        {
            throw new InvalidOperationException($"Spark swap {swap.Id} has no payment hash.");
        }

        // Paid: the exit is SparkSwapExitJob's
        if (swap.LightningFeeSats is not null) return Response(swap);

        await EnsureSwapWalletAsync(swap, ct);

        var failure = await ResolvePaymentAsync(node, swap, ct);
        return failure is not null ? Response(swap, SwapOutStatus.Failed, failure) : Response(swap);
    }

    public async Task<bool> AttributeAsync(SwapOut swap, CancellationToken ct = default)
    {
        if (swap.SparkTransferId is not null) return true;
        RequirePaid(swap);
        var identity = await EnsureSwapWalletAsync(swap, ct);

        // Claimed first: only a claimed transfer's leaves are the wallet's to exit. One that could not be claimed
        // is not attributed, so the next run claims it again
        var claim = await _spark.ClaimPendingAsync(ct);
        foreach (var failure in claim.Failures)
        {
            _logger.LogWarning("Spark: transfer {TransferId} could not be claimed: {Error}", failure.TransferId, failure.Error.Message);
        }

        var attributed = await _swapOutRepository.GetSparkTransferIdsAsync();
        var reported = await ReportedTransferAsync(swap, identity, attributed, ct);
        if (reported is not null && !IsClaimed(reported))
        {
            // The swap's own transfer, known by id: wait for it rather than take another of the same amount
            _logger.LogWarning("Spark swap {SwapId}: transfer {TransferId} is not claimed yet ({Status})", swap.Id, reported.Id,
                reported.Status);
            return false;
        }

        var transfer = reported ?? await MatchingTransferAsync(swap, attributed, ct);
        if (transfer is null)
        {
            _logger.LogInformation("Spark swap {SwapId}: the transfer of its payment has not been found yet", swap.Id);
            return false;
        }

        swap.SparkTransferId = transfer.Id;
        swap.SparkLeafIds = string.Join(',', transfer.Leaves.Select(l => l.Id));
        swap.SparkReceivedSats = transfer.Leaves.Sum(l => l.ValueSats);
        if (!Save(swap))
        {
            swap.SparkTransferId = swap.SparkLeafIds = null;
            swap.SparkReceivedSats = null;
            return false;
        }

        _logger.LogInformation("Spark swap {SwapId}: transfer {TransferId} brought in {Received} sats in {LeafCount} leaves",
            swap.Id, transfer.Id, swap.SparkReceivedSats, transfer.Leaves.Count);
        return true;
    }

    public async Task<SparkExitStep> ExitAsync(SwapOut swap, CancellationToken ct = default)
    {
        RequirePaid(swap);
        await EnsureSwapWalletAsync(swap, ct);

        // Done once the destination address has a confirmed payout. Checked before exiting too: an exit whose
        // txid was not saved (a crash) must not be followed by a second one. No address yet means no exit yet
        var payout = swap.DestinationAddress is null ? null : await FindPayoutAsync(swap, ct);
        if (payout is not null)
        {
            if (payout.Confirmations > 0)
            {
                Complete(swap, payout);
                return SparkExitStep.Completed;
            }

            if (swap.TxId != payout.TransactionId.ToString())
            {
                swap.TxId = payout.TransactionId.ToString();
                Save(swap);
            }

            return SparkExitStep.ExitSent;
        }

        if (swap.TxId is not null) return SparkExitStep.ExitSent;
        if (!await AttributeAsync(swap, ct)) return SparkExitStep.Waiting;

        // The address is reserved only now, and saved before the exit: the payout is found by it after a crash
        if (swap.DestinationAddress is null)
        {
            swap.DestinationAddress = await ReserveAddressAsync(swap, ct);
            if (!Save(swap))
            {
                swap.DestinationAddress = null;
                return SparkExitStep.Waiting;
            }

            _logger.LogInformation("Spark swap {SwapId}: exits to {Address}", swap.Id, swap.DestinationAddress);
        }

        var leafIds = LeafIds(swap);
        WithdrawLeavesResult exit;
        try
        {
            exit = await _spark.WithdrawLeavesAsync(leafIds, swap.DestinationAddress!, _settings.MaxExitFeeSats, ct);
        }
        catch (SparkLeavesNotSpendableException e)
        {
            // Either they already exited (a crash before the txid was saved) or they were renewed into new leaf ids
            if (await AlreadyExitedAsync(swap, leafIds, ct))
            {
                _logger.LogWarning("Spark swap {SwapId}: its leaves already exited; waiting for the payout", swap.Id);
                return SparkExitStep.ExitSent;
            }

            var replacement = await ReplacementLeavesAsync(swap, ct);
            if (replacement is null)
            {
                _logger.LogWarning("Spark swap {SwapId}: {Error} No other leaves add up to its {Received} sats yet",
                    swap.Id, e.Message, swap.SparkReceivedSats);
                return SparkExitStep.Waiting;
            }

            swap.SparkLeafIds = string.Join(',', replacement);
            if (!Save(swap)) return SparkExitStep.Waiting;

            _logger.LogInformation("Spark swap {SwapId}: its leaves were renewed; exiting {LeafCount} leaves of the same value",
                swap.Id, replacement.Count);
            exit = await _spark.WithdrawLeavesAsync(replacement, swap.DestinationAddress!, _settings.MaxExitFeeSats, ct);
        }

        swap.TxId = exit.Txid;
        SetFees(swap, exit.PayoutSats); // provisional until the payout confirms
        Save(swap);

        _logger.LogInformation("Spark swap {SwapId}: cooperative exit {TxId} pays {Payout} of its {Received} sats to {Address} (exit fee {Fee} sats)",
            swap.Id, exit.Txid, exit.PayoutSats, exit.SentSats, swap.DestinationAddress, exit.FeeSats);
        return SparkExitStep.ExitSent;
    }

    /// <summary>
    /// Looks the payment up on the node. Saves its fee once it succeeded; returns why the swap failed,
    /// or null while it may still succeed.
    /// </summary>
    private async Task<string?> ResolvePaymentAsync(Node node, SwapOut swap, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(PaymentLookupTimeout);
        var payment = await _lightningService.TrackPaymentV2Async(node, Convert.FromHexString(swap.PaymentHash!), timeout.Token);

        switch (payment?.Status)
        {
            case Payment.Types.PaymentStatus.Succeeded:
                swap.LightningFeeSats = payment.FeeSat;
                Save(swap);
                return null;

            case Payment.Types.PaymentStatus.Failed:
                return $"Lightning payment failed: {payment.FailureReason}";

            case null when _time.GetUtcNow() - swap.CreationDatetime > PaymentDeadline:
                // The node has no record of the payment and the invoice expired: no payment will arrive. The SSP's
                // view is only a hint (not every SSP serves it), so a failed lookup does not block this
                var receive = await ReceiveStatusAsync(swap);
                return $"The Lightning payment never reached the node and the invoice expired (SSP receive status: {receive ?? "unknown"})";

            default:
                return null;
        }
    }

    private async Task<string?> ReceiveStatusAsync(SwapOut swap)
    {
        try
        {
            return await _spark.GetReceiveStatusAsync(swap.ProviderId!);
        }
        catch (Exception e)
        {
            _logger.LogWarning("Spark swap {SwapId}: the SSP did not report the receive status: {Error}", swap.Id, e.Message);
            return null;
        }
    }

    /// <summary>The transfer the SSP reports for the swap's receive request, if it reports one.</summary>
    private async Task<SparkTransfer?> ReportedTransferAsync(SwapOut swap, string identity, HashSet<string> attributed,
        CancellationToken ct)
    {
        // Without a receive request (an SSP that returned none), the payment hash stands in as the provider id
        if (string.IsNullOrEmpty(swap.ProviderId) || swap.ProviderId == swap.PaymentHash) return null;

        LightningReceiveRequest? request;
        try
        {
            request = await _spark.GetReceiveRequestAsync(swap.ProviderId, ct);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            _logger.LogWarning("Spark swap {SwapId}: the SSP did not report its receive request: {Error}", swap.Id, e.Message);
            return null;
        }

        if (request?.TransferId is not { } transferId) return null;
        if (attributed.Contains(transferId))
        {
            _logger.LogError("Spark swap {SwapId}: the SSP reports transfer {TransferId}, which another swap already has",
                swap.Id, transferId);
            return null;
        }

        var transfer = await _spark.GetTransferAsync(transferId, ct);
        if (transfer is null || transfer.Leaves.Count == 0 ||
            !string.Equals(transfer.ReceiverIdentityPublicKey, identity, StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogWarning("Spark swap {SwapId}: transfer {TransferId} is not one this wallet received, with leaves",
                swap.Id, transferId);
            return null;
        }

        return transfer;
    }

    /// <summary>
    /// For an SSP that does not report the transfer: the claimed Lightning transfer received since the swap was
    /// created, given to no swap yet, closest to the swap's amount without exceeding it (any receive fee within 1%).
    /// Transfers of equal amounts are interchangeable, as their leaves add up the same.
    /// </summary>
    private async Task<SparkTransfer?> MatchingTransferAsync(SwapOut swap, HashSet<string> attributed, CancellationToken ct)
    {
        var received = await _spark.GetTransfersAsync(TransferDirection.Received, swap.CreationDatetime - TransferLookback, ct);
        var minimum = swap.SatsAmount - swap.SatsAmount / 100;

        return received
            .Where(t => string.Equals(t.Type, LightningTransferType, StringComparison.OrdinalIgnoreCase))
            .Where(t => !attributed.Contains(t.Id) && t.Leaves.Count > 0 && IsClaimed(t))
            .Where(t => t.TotalValueSats <= swap.SatsAmount && t.TotalValueSats >= minimum)
            .OrderBy(t => swap.SatsAmount - t.TotalValueSats)
            .ThenBy(t => t.CreatedAt)
            .FirstOrDefault();
    }

    /// <summary>Whether a cooperative exit the wallet sent since the swap was created moved any of its leaves.</summary>
    private async Task<bool> AlreadyExitedAsync(SwapOut swap, IReadOnlyCollection<string> leafIds, CancellationToken ct)
    {
        var sent = await _spark.GetTransfersAsync(TransferDirection.Sent, swap.CreationDatetime - TransferLookback, ct);
        return sent
            .Where(t => string.Equals(t.Type, ExitTransferType, StringComparison.OrdinalIgnoreCase))
            .Any(t => t.Leaves.Any(l => leafIds.Contains(l.Id, StringComparer.OrdinalIgnoreCase)));
    }

    /// <summary>
    /// Spendable leaves no other pending swap has, adding up to exactly what the swap received: its own leaves
    /// under their renewed ids.
    /// </summary>
    private async Task<IReadOnlyList<string>?> ReplacementLeavesAsync(SwapOut swap, CancellationToken ct)
    {
        var taken = (await _swapOutRepository.GetAllPending())
            .Where(s => s.Provider == SwapProvider.Spark && s.Id != swap.Id)
            .SelectMany(LeafIds)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var free = (await _spark.GetSpendableLeavesAsync(ct)).Where(l => !taken.Contains(l.Id)).ToList();

        return ExactLeafSet(free, swap.SparkReceivedSats!.Value);
    }

    /// <summary>Leaves adding up to exactly <paramref name="targetSats"/>, largest first; null if there are none.</summary>
    internal static IReadOnlyList<string>? ExactLeafSet(IReadOnlyList<SparkLeaf> leaves, long targetSats)
    {
        var sorted = leaves.Where(l => l.ValueSats > 0 && l.ValueSats <= targetSats).OrderByDescending(l => l.ValueSats).ToList();
        var remaining = new long[sorted.Count + 1];
        for (var i = sorted.Count - 1; i >= 0; i--) remaining[i] = remaining[i + 1] + sorted[i].ValueSats;

        var chosen = new List<string>();
        var steps = 0;

        bool Search(int index, long left)
        {
            if (left == 0) return true;
            if (index == sorted.Count || remaining[index] < left || ++steps > MaxLeafSearchSteps) return false;

            if (sorted[index].ValueSats <= left)
            {
                chosen.Add(sorted[index].Id);
                if (Search(index + 1, left - sorted[index].ValueSats)) return true;
                chosen.RemoveAt(chosen.Count - 1);
            }

            return Search(index + 1, left);
        }

        return targetSats > 0 && Search(0, targetSats) ? chosen : null;
    }

    private void Complete(SwapOut swap, Payout payout)
    {
        swap.Status = SwapOutStatus.Completed;
        swap.TxId = payout.TransactionId.ToString();
        swap.PayoutSats = payout.Value;
        SetFees(swap, payout.Value);
        Save(swap);

        _logger.LogInformation("Spark swap {SwapId}: completed, {Payout} sats confirmed at {Address} in {TxId} (on-chain fee {OnChainFee} sats)",
            swap.Id, payout.Value, swap.DestinationAddress, swap.TxId, swap.OnChainFeeSats);
    }

    /// <summary>
    /// A Spark swap's fees from what it delivered on-chain. Spark's own fee is charged on the Lightning route, so it is
    /// part of the routing fee (<see cref="SwapOut.LightningFeeSats"/>). The on-chain fee is the move from Spark to L1:
    /// the SSP's exit fee and the L1 broadcast, what the swap's leaves held minus the payout. The service fee is only
    /// what the payment brought in short of the amount, which receiving into Spark normally leaves at 0.
    /// </summary>
    private static void SetFees(SwapOut swap, long deliveredSats)
    {
        var received = swap.SparkReceivedSats ?? swap.SatsAmount;
        swap.ServiceFeeSats = Math.Max(0, swap.SatsAmount - received);
        swap.OnChainFeeSats = Math.Max(0, received - deliveredSats);
    }

    private sealed record Payout(uint256 TransactionId, long Value, long Confirmations);

    /// <summary>The destination wallet's transaction paying the swap's destination address, if any.</summary>
    private async Task<Payout?> FindPayoutAsync(SwapOut swap, CancellationToken ct)
    {
        var strategy = await DestinationStrategyAsync(swap) ??
                       throw new InvalidOperationException($"Spark swap {swap.Id} has no destination wallet to find its payout in.");

        ct.ThrowIfCancellationRequested();
        var script = BitcoinAddress.Create(swap.DestinationAddress!, Network).ScriptPubKey;
        var transactions = await _nbXplorerService.GetTransactionsAsync(strategy);

        return transactions.ConfirmedTransactions.Transactions
            .Concat(transactions.UnconfirmedTransactions.Transactions)
            .SelectMany(tx => tx.Outputs
                .Where(output => output.ScriptPubKey == script)
                .Select(output => new Payout(tx.TransactionId, ((Money)output.Value).Satoshi, tx.Confirmations)))
            .OrderByDescending(p => p.Confirmations)
            .FirstOrDefault();
    }

    /// <summary>A new address of the destination wallet, reserved so that nothing else is given it.</summary>
    private async Task<string> ReserveAddressAsync(SwapOut swap, CancellationToken ct)
    {
        var strategy = await DestinationStrategyAsync(swap) ??
                       throw new InvalidOperationException($"Spark swap {swap.Id} has no destination wallet to exit to.");
        var address = await _nbXplorerService.GetUnusedAsync(strategy, DerivationFeature.Deposit, 0, true, ct);

        return address?.Address?.ToString() ??
               throw new InvalidOperationException($"Spark swap {swap.Id}: no address of its destination wallet could be reserved.");
    }

    /// <summary>The swap's destination wallet's derivation strategy, or null if it has none.</summary>
    private async Task<DerivationStrategyBase?> DestinationStrategyAsync(SwapOut swap)
    {
        var wallet = swap.DestinationWalletId is { } walletId ? await _walletRepository.GetById(walletId) : null;
        return wallet?.GetDerivationStrategy();
    }

    /// <summary>Connects the wallet and checks it is the one the swap was paid into; returns its identity.</summary>
    private async Task<string> EnsureSwapWalletAsync(SwapOut swap, CancellationToken ct)
    {
        var status = await _spark.EnsureReadyAsync(ct);
        if (!status.IsReady) throw new SparkUnavailableException(status.Reason ?? "Spark is not available.");
        if (swap.SparkIdentity is { } identity && !string.Equals(identity, status.IdentityPublicKey, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"Spark swap {swap.Id} was paid into Spark identity {identity}, but NodeGuard's Spark wallet is now " +
                $"{status.IdentityPublicKey}: it must be resolved by hand with the seed of {identity}.");
        }

        return status.IdentityPublicKey!;
    }

    private static void RequirePaid(SwapOut swap)
    {
        if (swap.LightningFeeSats is null)
        {
            throw new InvalidOperationException($"Spark swap {swap.Id} is not paid yet.");
        }
    }

    private static IReadOnlyList<string> LeafIds(SwapOut swap) =>
        (swap.SparkLeafIds ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static bool IsClaimed(SparkTransfer transfer) =>
        string.Equals(transfer.Status, ClaimedTransferStatus, StringComparison.OrdinalIgnoreCase);

    /// <summary>Saves the swap's progress; returns whether it was saved.</summary>
    private bool Save(SwapOut swap)
    {
        var (saved, error) = _swapOutRepository.Update(swap);
        if (!saved)
        {
            // Not fatal: every step can be found again (the payment by its hash, the transfer from the SSP, the payout by its address)
            _logger.LogError("Spark swap {SwapId}: could not save its progress: {Error}", swap.Id, error);
        }

        return saved;
    }

    private static SwapResponse Response(SwapOut swap, SwapOutStatus? status = null, string? error = null) => new()
    {
        Id = swap.ProviderId!,
        HtlcAddress = string.Empty,
        PaymentHash = swap.PaymentHash,
        Amount = swap.SatsAmount,
        // Spark's fee is charged on the Lightning route; the on-chain fee is the exit from Spark to L1
        OffchainFee = swap.LightningFeeSats ?? 0,
        ServerFee = swap.ServiceFeeSats ?? 0,
        OnchainFee = swap.OnChainFeeSats ?? 0,
        Status = status ?? swap.Status,
        TxId = swap.TxId,
        ErrorMessage = error ?? swap.ErrorDetails
    };
}
