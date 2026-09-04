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

using NBitcoin;
using NodeGuard.Data.Models;
using NodeGuard.Data.Repositories.Interfaces;
using NodeGuard.Helpers;
using NodeGuard.Jobs;
using Quartz;

namespace NodeGuard.Services;

/// <summary>
/// Withdrawal-request lifecycle steps shared by the Blazor UI and the gRPC API: RBF fee bumps and the execution of
/// hot-wallet requests.
///
/// Keeping the orchestration here, rather than in BumpfeeModal.razor / Withdrawals.razor, lets the gRPC API expose it and the
/// E2E suite exercise the same code the pages run. Hot-wallet requests are moved to PSBTSignaturesPending explicitly: it is
/// the status PerformWithdrawal requires, and a hot wallet has no approvers whose signatures would get it there.
/// </summary>
public interface IWithdrawalRequestService
{
    /// <summary>
    /// Validates and creates the RBF bump of a withdrawal that is waiting for on-chain confirmation: same destinations
    /// and the same locked UTXOs, higher fee rate. Cold-wallet bumps start Pending (approvers sign them like any other
    /// request); hot-wallet bumps start in PSBTSignaturesPending and are executed with
    /// <see cref="ScheduleHotWalletWithdrawalAsync"/>. Throws <see cref="BumpingException"/> with a user-facing message
    /// and a <see cref="BumpingErrorReason"/> when the bump is refused.
    /// </summary>
    Task<WalletWithdrawalRequest> CreateBumpRequestAsync(int originalRequestId, MempoolRecommendedFeesType feeType,
        decimal? customFeeRate);

    /// <summary>
    /// Executes a hot-wallet request (fresh or bump): moves it to PSBTSignaturesPending, generates and persists the
    /// template PSBT, marks the request it replaces (if any) as Bumped and schedules <see cref="PerformWithdrawalJob"/>,
    /// which signs and broadcasts. On failure the replaced request is put back to OnChainConfirmationPending and the
    /// exception is rethrown; the caller decides what happens to the request itself.
    /// </summary>
    /// <returns>The template PSBT; its global transaction hash is the txid that will be broadcast.</returns>
    Task<PSBT> ScheduleHotWalletWithdrawalAsync(int requestId);

    /// <summary>
    /// Marks the request replaced by <paramref name="bumpRequest"/> as Bumped. Cold-wallet flow: called when the last
    /// human signature lands and the withdrawal job is scheduled. No-op when the request is not a bump.
    /// </summary>
    Task MarkOriginalAsBumpedAsync(WalletWithdrawalRequest bumpRequest);

    /// <summary>
    /// Puts the request replaced by <paramref name="bumpRequest"/> back to OnChainConfirmationPending when the bump is
    /// abandoned (cancelled, rejected or failed) and the original had already been marked Bumped. No-op otherwise.
    /// </summary>
    Task RevertOriginalAsync(WalletWithdrawalRequest bumpRequest);
}

public class WithdrawalRequestService : IWithdrawalRequestService
{
    public const string BumpDescriptionPrefix = "Bump of request";

    private readonly ILogger<WithdrawalRequestService> _logger;
    private readonly IWalletWithdrawalRequestRepository _walletWithdrawalRequestRepository;
    private readonly IFMUTXORepository _fmutxoRepository;
    private readonly ICoinSelectionService _coinSelectionService;
    private readonly INBXplorerService _nbXplorerService;
    private readonly IBitcoinService _bitcoinService;
    private readonly ISchedulerFactory _schedulerFactory;

