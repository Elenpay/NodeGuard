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
using NBXplorer.Models;
using NodeGuard.Data.Models;
using NodeGuard.Data.Repositories.Interfaces;
using NSpark;

namespace NodeGuard.Services.Spark;

/// <summary>
/// Spark as a swap-out provider, in two legs:
/// <list type="number">
/// <item>creation: NodeGuard's Spark wallet issues a BOLT11 invoice (through the SSP), the swap is
/// recorded, and then the node pays the invoice;</item>
/// <item>monitoring (<see cref="AdvanceSwapAsync"/>): once the payment has settled, the transfer is
/// claimed and the whole transit wallet exits on-chain to the swap's destination address in one
/// cooperative exit. The swap completes when that address receives a confirmed payout.</item>
/// </list>
/// Each step can be retried after a crash: the swap is recorded before the payment, the payment is
/// looked up by its hash, and the payout by its address (the SSP may fee-bump the exit, changing its
/// txid). One Spark swap is in flight at a time, as each exit drains the transit wallet.
/// </summary>
public interface ISparkSwapService
{
    /// <summary>Creates, records and pays a Spark swap; see <see cref="ISwapsService.CreateSwapOutAsync"/>.</summary>
    /// <exception cref="SparkUnavailableException">Spark is disabled or unavailable.</exception>
    /// <exception cref="InvalidOperationException">Another Spark swap is in flight.</exception>
    Task<SwapOutCreation> CreateSwapOutAsync(Node node, SwapOut swapOut, SwapOutRequest request, CancellationToken ct = default);

    /// <summary>
    /// Moves a pending Spark swap forward and returns its state. Progress (the payment's fee, the exit)
    /// is saved on <paramref name="swap"/> as it happens.
    /// </summary>
    Task<SwapResponse> AdvanceSwapAsync(Node node, SwapOut swap, CancellationToken ct = default);
}

public sealed class SparkSwapService : ISparkSwapService
{
    public const int InvoiceExpirySeconds = 1800;
    private const int PaymentTimeoutSeconds = 120;
    private const decimal DefaultMaxRoutingFeePercent = 0.5m;
    private static readonly TimeSpan PaymentLookupTimeout = TimeSpan.FromSeconds(30);

    /// <summary>When nothing has arrived by then, the invoice has expired and the payment never will.</summary>
    private static readonly TimeSpan PaymentDeadline = TimeSpan.FromSeconds(InvoiceExpirySeconds) + TimeSpan.FromMinutes(10);

    /// <summary>Serializes reservations of the single Spark slot within this process; the database index does across processes.</summary>
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
        if (string.IsNullOrWhiteSpace(request.Address) || !IsAddressOn(request.Address, Network))
        {
            throw new ArgumentException($"The destination must be a {Network} address.", nameof(request));
        }

        var status = await _spark.EnsureReadyAsync(ct);
        if (!status.IsReady) throw new SparkUnavailableException(status.Reason ?? "Spark is not available.");

