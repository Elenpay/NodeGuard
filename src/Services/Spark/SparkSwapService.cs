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
using NodeGuard.Jobs;
using NSpark;
using NSpark.Exceptions;
using NSpark.Models;
using NSpark.Services;
using Quartz;

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
/// <item>creation: the node's Spark wallet issues a BOLT11 invoice (through the SSP), the swap is
/// recorded, and then the node pays the invoice. A node has at most its Max swaps in flight (at least 1)
/// Spark swaps open at once;</item>
/// <item>payment (<see cref="AdvanceSwapAsync"/>, MonitorSwapsJob): the payment is followed on the node
/// until it settles, which hands the swap to SparkSwapExitJob;</item>
/// <item>exit (<see cref="AttributeAsync"/>, <see cref="PlanExitsAsync"/> and <see cref="ExitBatchAsync"/>,
/// SparkSwapExitJob): the inbound transfer that brought the payment in is found and its leaves are recorded on the
/// swap. Paid swaps wait in their Spark wallet until it is full, then exit together: one cooperative exit per
/// destination wallet, of exactly their leaves, with no leaf swap, so the remote signer can sign it. Each swap
/// completes when the batch's address receives a confirmed payout, its share of which is what it delivered.</item>
/// </list>
/// Each step is saved before the next and can be retried after a crash: the swap is recorded before the
/// payment, the payment is looked up by its hash, a transfer is given to one swap only, and the payout is
/// found by its address (the SSP may fee-bump the exit, changing its txid). Every step after creation works on
/// the wallet the swap was paid into, whichever the node uses now.
/// </summary>
public interface ISparkSwapService
{
    /// <summary>
    /// Creates and records a Spark swap, then starts its payment in the background without waiting for it: the swap
    /// is returned pending. See <see cref="ISwapsService.CreateSwapOutAsync"/>.
    /// </summary>
    /// <exception cref="SparkUnavailableException">Spark is disabled or unavailable.</exception>
    /// <exception cref="InvalidOperationException">
    /// The node has no Spark wallet or already has its Max swaps in flight of Spark swaps, or its wallet has no room.
    /// </exception>
    Task<SwapOutCreation> CreateSwapOutAsync(Node node, SwapOut swapOut, SwapOutRequest request, CancellationToken ct = default);

    /// <summary>
    /// Pays a retry of a swap's payment, scheduled after its previous attempt failed (SparkSwapPaymentJob): only
    /// while the swap still waits for that invoice (<paramref name="paymentHash"/>) and the node has no record of
    /// its payment yet, so a job run again after a restart never pays twice.
    /// </summary>
    Task PayScheduledAsync(int swapId, string paymentHash, string paymentRequest, decimal maxFeePercent,
        ulong[]? channelsOut, int timeoutSeconds, CancellationToken ct = default);

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
    /// The batches to exit now, oldest first, out of the attributed swaps not exiting yet among
    /// <paramref name="pendingSwaps"/> (all pending Spark swaps): those of each Spark wallet that is full (no node
    /// using it can add a swap under its max balance) and none of whose swaps is still being paid, one batch per
    /// destination wallet, of at most <see cref="SparkSwapService.MaxLeavesPerExit"/> leaves.
    /// </summary>
    Task<IReadOnlyList<IReadOnlyList<SwapOut>>> PlanExitsAsync(IReadOnlyCollection<SwapOut> pendingSwaps, CancellationToken ct = default);

    /// <summary>
    /// Moves a batch's exit one step forward: exits its swaps' leaves together to one new address of their
    /// destination wallet, or completes them once that address has a confirmed payout, shared among them in
    /// proportion to what each brought in. A batch is either new (from <see cref="PlanExitsAsync"/>) or the swaps
    /// already sharing a destination address. Progress is saved on its swaps.
    /// </summary>
    Task<SparkExitStep> ExitBatchAsync(IReadOnlyList<SwapOut> batch, CancellationToken ct = default);
}

public sealed class SparkSwapService : ISparkSwapService
{
    public const int InvoiceExpirySeconds = 1800;

    /// <summary>
    /// Most leaves one cooperative exit carries. open-ssp refuses more than 100 (the operators take up to 1000); its fee
    /// doesn't grow with them, so a batch packs as many swaps as fit.
    /// </summary>
    public const int MaxLeavesPerExit = 100;

    /// <summary>
    /// Spark's fixed fee for a Lightning payment into Spark, in basis points: 15 (0.15%), charged to the sender on the
    /// Lightning route through the invoice's route hints (https://docs.spark.money/wallets/estimate-fees). It is part of
    /// the swap's routing fee, so it is shown but never added to the swap's fees.
    /// </summary>
    public const int ServiceFeeBps = 15;
    private const decimal DefaultMaxRoutingFeePercent = 0.5m;
    /// <summary>The status of a transfer this wallet has claimed: its leaves are the wallet's.</summary>
    private const string ClaimedTransferStatus = "Completed";
    private const string ExitTransferType = "CooperativeExit";
    private static readonly TimeSpan PaymentLookupTimeout = TimeSpan.FromSeconds(30);