    public WithdrawalRequestService(ILogger<WithdrawalRequestService> logger,
        IWalletWithdrawalRequestRepository walletWithdrawalRequestRepository,
        IFMUTXORepository fmutxoRepository,
        ICoinSelectionService coinSelectionService,
        INBXplorerService nbXplorerService,
        IBitcoinService bitcoinService,
        ISchedulerFactory schedulerFactory)
    {
        _logger = logger;
        _walletWithdrawalRequestRepository = walletWithdrawalRequestRepository;
        _fmutxoRepository = fmutxoRepository;
        _coinSelectionService = coinSelectionService;
        _nbXplorerService = nbXplorerService;
        _bitcoinService = bitcoinService;
        _schedulerFactory = schedulerFactory;
    }

    public async Task<WalletWithdrawalRequest> CreateBumpRequestAsync(int originalRequestId,
        MempoolRecommendedFeesType feeType, decimal? customFeeRate)
    {
        var original = await _walletWithdrawalRequestRepository.GetById(originalRequestId)
                       ?? throw new BumpingException("Withdrawal request not found", BumpingErrorReason.RequestNotFound);

        if (original.Status != WalletWithdrawalRequestStatus.OnChainConfirmationPending)
        {
            throw new BumpingException(
                "Bumpfee can only be used for transactions that are pending on-chain confirmation.",
                BumpingErrorReason.InvalidState);
        }

        if (string.IsNullOrEmpty(original.TxId))
        {
            throw new BumpingException("The withdrawal request has no transaction to replace.",
                BumpingErrorReason.InvalidState);
        }

        var originalTx = await _nbXplorerService.GetTransactionAsync(uint256.Parse(original.TxId))
                         ?? throw new BumpingException("The transaction to replace could not be retrieved.",
                             BumpingErrorReason.TransactionNotFound);

        if (originalTx.Confirmations > 0)
        {
            throw new BumpingException(
                "Bumpfee can only be used for transactions with no confirmations. This transaction has already been mined.",
                BumpingErrorReason.AlreadyConfirmed);
        }

        var destinations = original.WalletWithdrawalRequestDestinations ?? new List<WalletWithdrawalRequestDestination>();
        if (destinations.Count == 0)
        {
            throw new BumpingException("Original withdrawal request destinations not found",
                BumpingErrorReason.InvalidState);
        }

        if (original.Changeless && destinations.Count > 1)
        {
            throw new BumpingException(
                "Fee bumping is not supported for changeless transactions. Please create a new withdrawal with higher fees instead.",
                BumpingErrorReason.ChangelessMultipleDestinations);
        }

        var wallet = original.Wallet
                     ?? throw new BumpingException("The wallet of the withdrawal request could not be loaded.",
                         BumpingErrorReason.InvalidState);

        var newFeeRate = await ResolveFeeRateAsync(feeType, customFeeRate);

        var currentFeeRate = original.CustomFeeRate ?? 0;
        if (newFeeRate <= currentFeeRate)
        {
            throw new BumpingException($"Fee must be greater than the current one ({currentFeeRate} sat/vb)",
                BumpingErrorReason.FeeRateNotHigher);
        }

        // A single-destination request can shrink its destination to pay the higher fee (GenerateTemplatePSBT's
        // changeless branch subtracts the fee from the output), so the inputs-cover-everything check only applies
        // when there is more than one destination.
        if (original.UTXOs is { Count: > 0 } && destinations.Count > 1)
        {
            var vSize = originalTx.Transaction.GetVirtualSize();
            var newFee = vSize * newFeeRate / 100_000_000m;
            var inputAmount = Money.Satoshis(original.UTXOs.Sum(x => x.SatsAmount)).ToDecimal(MoneyUnit.BTC);
            if (inputAmount < newFee + original.TotalAmount + Constants.BITCOIN_DUST.ToDecimal(MoneyUnit.BTC))
            {
                throw new BumpingException(
                    "The new fee plus the amount exceeds the sum of the selected UTXOs or returns dust. Please lower the fee rate.",
                    BumpingErrorReason.FeeExceedsInputs);
            }
        }

        var bump = new WalletWithdrawalRequest
        {
            Description = $"{BumpDescriptionPrefix} {original.Id}: {StripBumpPrefix(original.Description)}",
            Changeless = original.Changeless,
            WithdrawAllFunds = original.WithdrawAllFunds,
            MempoolRecommendedFeesType = feeType,
            CustomFeeRate = newFeeRate,
            UserRequestorId = original.UserRequestorId,
            RequestMetadata = original.RequestMetadata,
            BumpingWalletWithdrawalRequestId = original.Id,
            WalletId = original.WalletId,
            // Hot wallets have no human approvers, so the request goes straight to PSBTSignaturesPending, the status
            // PerformWithdrawal requires. Same convention as TransferFundsModal and NodeGuardService.RequestWithdrawal.
            Status = wallet.IsHotWallet
                ? WalletWithdrawalRequestStatus.PSBTSignaturesPending
                : WalletWithdrawalRequestStatus.Pending,
            WalletWithdrawalRequestDestinations = destinations
                .Select(d => new WalletWithdrawalRequestDestination { Address = d.Address, Amount = d.Amount })
                .ToList(),
        };

        var (added, addError) = await _walletWithdrawalRequestRepository.AddAsync(bump);
        if (!added)
        {
            _logger.LogError("Error creating the bump of withdrawal request {RequestId}: {Error}", original.Id, addError);
            throw new BumpingException(addError ?? "Could not create the bump request",
                BumpingErrorReason.PersistenceError);
        }

        try
        {
            // RBF replaces the very same inputs: the bump locks exactly the UTXOs of the request it replaces.
            var lockedUtxos = await _fmutxoRepository.GetLockedUTXOsByWithdrawalId(original.Id);
            if (lockedUtxos.Count == 0)
            {
                throw new BumpingException("The withdrawal request to replace has no UTXOs locked.",
                    BumpingErrorReason.InvalidState);
            }

            var derivationStrategy = wallet.GetDerivationStrategy()
                                     ?? throw new BumpingException("The wallet has no derivation strategy.",
                                         BumpingErrorReason.InvalidState);

            var outpoints = lockedUtxos.Select(u => OutPoint.Parse($"{u.TxId}:{u.OutputIndex}")).ToList();
            var utxos = await _coinSelectionService.GetUTXOsByOutpointAsync(derivationStrategy, outpoints);
            if (utxos.Select(u => u.Outpoint).Distinct().Count() != outpoints.Count)
            {
                throw new BumpingException(
                    "The UTXOs of the transaction to replace are no longer available. It may have been confirmed.",
                    BumpingErrorReason.InvalidState);
            }

            await _coinSelectionService.LockUTXOs(utxos, bump, BitcoinRequestType.WalletWithdrawal);
        }
        catch (Exception)
        {
            // Never leave a half-prepared bump behind as a pending request.
            bump.Status = WalletWithdrawalRequestStatus.Cancelled;
            bump.RejectCancelDescription = "The bump request could not be prepared";
            _walletWithdrawalRequestRepository.Update(bump);
            throw;
        }

        return await _walletWithdrawalRequestRepository.GetById(bump.Id) ?? bump;
    }

