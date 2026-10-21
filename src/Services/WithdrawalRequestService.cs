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
using NBXplorer.DerivationStrategy;
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
///
/// RBF rule: a request's status only ever describes its own transaction, and only a confirmation changes it. Creating,
/// executing or abandoning a replacement never writes the request it replaces, which stays pending confirmation with
/// the replacement as its next hop (<see cref="RbfReplacements.Newest"/>); the replaced request gets its final status
/// (Bumped, Cancelled, or OnChainConfirmed when it won the race) from <see cref="ReconcileReplacementChainsAsync"/>.
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
    /// Validates and creates the RBF CANCELLATION of a withdrawal that is waiting for on-chain confirmation: the same
    /// locked UTXOs, a higher fee rate, and a single output that returns the funds to a fresh address of the wallet, so
    /// the original payment never confirms. Same status rules as <see cref="CreateBumpRequestAsync"/>; once the
    /// cancellation confirms the replaced request ends up Cancelled instead of Bumped.
    /// </summary>
    Task<WalletWithdrawalRequest> CreateCancelRequestAsync(int originalRequestId, MempoolRecommendedFeesType feeType,
        decimal? customFeeRate);

    /// <summary>
    /// Executes a hot-wallet request (fresh or replacement): moves it to PSBTSignaturesPending, generates and persists
    /// the template PSBT and schedules <see cref="PerformWithdrawalJob"/>, which signs and broadcasts. On failure the
    /// exception is rethrown and the caller decides what happens to the request; the request it replaces, if any, is
    /// never touched.
    /// </summary>
    /// <returns>The template PSBT; its global transaction hash is the txid that will be broadcast.</returns>
    Task<PSBT> ScheduleHotWalletWithdrawalAsync(int requestId);

    /// <summary>
    /// Gives every live request linked by RBF its final status once its chain holds a confirmed transaction, from the
    /// database alone. A request whose original (direct or through earlier replacements) confirmed fails: RBF does
    /// not guarantee the replacement wins. A request one of whose replacements confirmed becomes Bumped, or
    /// Cancelled when the confirmed replacement cancels it. Each request writes only its own row, so a failed write
    /// is simply retried on the next run. Run by <see cref="MonitorWithdrawalsJob"/> after it settles confirmations.
    /// </summary>
    Task ReconcileReplacementChainsAsync();
}

public class WithdrawalRequestService : IWithdrawalRequestService
{
    public const string BumpDescriptionPrefix = "Bump of request";
    public const string CancelDescriptionPrefix = "Cancel of request";

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

    public Task<WalletWithdrawalRequest> CreateBumpRequestAsync(int originalRequestId,
        MempoolRecommendedFeesType feeType, decimal? customFeeRate)
        => CreateReplacementAsync(originalRequestId, feeType, customFeeRate, cancellation: false);

    public Task<WalletWithdrawalRequest> CreateCancelRequestAsync(int originalRequestId,
        MempoolRecommendedFeesType feeType, decimal? customFeeRate)
        => CreateReplacementAsync(originalRequestId, feeType, customFeeRate, cancellation: true);