    /// <summary>
    /// When nothing has arrived by then, the swap's current invoice has expired and its payment never will: 10
    /// minutes after it expires (30 minutes after creation for swaps recorded without its expiry).
    /// </summary>
    private static DateTimeOffset PaymentDeadlineOf(SwapOut swap) =>
        (swap.SparkInvoiceExpiresAt ?? swap.CreationDatetime + TimeSpan.FromSeconds(InvoiceExpirySeconds)) + TimeSpan.FromMinutes(10);

    /// <summary>Transfers are looked for from this long before the swap was created, for clock skew.</summary>
    private static readonly TimeSpan TransferLookback = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Serializes the in-flight check and the record of a swap, so concurrent creations can't exceed a node's
    /// limit (NodeGuard runs as one process), and the Spark wallets' withdraw-all against them.
    /// </summary>
    internal static readonly SemaphoreSlim Reservation = new(1, 1);

    private readonly ILogger<SparkSwapService> _logger;
    private readonly SparkSettings _settings;
    private readonly ISparkWalletService _spark;
    private readonly ILightningService _lightningService;
    private readonly ISwapOutRepository _swapOutRepository;
    private readonly IWalletRepository _walletRepository;
    private readonly INBXplorerService _nbXplorerService;
    private readonly INodeRepository _nodeRepository;
    private readonly ISchedulerFactory _schedulerFactory;
    private readonly IAuditService _auditService;
    private readonly TimeProvider _time;