    public async Task<PSBT> ScheduleHotWalletWithdrawalAsync(int requestId)
    {
        var request = await _walletWithdrawalRequestRepository.GetById(requestId)
                      ?? throw new InvalidOperationException($"Withdrawal request {requestId} not found");

        if (request.Wallet is not { IsHotWallet: true })
        {
            throw new InvalidOperationException(
                $"Withdrawal request {requestId} is not on a hot wallet, it needs human signatures");
        }

        switch (request.Status)
        {
            case WalletWithdrawalRequestStatus.Pending:
                // PerformWithdrawal refuses anything but PSBTSignaturesPending/FinalizingPSBT. A hot wallet has no
                // approvers whose signatures would move the request there, so it is promoted here, before the job that
                // signs and broadcasts is scheduled.
                request.Status = WalletWithdrawalRequestStatus.PSBTSignaturesPending;
                var (updated, error) = _walletWithdrawalRequestRepository.Update(request);
                if (!updated)
                {
                    throw new InvalidOperationException(
                        $"Could not update the status of withdrawal request {requestId}: {error}");
                }

                break;
            case WalletWithdrawalRequestStatus.PSBTSignaturesPending:
                break;
            default:
                throw new InvalidOperationException(
                    $"Withdrawal request {requestId} cannot be executed from status {request.Status}");
        }

        try
        {
            var templatePsbt = await _bitcoinService.GenerateTemplatePSBT(request);

            if (request.BumpingWalletWithdrawalRequestId != null)
            {
                await MarkOriginalAsBumpedAsync(request);
            }

            var scheduler = await _schedulerFactory.GetScheduler();
            var map = new JobDataMap();
            map.Put("withdrawalRequestId", request.Id);
            var job = SimpleJob.Create<PerformWithdrawalJob>(map, request.Id.ToString());
            await scheduler.ScheduleJob(job.Job, job.Trigger);

            return templatePsbt;
        }
        catch (Exception e)
        {
            _logger.LogError(e, "Error executing withdrawal request {RequestId}", request.Id);
            await RevertOriginalAsync(request);
            throw;
        }
    }