        string paymentRequest;
        await Reservation.WaitAsync(ct);
        try
        {
            var inFlight = (await _swapOutRepository.GetAllPending()).FirstOrDefault(s => s.Provider == SwapProvider.Spark);
            if (inFlight is not null)
            {
                throw new InvalidOperationException(
                    $"Spark swap {inFlight.Id} is still in flight: Spark swaps run one at a time, as each one drains the transit wallet.");
            }

            var invoice = await _spark.CreateInvoiceAsync(request.Amount, $"NodeGuard swap-out from {node.Name}",
                InvoiceExpirySeconds, ct);
            paymentRequest = invoice.PaymentRequest;

            swapOut.Provider = SwapProvider.Spark;
            swapOut.ProviderId = invoice.RequestId ?? invoice.PaymentHash;
            swapOut.Status = SwapOutStatus.Pending;
            swapOut.SatsAmount = request.Amount;
            swapOut.DestinationAddress = request.Address;
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
        if (string.IsNullOrEmpty(swap.PaymentHash) || string.IsNullOrEmpty(swap.DestinationAddress))
        {
            throw new InvalidOperationException($"Spark swap {swap.Id} has no payment hash or destination address.");
        }

        var status = await _spark.EnsureReadyAsync(ct);
        if (!status.IsReady) throw new SparkUnavailableException(status.Reason ?? "Spark is not available.");
        if (swap.SparkIdentity is { } identity && !string.Equals(identity, status.IdentityPublicKey, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"Spark swap {swap.Id} was paid into Spark identity {identity}, but NodeGuard's Spark wallet is now " +
                $"{status.IdentityPublicKey}: it must be resolved by hand with the seed of {identity}.");
        }

        // Leg 1: the node's payment into Spark
        if (swap.LightningFeeSats is null)
        {
            var failure = await ResolvePaymentAsync(node, swap, ct);
            if (failure is not null) return Response(swap, SwapOutStatus.Failed, failure);
            if (swap.LightningFeeSats is null) return Response(swap);
        }

        // Done once the destination address has a confirmed payout. Checked before exiting too: an exit whose
        // txid was not saved (a crash) must not be followed by a second one
        var payout = await FindPayoutAsync(swap, ct);
        if (payout is not null)
        {
            return payout.Confirmations > 0
                ? Response(swap, SwapOutStatus.Completed, txId: payout.TransactionId.ToString(),
                    serviceFee: Math.Max(0, swap.SatsAmount - payout.Value))
                : Response(swap, txId: payout.TransactionId.ToString());
        }

        if (swap.TxId is not null)
        {
            return Response(swap);
        }

        // Leg 2: claim what the payment brought in and exit everything to the destination address
        await _spark.ClaimPendingAsync(ct);
        var balance = await _spark.GetBalanceAsync(ct);
        if (balance.SatsBalance.Available <= 0)
        {
            _logger.LogInformation("Spark swap {SwapId}: nothing claimable yet ({Incoming} sats incoming)",
                swap.Id, balance.SatsBalance.Incoming);
            return Response(swap);
        }

        var exit = await _spark.WithdrawAllAsync(swap.DestinationAddress, _settings.MaxExitFeeSats, ct);
        swap.TxId = exit.Txid;
        swap.ServiceFeeSats = exit.FeeSats;
        Save(swap);

        _logger.LogInformation(
            "Spark swap {SwapId}: cooperative exit {TxId} pays {Payout} sats to {Address} (SSP fee {Fee} sats; left behind: " +
            "{Frozen} frozen, {Unrenewed} unrenewed, {Locked} locked, {Unclaimed} unclaimed)",
            swap.Id, exit.Txid, exit.PayoutSats, swap.DestinationAddress, exit.FeeSats, exit.FrozenSats, exit.UnrenewedSats,
            exit.LockedSats, exit.UnclaimedSats);
        return Response(swap);
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

    private sealed record Payout(uint256 TransactionId, long Value, long Confirmations);

    /// <summary>The destination wallet's transaction paying the swap's destination address, if any.</summary>
    private async Task<Payout?> FindPayoutAsync(SwapOut swap, CancellationToken ct)
    {
        var wallet = swap.DestinationWalletId is { } walletId ? await _walletRepository.GetById(walletId) : null;
        var strategy = wallet?.GetDerivationStrategy();
        if (strategy is null)
        {
            throw new InvalidOperationException($"Spark swap {swap.Id} has no destination wallet to find its payout in.");
        }

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

    private void Save(SwapOut swap)
    {
        var (saved, error) = _swapOutRepository.Update(swap);
        if (!saved)
        {
            // Not fatal: every step can be found again (the payment by its hash, the payout by its address)
            _logger.LogError("Spark swap {SwapId}: could not save its progress: {Error}", swap.Id, error);
        }
    }

    private static SwapResponse Response(SwapOut swap, SwapOutStatus? status = null, string? error = null,
        string? txId = null, long? serviceFee = null) => new()
    {
        Id = swap.ProviderId!,
        HtlcAddress = string.Empty,
        PaymentHash = swap.PaymentHash,
        Amount = swap.SatsAmount,
        OffchainFee = swap.LightningFeeSats ?? 0,
        // The SSP's fee covers the exit's miner fee
        ServerFee = serviceFee ?? swap.ServiceFeeSats ?? 0,
        OnchainFee = 0,
        Status = status ?? swap.Status,
        TxId = txId ?? swap.TxId,
        ErrorMessage = error ?? swap.ErrorDetails
    };

    private static bool IsAddressOn(string address, Network network)
    {
        try
        {
            BitcoinAddress.Create(address, network);
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
    }
}