    public SparkSwapService(ILogger<SparkSwapService> logger, SparkSettings settings, ISparkWalletService spark,
        ILightningService lightningService, ISwapOutRepository swapOutRepository, IWalletRepository walletRepository,
        INBXplorerService nbXplorerService, INodeRepository nodeRepository, ISchedulerFactory schedulerFactory,
        IAuditService auditService, TimeProvider time)
    {
        _logger = logger;
        _settings = settings;
        _spark = spark;
        _lightningService = lightningService;
        _swapOutRepository = swapOutRepository;
        _walletRepository = walletRepository;
        _nbXplorerService = nbXplorerService;
        _nodeRepository = nodeRepository;
        _schedulerFactory = schedulerFactory;
        _auditService = auditService;
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

        if (_settings.WalletFor(node) is not { } sparkWallet)
        {
            throw new InvalidOperationException(
                $"Node {node.Name} has no Spark wallet: choose one in its liquidity settings on the Nodes page.");
        }

        var entry = await _spark.GetWalletAsync(sparkWallet, ct);
        if (entry is null || entry.IsArchived)
        {
            throw new InvalidOperationException($"Node {node.Name}'s {sparkWallet} is archived or not a Spark wallet.");
        }

        var status = await _spark.EnsureReadyAsync(sparkWallet, ct);
        if (!status.IsReady) throw new SparkUnavailableException(status.Reason ?? $"{entry.Name} is not available.");

        string paymentRequest;
        await Reservation.WaitAsync(ct);
        try
        {
            // Each swap exits its own leaves, so several can be in flight: up to the node's Max swaps in flight
            var limit = Math.Max(node.MaxSwapsInFlight, 1);
            var pending = await _swapOutRepository.GetAllPending();
            // Once paid, a swap waits in the wallet, whose max balance limits it: only swaps being paid are in flight
            var inFlight = pending.Count(s => s.Provider == SwapProvider.Spark && s.NodeId == node.Id && s.LightningFeeSats is null);
            if (inFlight >= limit)
            {
                throw new InvalidOperationException(
                    $"{node.Name} already has {inFlight} Spark swaps in flight, its limit (Max swaps in flight: {limit}).");
            }

            // Transit only: refuse a swap that would leave more than the wallet's max balance in it, counting what
            // its swaps not paid yet will still bring in
            var balance = (await _spark.GetBalanceAsync(sparkWallet, ct)).SatsBalance;
            var room = SparkGuardrails.RoomSats(entry.MaxBalanceSats, balance, pending.Where(s =>
                string.Equals(s.SparkIdentity, status.IdentityPublicKey, StringComparison.OrdinalIgnoreCase)));
            var held = entry.MaxBalanceSats - room;
            if (request.Amount > room)
            {
                throw new InvalidOperationException(
                    $"A {request.Amount} sats Spark swap would bring {entry.Name} to {held + request.Amount} sats, " +
                    $"over its max balance ({entry.MaxBalanceSats} sats).");
            }

            var invoice = await _spark.CreateInvoiceAsync(sparkWallet, request.Amount, $"NodeGuard swap-out from {node.Name}",
                InvoiceExpirySeconds, ct);
            paymentRequest = invoice.PaymentRequest;

            // The swap's funds are found only through the transfer the SSP reports for its receive request:
            // without one, nothing could ever be exited, so nothing is paid
            if (string.IsNullOrEmpty(invoice.RequestId))
            {
                throw new InvalidOperationException("The SSP issued the invoice without a receive request, so its payment could not be followed.");
            }

            swapOut.Provider = SwapProvider.Spark;
            swapOut.ProviderId = invoice.RequestId;
            swapOut.Status = SwapOutStatus.Pending;
            swapOut.SatsAmount = request.Amount;
            swapOut.PaymentHash = invoice.PaymentHash;
            swapOut.SparkPaymentAttempt = 1;
            swapOut.SparkInvoiceExpiresAt = invoice.ExpiresAt;
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

        // Paid in the background, for up to the node's payment timeout: the swap shows as pending meanwhile. Off
        // the caller's synchronization context, so the payment doesn't depend on the page that created the swap
        var maxFeePercent = request.MaxRoutingFeesPercent ?? DefaultMaxRoutingFeePercent;
        _ = Task.Run(() => PayAsync(node, swapOut.Id, paymentRequest, request.Amount, maxFeePercent, request.ChannelsOut,
            _settings.PaymentTimeoutFor(node) * 60));
        _logger.LogInformation("Spark swap {SwapId}: {Node} is paying {Amount} sats into Spark", swapOut.Id, node.Name, request.Amount);

        return new SwapOutCreation(Response(swapOut), true, null);
    }

    /// <summary>
    /// Pays a recorded swap's invoice of <paramref name="amountSats"/>, with a fee limit of
    /// <paramref name="maxFeePercent"/> of it, and saves the outcome: the routing fee once paid; once it failed for
    /// want of a route or liquidity, a smaller retry (<see cref="RetrySmallerAsync"/>), else the failure. An unknown
    /// outcome, or an error here, is left to MonitorSwapsJob, which finds the payment by its hash. Not cancellable:
    /// once the payment starts it is followed to its end.
    /// </summary>
    internal async Task PayAsync(Node node, int swapId, string paymentRequest, long amountSats, decimal maxFeePercent,
        ulong[]? channelsOut, int timeoutSeconds)
    {
        try
        {
            var result = await _lightningService.SendPaymentV2Async(node, paymentRequest, 0,
                FeeLimitMsat(amountSats, maxFeePercent), channelsOut, null, timeoutSeconds, CancellationToken.None);
            await SavePaymentAsync(node, swapId, result, maxFeePercent, channelsOut, timeoutSeconds);
        }
        catch (Exception e)
        {
            _logger.LogError(e, "Spark swap {SwapId}: its payment could not be followed; MonitorSwapsJob takes over", swapId);
        }
    }

    private static long FeeLimitMsat(long amountSats, decimal maxFeePercent) => (long)(amountSats * 1000m * maxFeePercent / 100m);

    private async Task SavePaymentAsync(Node node, int swapId, Payment result, decimal maxFeePercent, ulong[]? channelsOut,
        int timeoutSeconds)
    {
        // Read again once the payment ended: the monitor may have resolved the swap meanwhile
        if (await _swapOutRepository.GetById(swapId) is not { Status: SwapOutStatus.Pending, LightningFeeSats: null } swap)
        {
            _logger.LogInformation("Spark swap {SwapId}: its payment ended ({Status}) after the swap was already resolved",
                swapId, result.Status);
            return;
        }

        if (result.Status == Payment.Types.PaymentStatus.Succeeded)
        {
            swap.LightningFeeSats = result.FeeSat;
            _logger.LogInformation("Spark swap {SwapId}: {Node} paid {Amount} sats into Spark (routing fee {Fee} sats)",
                swap.Id, node.Name, swap.SatsAmount, result.FeeSat);
        }
        else if (result.Status == Payment.Types.PaymentStatus.Failed &&
                 result.FailureReason != PaymentFailureReason.FailureReasonError)
        {
            if (RetriedFailures.Contains(result.FailureReason) &&
                await RetrySmallerAsync(node, swap, result.FailureReason, maxFeePercent, channelsOut, timeoutSeconds))
            {
                return;
            }

            swap.Status = SwapOutStatus.Failed;
            swap.ErrorDetails = swap.SparkPaymentAttempt > 1
                ? $"Lightning payment failed: {result.FailureReason} (attempt {swap.SparkPaymentAttempt}, {swap.SatsAmount} sats)"
                : $"Lightning payment failed: {result.FailureReason}";
            _logger.LogWarning("Spark swap {SwapId}: the payment from {Node} failed on attempt {Attempt}: {Reason}",
                swap.Id, node.Name, swap.SparkPaymentAttempt, result.FailureReason);
        }
        else
        {
            // Unknown outcome (a stream error is reported as FailureReasonError): the monitor looks the payment up
            _logger.LogWarning("Spark swap {SwapId}: the payment from {Node} has no final outcome yet ({Status}, {Reason})",
                swap.Id, node.Name, result.Status, result.FailureReason);
            return;
        }

        Save(swap);
    }

    /// <summary>Failures a smaller amount may get past: no route, a timeout, or too little outbound liquidity.</summary>
    private static readonly HashSet<PaymentFailureReason> RetriedFailures =
    [
        PaymentFailureReason.FailureReasonNoRoute,
        PaymentFailureReason.FailureReasonTimeout,
        PaymentFailureReason.FailureReasonInsufficientBalance
    ];

    /// <summary>
    /// Retries a failed payment with a smaller amount (SEND_PAYMENT_AMOUNT_BACKOFF_RATIO, down to the node's Spark
    /// min swap, up to SEND_PAYMENT_MAX_ATTEMPTS attempts): issues a fresh invoice for it right away and saves it on
    /// the swap, so the swap never points at a failed payment and the monitor leaves it be, then schedules its
    /// payment after the backoff delay (SparkSwapPaymentJob). False when no retry is left, or the new invoice could
    /// not be issued or saved.
    /// </summary>
    private async Task<bool> RetrySmallerAsync(Node node, SwapOut swap, PaymentFailureReason reason, decimal maxFeePercent,
        ulong[]? channelsOut, int timeoutSeconds)
    {
        var attempt = swap.SparkPaymentAttempt + 1;
        var amount = (long)Math.Floor(swap.SatsAmount * (decimal)Constants.SEND_PAYMENT_AMOUNT_BACKOFF_RATIO);
        var minSwap = SwapAmountLimits.For(node, SwapProvider.Spark, _settings).MinSats;
        if (attempt > Constants.SEND_PAYMENT_MAX_ATTEMPTS || amount < minSwap || amount >= swap.SatsAmount)
        {
            _logger.LogInformation("Spark swap {SwapId}: no retry left after attempt {Attempt} ({Amount} sats; next {Next}, min {Min})",
                swap.Id, swap.SparkPaymentAttempt, swap.SatsAmount, amount, minSwap);
            return false;
        }

        LightningInvoice invoice;
        try
        {
            var wallet = await SwapWalletAsync(swap, CancellationToken.None);
            invoice = await _spark.CreateInvoiceAsync(wallet.Ref, amount, $"NodeGuard swap-out from {node.Name}",
                InvoiceExpirySeconds, CancellationToken.None);
        }
        catch (Exception e)
        {
            _logger.LogWarning(e, "Spark swap {SwapId}: could not issue the invoice for its retry", swap.Id);
            return false;
        }

        if (string.IsNullOrEmpty(invoice.RequestId))
        {
            _logger.LogWarning("Spark swap {SwapId}: the SSP issued the retry's invoice without a receive request", swap.Id);
            return false;
        }

        var previous = (swap.ProviderId, swap.PaymentHash, swap.SatsAmount, swap.SparkPaymentAttempt, swap.SparkInvoiceExpiresAt);
        swap.ProviderId = invoice.RequestId;
        swap.PaymentHash = invoice.PaymentHash;
        swap.SatsAmount = amount;
        swap.SparkPaymentAttempt = attempt;
        swap.SparkInvoiceExpiresAt = invoice.ExpiresAt;
        if (!Save(swap))
        {
            (swap.ProviderId, swap.PaymentHash, swap.SatsAmount, swap.SparkPaymentAttempt, swap.SparkInvoiceExpiresAt) = previous;
            return false;
        }

        var delaySeconds = Constants.SEND_PAYMENT_INITIAL_RETRY_DELAY_SECONDS *
                           Math.Pow(Constants.SEND_PAYMENT_RETRY_BACKOFF_MULTIPLIER, attempt - 2);
        var fireAt = _time.GetUtcNow().AddSeconds(delaySeconds);
        await SparkSwapPaymentJob.ScheduleAsync(_schedulerFactory, fireAt, swap.Id, attempt, invoice.PaymentHash,
            invoice.PaymentRequest, maxFeePercent, channelsOut, timeoutSeconds);

        _logger.LogInformation("Spark swap {SwapId}: the payment from {Node} failed ({Reason}); attempt {Attempt} pays {Amount} sats at {FireAt:O}",
            swap.Id, node.Name, reason, attempt, amount, fireAt);
        await _auditService.LogSystemAsync(AuditActionType.SwapOutPaymentRetryScheduled, AuditEventType.Attempt,
            AuditObjectType.SwapOut, swap.Id.ToString(),
            new { Reason = reason.ToString(), Attempt = attempt, AmountSats = amount, previous.SatsAmount, FireAt = fireAt });
        return true;
    }

    public async Task PayScheduledAsync(int swapId, string paymentHash, string paymentRequest, decimal maxFeePercent,
        ulong[]? channelsOut, int timeoutSeconds, CancellationToken ct = default)
    {
        if (await _swapOutRepository.GetById(swapId) is not { Status: SwapOutStatus.Pending, LightningFeeSats: null } swap ||
            swap.PaymentHash != paymentHash)
        {
            _logger.LogInformation("Spark swap {SwapId}: no longer waits for the payment of {PaymentHash}, which is not sent",
                swapId, paymentHash);
            return;
        }

        if (swap.NodeId is not { } nodeId || await _nodeRepository.GetById(nodeId) is not { } node)
        {
            _logger.LogError("Spark swap {SwapId}: its node {NodeId} is gone, so its retry is not paid", swapId, swap.NodeId);
            return;
        }

        // Run again after a restart: what the node already knows of is followed by the monitor instead
        using (var lookup = CancellationTokenSource.CreateLinkedTokenSource(ct))
        {
            lookup.CancelAfter(PaymentLookupTimeout);
            if (await _lightningService.TrackPaymentV2Async(node, Convert.FromHexString(paymentHash), lookup.Token) is { } known)
            {
                _logger.LogInformation("Spark swap {SwapId}: the node already has the payment of attempt {Attempt} ({Status})",
                    swapId, swap.SparkPaymentAttempt, known.Status);
                return;
            }
        }

        await PayAsync(node, swapId, paymentRequest, swap.SatsAmount, maxFeePercent, channelsOut, timeoutSeconds);
    }

    public async Task<SwapResponse> AdvanceSwapAsync(Node node, SwapOut swap, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(swap.PaymentHash))
        {
            throw new InvalidOperationException($"Spark swap {swap.Id} has no payment hash.");
        }

        // Paid: the exit is SparkSwapExitJob's
        if (swap.LightningFeeSats is not null) return Response(swap);

        var wallet = await SwapWalletAsync(swap, ct);

        var failure = await ResolvePaymentAsync(node, swap, wallet.Ref, ct);
        return failure is not null ? Response(swap, SwapOutStatus.Failed, failure) : Response(swap);
    }