    public async Task MarkOriginalAsBumpedAsync(WalletWithdrawalRequest bumpRequest)
    {
        if (bumpRequest.BumpingWalletWithdrawalRequestId is not { } originalId) return;

        var original = await _walletWithdrawalRequestRepository.GetById(originalId)
                       ?? throw new BumpingException("Could not find bumping withdrawal request",
                           BumpingErrorReason.RequestNotFound);

        if (original.Status == WalletWithdrawalRequestStatus.Bumped) return;

        original.Status = WalletWithdrawalRequestStatus.Bumped;
        var (updated, error) = _walletWithdrawalRequestRepository.Update(original);
        if (!updated)
        {
            throw new BumpingException($"Could not mark withdrawal request {originalId} as bumped: {error}",
                BumpingErrorReason.PersistenceError);
        }
    }

    public async Task RevertOriginalAsync(WalletWithdrawalRequest bumpRequest)
    {
        if (bumpRequest.BumpingWalletWithdrawalRequestId is not { } originalId) return;

        var original = await _walletWithdrawalRequestRepository.GetById(originalId);
        if (original == null || original.Status != WalletWithdrawalRequestStatus.Bumped) return;

        original.Status = WalletWithdrawalRequestStatus.OnChainConfirmationPending;
        var (updated, error) = _walletWithdrawalRequestRepository.Update(original);
        if (!updated)
        {
            _logger.LogError("Could not put withdrawal request {RequestId} back to OnChainConfirmationPending: {Error}",
                originalId, error);
        }
    }

    private async Task<decimal> ResolveFeeRateAsync(MempoolRecommendedFeesType feeType, decimal? customFeeRate)
    {
        if (feeType == MempoolRecommendedFeesType.CustomFee)
        {
            if (customFeeRate is null or <= 0)
            {
                throw new BumpingException("A custom fee rate greater than zero is required.",
                    BumpingErrorReason.InvalidFeeRate);
            }

            return customFeeRate.Value;
        }

        var recommended = await _nbXplorerService.GetFeesByType(feeType);
        if (recommended is null or <= 0)
        {
            throw new BumpingException("The recommended fee rate could not be retrieved.",
                BumpingErrorReason.InvalidFeeRate);
        }

        return recommended.Value;
    }

    /// <summary>Bumps of bumps keep a single "Bump of request N:" prefix, pointing at the request being replaced.</summary>
    private static string StripBumpPrefix(string? description)
    {
        var text = description ?? string.Empty;
        if (!text.StartsWith(BumpDescriptionPrefix)) return text;

        var colon = text.IndexOf(':');
        return colon >= 0 ? text[(colon + 1)..].Trim() : text;
    }
}