    /// <summary>
    /// A fee bump keeps the original destinations and pays the higher fee from the change; a cancellation keeps only the
    /// inputs and sends everything, minus the fee, back to the wallet. Everything else — validation, status rules, UTXO
    /// locking — is the same.
    /// </summary>
    private async Task<WalletWithdrawalRequest> CreateReplacementAsync(int originalRequestId,
        MempoolRecommendedFeesType feeType, decimal? customFeeRate, bool cancellation)
    {
        var original = await _walletWithdrawalRequestRepository.GetById(originalRequestId)
                       ?? throw new BumpingException("Withdrawal request not found", BumpingErrorReason.RequestNotFound);

        if (original.Status != WalletWithdrawalRequestStatus.OnChainConfirmationPending)
        {
            throw new BumpingException(
                "Only withdrawals that are pending on-chain confirmation can be replaced.",
                BumpingErrorReason.InvalidState);
        }

        var replacements = await _walletWithdrawalRequestRepository.GetReplacementsAsync(new[] { original.Id });
        if (new RbfReplacements(replacements[original.Id]).Live is { } live)
        {
            throw new BumpingException(
                $"Withdrawal request {original.Id} is already being replaced by request {live.Id}; replace that request instead.",
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
                "Only transactions with no confirmations can be replaced. This transaction has already been mined.",
                BumpingErrorReason.AlreadyConfirmed);
        }

        var destinations = original.WalletWithdrawalRequestDestinations ?? new List<WalletWithdrawalRequestDestination>();
        if (destinations.Count == 0)
        {
            throw new BumpingException("Original withdrawal request destinations not found",
                BumpingErrorReason.InvalidState);
        }

        // A cancellation replaces every output, so the shape of the original does not matter to it.
        if (!cancellation && original.Changeless && destinations.Count > 1)
        {
            throw new BumpingException(
                "Fee bumping is not supported for changeless transactions. Please create a new withdrawal with higher fees instead.",
                BumpingErrorReason.ChangelessMultipleDestinations);
        }

        var wallet = original.Wallet
                     ?? throw new BumpingException("The wallet of the withdrawal request could not be loaded.",
                         BumpingErrorReason.InvalidState);

        var derivationStrategy = wallet.GetDerivationStrategy()
                                 ?? throw new BumpingException("The wallet has no derivation strategy.",
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
        // when there is more than one destination. A cancellation has a single output by construction.
        if (!cancellation && original.UTXOs is { Count: > 0 } && destinations.Count > 1)
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

        // RBF replaces the very same inputs: the replacement locks exactly the UTXOs of the request it replaces.
        var lockedUtxos = await _fmutxoRepository.GetLockedUTXOsByWithdrawalId(original.Id);
        if (lockedUtxos.Count == 0)
        {
            throw new BumpingException("The withdrawal request to replace has no UTXOs locked.",
                BumpingErrorReason.InvalidState);
        }

        List<WalletWithdrawalRequestDestination> replacementDestinations;
        if (cancellation)
        {
            // Everything the inputs hold goes back to a fresh address of the same wallet; GenerateTemplatePSBT's
            // changeless branch then subtracts the fee from that single output.
            var returnAddress = await _nbXplorerService.GetUnusedAsync(derivationStrategy, DerivationFeature.Deposit, 0, true)
                                ?? throw new BumpingException("Could not derive an address of the wallet to return the funds to.",
                                    BumpingErrorReason.PersistenceError);

            replacementDestinations = new List<WalletWithdrawalRequestDestination>
            {
                new()
                {
                    Address = returnAddress.Address.ToString(),
                    Amount = Money.Satoshis(lockedUtxos.Sum(u => u.SatsAmount)).ToDecimal(MoneyUnit.BTC),
                },
            };
        }
        else
        {
            replacementDestinations = destinations
                .Select(d => new WalletWithdrawalRequestDestination { Address = d.Address, Amount = d.Amount })
                .ToList();
        }

        var prefix = cancellation ? CancelDescriptionPrefix : BumpDescriptionPrefix;
        var replacement = new WalletWithdrawalRequest
        {
            Description = $"{prefix} {original.Id}: {StripReplacementPrefix(original.Description)}",
            Changeless = cancellation || original.Changeless,
            WithdrawAllFunds = !cancellation && original.WithdrawAllFunds,
            MempoolRecommendedFeesType = feeType,
            CustomFeeRate = newFeeRate,
            UserRequestorId = original.UserRequestorId,
            RequestMetadata = original.RequestMetadata,
            // The replacement represents the same external request, so callers can keep tracking it by reference id.
            ReferenceId = original.ReferenceId,
            CoinSelectionStrategy = original.CoinSelectionStrategy,
            BumpingWalletWithdrawalRequestId = original.Id,
            IsRbfCancellation = cancellation,
            WalletId = original.WalletId,
            // Hot wallets have no human approvers, so the request goes straight to PSBTSignaturesPending, the status
            // PerformWithdrawal requires. Same convention as TransferFundsModal and NodeGuardService.RequestWithdrawal.
            Status = wallet.IsHotWallet
                ? WalletWithdrawalRequestStatus.PSBTSignaturesPending
                : WalletWithdrawalRequestStatus.Pending,
            WalletWithdrawalRequestDestinations = replacementDestinations,
        };

        var (added, addError) = await _walletWithdrawalRequestRepository.AddAsync(replacement);
        if (!added)
        {
            _logger.LogError("Error creating the replacement of withdrawal request {RequestId}: {Error}", original.Id, addError);
            throw new BumpingException(addError ?? "Could not create the replacement request",
                BumpingErrorReason.PersistenceError);
        }

        try
        {
            var outpoints = lockedUtxos.Select(u => OutPoint.Parse($"{u.TxId}:{u.OutputIndex}")).ToList();
            var utxos = await _coinSelectionService.GetUTXOsByOutpointAsync(derivationStrategy, outpoints);
            if (utxos.Select(u => u.Outpoint).Distinct().Count() != outpoints.Count)
            {
                throw new BumpingException(
                    "The UTXOs of the transaction to replace are no longer available. It may have been confirmed.",
                    BumpingErrorReason.InvalidState);
            }

            await _coinSelectionService.LockUTXOs(utxos, replacement, BitcoinRequestType.WalletWithdrawal);
        }
        catch (Exception)
        {
            // Never leave a half-prepared replacement behind as a pending request.
            replacement.Status = WalletWithdrawalRequestStatus.Cancelled;
            replacement.RejectCancelDescription = "The replacement request could not be prepared";
            _walletWithdrawalRequestRepository.Update(replacement);
            throw;
        }

        return await _walletWithdrawalRequestRepository.GetById(replacement.Id) ?? replacement;
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
            throw;
        }
    }