    public async Task<bool> AttributeAsync(SwapOut swap, CancellationToken ct = default)
    {
        if (swap.SparkTransferId is not null) return true;
        RequirePaid(swap);
        var wallet = await SwapWalletAsync(swap, ct);

        // Claimed first: only a claimed transfer's leaves are the wallet's to exit. One that could not be claimed
        // is not attributed, so the next run claims it again
        var claim = await _spark.ClaimPendingAsync(wallet.Ref, ct);
        foreach (var failure in claim.Failures)
        {
            _logger.LogWarning("{Wallet}: transfer {TransferId} could not be claimed: {Error}", wallet.Name, failure.TransferId,
                failure.Error.Message);
        }

        // Only the swap's own transfer, the one the SSP reports for its receive request: never another of the same amount
        var attributed = await _swapOutRepository.GetSparkTransferIdsAsync();
        var transfer = await ReportedTransferAsync(swap, wallet.Ref, attributed, ct);
        if (transfer is null)
        {
            _logger.LogInformation("Spark swap {SwapId}: the transfer of its payment has not been found yet", swap.Id);
            return false;
        }

        if (!IsClaimed(transfer))
        {
            _logger.LogWarning("Spark swap {SwapId}: transfer {TransferId} is not claimed yet ({Status})", swap.Id, transfer.Id,
                transfer.Status);
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

    public async Task<IReadOnlyList<IReadOnlyList<SwapOut>>> PlanExitsAsync(IReadOnlyCollection<SwapOut> pendingSwaps,
        CancellationToken ct = default)
    {
        var spark = pendingSwaps.Where(s => s.Provider == SwapProvider.Spark && s.Status == SwapOutStatus.Pending).ToList();
        var waiting = spark
            .Where(s => s.LightningFeeSats is not null && s.SparkTransferId is not null && s.DestinationAddress is null && s.TxId is null)
            .OrderBy(s => s.CreationDatetime)
            .ThenBy(s => s.Id)
            .ToList();
        if (waiting.Count == 0) return [];

        var nodes = await _nodeRepository.GetAllManagedByNodeGuard(withDisabled: false);
        var batches = new List<IReadOnlyList<SwapOut>>();
        foreach (var walletSwaps in waiting.GroupBy(s => s.SparkIdentity ?? "", StringComparer.OrdinalIgnoreCase))
        {
            var ofWallet = spark.Where(s => string.Equals(s.SparkIdentity, walletSwaps.Key, StringComparison.OrdinalIgnoreCase)).ToList();

            // A swap still being paid joins this batch once paid: a full wallet takes no new swaps, so that is soon
            if (ofWallet.Any(s => s.LightningFeeSats is null)) continue;

            if (await _spark.FindByIdentityAsync(walletSwaps.Key, ct) is not { } entry ||
                !(await _spark.EnsureReadyAsync(entry.Ref, ct)).IsReady)
            {
                continue; // ExitBatchAsync says why, once the wallet is back
            }

            var balance = (await _spark.GetBalanceAsync(entry.Ref, ct)).SatsBalance;
            var room = SparkGuardrails.RoomSats(entry.MaxBalanceSats, balance, ofWallet);
            var smallestSwap = nodes
                .Where(n => _settings.WalletFor(n) == entry.Ref)
                .Select(n => (long?)SwapAmountLimits.For(n, SwapProvider.Spark, _settings).MinSats)
                .Min();
            if (smallestSwap is { } min && room >= min)
            {
                _logger.LogDebug("{SparkWallet} ({Name}) has room for another swap ({Room} sats, its nodes' smallest Spark min swap {Min}): its {Count} paid swaps wait",
                    entry.Ref, entry.Name, room, min, walletSwaps.Count());
                continue;
            }

            _logger.LogInformation("{SparkWallet} ({Name}) is full ({Room} sats of room under its max balance): its {Count} paid swaps exit",
                entry.Ref, entry.Name, room, walletSwaps.Count());
            foreach (var destinationSwaps in walletSwaps.GroupBy(s => s.DestinationWalletId))
            {
                batches.AddRange(Pack(destinationSwaps));
            }
        }

        return batches.OrderBy(b => b[0].CreationDatetime).ThenBy(b => b[0].Id).ToList();
    }

    /// <summary>
    /// Whole swaps, in order, in batches of at most <see cref="MaxLeavesPerExit"/> leaves: a swap's leaves never split
    /// across exits, so each swap has one exit and one address. A swap with more leaves than that exits alone.
    /// </summary>
    private static IEnumerable<IReadOnlyList<SwapOut>> Pack(IEnumerable<SwapOut> swaps)
    {
        var batch = new List<SwapOut>();
        var leaves = 0;
        foreach (var swap in swaps)
        {
            var count = LeafIds(swap).Count;
            if (batch.Count > 0 && leaves + count > MaxLeavesPerExit)
            {
                yield return batch;
                batch = [];
                leaves = 0;
            }

            batch.Add(swap);
            leaves += count;
        }

        if (batch.Count > 0) yield return batch;
    }

    public Task<SparkExitStep> ExitBatchAsync(IReadOnlyList<SwapOut> batch, CancellationToken ct = default) =>
        ExitBatchAsync(batch, retryWithoutStuckLeaves: true, ct);

    private async Task<SparkExitStep> ExitBatchAsync(IReadOnlyList<SwapOut> batch, bool retryWithoutStuckLeaves, CancellationToken ct)
    {
        if (batch.Count == 0) throw new ArgumentException("A batch has at least one swap.", nameof(batch));
        foreach (var swap in batch) RequirePaid(swap);

        var first = batch[0];
        if (batch.Any(s => !string.Equals(s.SparkIdentity, first.SparkIdentity, StringComparison.OrdinalIgnoreCase) ||
                           s.DestinationWalletId != first.DestinationWalletId || s.DestinationAddress != first.DestinationAddress))
        {
            throw new InvalidOperationException(
                $"Spark swaps {Ids(batch)} can't exit together: their Spark wallet, destination wallet or address differ.");
        }

        var wallet = await SwapWalletAsync(first, ct);

        // Done once the batch's address has a confirmed payout. Checked before exiting too: an exit whose txid was
        // not saved (a crash) must not be followed by a second one. No address yet means no exit yet
        var payout = first.DestinationAddress is null ? null : await FindPayoutAsync(first, ct);
        if (payout is not null)
        {
            if (payout.Confirmations > 0)
            {
                Complete(batch, payout);
                return SparkExitStep.Completed;
            }

            var txId = payout.TransactionId.ToString();
            if (batch.Any(s => s.TxId != txId))
            {
                foreach (var swap in batch) swap.TxId = txId;
                SaveAll(batch);
            }

            return SparkExitStep.ExitSent;
        }

        if (batch.Any(s => s.TxId is not null)) return SparkExitStep.ExitSent;
        foreach (var swap in batch)
        {
            if (!await AttributeAsync(swap, ct)) return SparkExitStep.Waiting;
        }

        // The address is reserved only now, and saved on every swap of the batch before the exit: the payout is found
        // by it after a crash, and the swaps sharing it are the batch from then on
        if (first.DestinationAddress is null)
        {
            var address = await ReserveAddressAsync(first, ct);
            var now = _time.GetUtcNow();
            foreach (var swap in batch)
            {
                swap.DestinationAddress = address;
                swap.SparkExitDatetime ??= now;
            }

            if (!SaveAll(batch))
            {
                foreach (var swap in batch) swap.DestinationAddress = null;
                return SparkExitStep.Waiting;
            }

            _logger.LogInformation("Spark swaps {SwapIds}: exit together to {Address}", Ids(batch), address);
        }

        var leafIds = batch.SelectMany(LeafIds).ToList();
        WithdrawLeavesResult exit;
        try
        {
            exit = await _spark.WithdrawLeavesAsync(wallet.Ref, leafIds, first.DestinationAddress!, _settings.MaxExitFeeSats, ct);
        }
        catch (SparkLeavesNotSpendableException e)
        {
            // Only ever the batch's own leaves. A renewal keeps a leaf's id, so they can't have moved under another one:
            // either they already exited (a crash before the txid was saved), or some can't move yet (a renewal failed,
            // or they are frozen)
            if (await AlreadyExitedAsync(batch, wallet.Ref, leafIds, ct))
            {
                _logger.LogWarning("Spark swaps {SwapIds}: their leaves already exited; waiting for the payout", Ids(batch));
                return SparkExitStep.ExitSent;
            }

            // The swaps whose leaves can't move leave the batch and wait for the next run; the others exit without them
            var stuck = batch.Where(s => LeafIds(s).Intersect(e.LeafIds, StringComparer.OrdinalIgnoreCase).Any()).ToList();
            var rest = batch.Except(stuck).ToList();
            _logger.LogWarning("Spark swaps {SwapIds}: their leaves can't move yet ({Error}); trying again on the next run",
                stuck.Count > 0 ? Ids(stuck) : Ids(batch), e.Message);
            if (stuck.Count == 0 || rest.Count == 0 || !retryWithoutStuckLeaves) return SparkExitStep.Waiting;

            foreach (var swap in stuck) swap.DestinationAddress = null;
            if (!SaveAll(stuck)) return SparkExitStep.Waiting;

            return await ExitBatchAsync(rest, retryWithoutStuckLeaves: false, ct);
        }

        foreach (var swap in batch) swap.TxId = exit.Txid;
        SetFees(batch, exit.PayoutSats); // provisional until the payout confirms
        SaveAll(batch);

        _logger.LogInformation("Spark swaps {SwapIds}: cooperative exit {TxId} pays {Payout} of their {Received} sats to {Address} (exit fee {Fee} sats)",
            Ids(batch), exit.Txid, exit.PayoutSats, exit.SentSats, first.DestinationAddress, exit.FeeSats);
        return SparkExitStep.ExitSent;
    }

    /// <summary>
    /// Looks the payment up on the node. Saves its fee once it succeeded; returns why the swap failed,
    /// or null while it may still succeed.
    /// </summary>
    private async Task<string?> ResolvePaymentAsync(Node node, SwapOut swap, SparkWalletRef sparkWallet, CancellationToken ct)
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

            case null when _time.GetUtcNow() > PaymentDeadlineOf(swap):
                // The node has no record of the payment and the invoice expired: no payment will arrive. The SSP's
                // view is only a hint (not every SSP serves it), so a failed lookup does not block this
                var receive = await ReceiveStatusAsync(swap, sparkWallet);
                return $"The Lightning payment never reached the node and the invoice expired (SSP receive status: {receive ?? "unknown"})";

            default:
                return null;
        }
    }

    private async Task<string?> ReceiveStatusAsync(SwapOut swap, SparkWalletRef sparkWallet)
    {
        try
        {
            return await _spark.GetReceiveStatusAsync(sparkWallet, swap.ProviderId!);
        }
        catch (Exception e)
        {
            _logger.LogWarning("Spark swap {SwapId}: the SSP did not report the receive status: {Error}", swap.Id, e.Message);
            return null;
        }
    }

    /// <summary>The transfer the SSP reports for the swap's receive request, if it reports one.</summary>
    private async Task<SparkTransfer?> ReportedTransferAsync(SwapOut swap, SparkWalletRef wallet, HashSet<string> attributed,
        CancellationToken ct)
    {
        if (string.IsNullOrEmpty(swap.ProviderId)) return null;

        LightningReceiveRequest? request;
        try
        {
            request = await _spark.GetReceiveRequestAsync(wallet, swap.ProviderId, ct);
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

        var transfer = await _spark.GetTransferAsync(wallet, transferId, ct);
        if (transfer is null || transfer.Leaves.Count == 0 ||
            !string.Equals(transfer.ReceiverIdentityPublicKey, swap.SparkIdentity, StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogWarning("Spark swap {SwapId}: transfer {TransferId} is not one this wallet received, with leaves",
                swap.Id, transferId);
            return null;
        }

        return transfer;
    }

    /// <summary>Whether a cooperative exit the wallet sent since the batch's oldest swap was created moved any of its leaves.</summary>
    private async Task<bool> AlreadyExitedAsync(IReadOnlyList<SwapOut> batch, SparkWalletRef wallet, IReadOnlyCollection<string> leafIds,
        CancellationToken ct)
    {
        var since = batch.Min(s => s.CreationDatetime) - TransferLookback;
        var sent = await _spark.GetTransfersAsync(wallet, TransferDirection.Sent, since, ct);
        return sent
            .Where(t => string.Equals(t.Type, ExitTransferType, StringComparison.OrdinalIgnoreCase))
            .Any(t => t.Leaves.Any(l => leafIds.Contains(l.Id, StringComparer.OrdinalIgnoreCase)));
    }

    private void Complete(IReadOnlyList<SwapOut> batch, Payout payout)
    {
        foreach (var swap in batch)
        {
            swap.Status = SwapOutStatus.Completed;
            swap.TxId = payout.TransactionId.ToString();
        }

        SetFees(batch, payout.Value, payoutConfirmed: true);
        SaveAll(batch);

        foreach (var swap in batch)
        {
            _logger.LogInformation("Spark swap {SwapId}: completed, {Payout} of the {BatchPayout} sats confirmed at {Address} in {TxId} (on-chain fee {OnChainFee} sats)",
                swap.Id, swap.PayoutSats, payout.Value, swap.DestinationAddress, swap.TxId, swap.OnChainFeeSats);
        }
    }

    /// <summary>
    /// The fees of a batch's swaps from what their exit delivered on-chain. Spark's own fee is charged on the Lightning
    /// route, so it is part of the routing fee (<see cref="SwapOut.LightningFeeSats"/>). The on-chain fee is the move
    /// from Spark to L1: the SSP's exit fee and the L1 broadcast, what the batch's leaves held minus the payout, shared
    /// in proportion to what each swap brought in (the rounding's remainder to the oldest, the batch's first). Once the
    /// payout confirmed, each swap delivered what it brought in minus its share. The service fee is only what the
    /// payment brought in short of the amount, which receiving into Spark normally leaves at 0.
    /// </summary>
    private static void SetFees(IReadOnlyList<SwapOut> batch, long deliveredSats, bool payoutConfirmed = false)
    {
        var received = batch.Select(s => s.SparkReceivedSats ?? s.SatsAmount).ToList();
        var total = received.Sum();
        var fee = Math.Max(0, total - deliveredSats);
        var shares = received.Select(r => total == 0 ? 0 : (long)((decimal)fee * r / total)).ToList();
        shares[0] += fee - shares.Sum();

        for (var i = 0; i < batch.Count; i++)
        {
            var swap = batch[i];
            swap.ServiceFeeSats = Math.Max(0, swap.SatsAmount - received[i]);
            swap.OnChainFeeSats = shares[i];
            if (payoutConfirmed) swap.PayoutSats = received[i] - shares[i];
        }
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

    /// <summary>The wallet the swap was paid into, whichever the node uses now, connected.</summary>
    private async Task<SparkWalletEntry> SwapWalletAsync(SwapOut swap, CancellationToken ct)
    {
        var identity = swap.SparkIdentity ?? throw new InvalidOperationException($"Spark swap {swap.Id} has no Spark identity.");
        var entry = await _spark.FindByIdentityAsync(identity, ct) ?? throw new InvalidOperationException(
            $"Spark swap {swap.Id} was paid into Spark identity {identity}, which no Spark wallet in NodeGuard has: " +
            $"it must be resolved by hand with the seed of {identity}.");

        var status = await _spark.EnsureReadyAsync(entry.Ref, ct);
        if (!status.IsReady) throw new SparkUnavailableException(status.Reason ?? $"{entry.Name} is not available.");

        return entry;
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

    /// <summary>Saves a batch's progress in one transaction; returns whether it was saved.</summary>
    private bool SaveAll(IReadOnlyCollection<SwapOut> batch)
    {
        if (batch.Count == 1) return Save(batch.First());

        var (saved, error) = _swapOutRepository.UpdateRange(batch);
        if (!saved)
        {
            _logger.LogError("Spark swaps {SwapIds}: could not save their progress: {Error}", Ids(batch), error);
        }

        return saved;
    }

    private static string Ids(IEnumerable<SwapOut> swaps) => string.Join(", ", swaps.Select(s => s.Id));

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