    public async Task ReconcileReplacementChainsAsync()
    {
        var liveIds = await _walletWithdrawalRequestRepository.GetLiveRbfLinkedRequestIdsAsync();
        var chains = new Dictionary<int, Dictionary<int, WalletWithdrawalRequest>>();

        foreach (var id in liveIds)
        {
            try
            {
                if (!chains.TryGetValue(id, out var chain))
                {
                    chain = (await _walletWithdrawalRequestRepository.GetRbfChainAsync(id)).ToDictionary(member => member.Id);
                    foreach (var memberId in chain.Keys) chains[memberId] = chain;
                }

                // The chain is the fresher read; a row that moved on in between is left to the next run. So is a
                // FinalizingPSBT one: PerformWithdrawal writes that row whole when it broadcasts.
                if (!chain.TryGetValue(id, out var request) || !request.IsLive
                    || request.Status == WalletWithdrawalRequestStatus.FinalizingPSBT) continue;

                if (FinalStatusFromChain(request, chain) is not var (status, reason)) continue;

                request.Status = status;
                request.RejectCancelDescription = reason;
                var (updated, error) = _walletWithdrawalRequestRepository.Update(request);
                if (updated)
                {
                    _logger.LogInformation("Withdrawal request {RequestId} is {Status}: {Reason}", request.Id, status, reason);
                }
                else
                {
                    _logger.LogError("Could not mark withdrawal request {RequestId} as {Status}: {Error}", request.Id, status, error);
                }
            }
            catch (Exception e)
            {
                _logger.LogError(e, "Error reconciling withdrawal request {RequestId} against its RBF chain", id);
            }
        }
    }

    /// <summary>
    /// What a live request becomes once its chain holds a confirmed transaction, or null while it does not. Every
    /// member of a chain spends the original's inputs, so one confirming means no other can. A confirmed
    /// descendant supersedes this request; a confirmed ancestor wins over it (only a reorg confirms both, and the
    /// payment happened); any other confirmed member, a sibling branch, just means this one lost.
    /// </summary>
    private static (WalletWithdrawalRequestStatus Status, string Reason)? FinalStatusFromChain(
        WalletWithdrawalRequest request, Dictionary<int, WalletWithdrawalRequest> chain)
    {
        var confirmed = chain.Values.Where(member => member.Status == WalletWithdrawalRequestStatus.OnChainConfirmed).ToList();
        if (confirmed.Count == 0) return null;

        if (Ancestors(request, chain).FirstOrDefault(a => a.Status == WalletWithdrawalRequestStatus.OnChainConfirmed) is { } minedBefore)
        {
            return (WalletWithdrawalRequestStatus.Failed, WalletWithdrawalRequest.MinedInsteadReason(minedBefore.Id, minedBefore.TxId));
        }

        foreach (var descendant in confirmed)
        {
            var lineage = Ancestors(descendant, chain).ToList();
            var index = lineage.FindIndex(a => a.Id == request.Id);
            if (index < 0) continue;

            // Cancelled iff a cancellation sits on the way: a bump of a cancellation still returns the funds, while a
            // cancellation of a bump still means this request's destinations were never paid.
            var cancellation = descendant.IsRbfCancellation || lineage.Take(index).Any(a => a.IsRbfCancellation);
            return WalletWithdrawalRequest.SupersededBy(descendant, cancellation);
        }

        var sibling = confirmed[0];
        return (WalletWithdrawalRequestStatus.Failed, WalletWithdrawalRequest.MinedInsteadReason(sibling.Id, sibling.TxId));
    }

    /// <summary>What <paramref name="request"/> replaces, nearest first, following the link while it stays in the chain.</summary>
    private static IEnumerable<WalletWithdrawalRequest> Ancestors(WalletWithdrawalRequest request,
        Dictionary<int, WalletWithdrawalRequest> chain)
    {
        var visited = new HashSet<int> { request.Id };
        var current = request;
        while (current.BumpingWalletWithdrawalRequestId is { } id && visited.Add(id) && chain.TryGetValue(id, out current))
        {
            yield return current;
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

    /// <summary>
    /// Replacements of replacements keep a single "Bump of request N:" / "Cancel of request N:" prefix, pointing at the
    /// request being replaced.
    /// </summary>
    private static string StripReplacementPrefix(string? description)
    {
        var text = description ?? string.Empty;
        if (!text.StartsWith(BumpDescriptionPrefix) && !text.StartsWith(CancelDescriptionPrefix)) return text;

        var colon = text.IndexOf(':');
        return colon >= 0 ? text[(colon + 1)..].Trim() : text;
    }
}
