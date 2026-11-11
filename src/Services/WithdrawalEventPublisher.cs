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

using Microsoft.EntityFrameworkCore;
using NodeGuard.Data;
using NodeGuard.Data.Models;
using NodeGuard.Helpers;
using Nodeguard;

namespace NodeGuard.Services;

/// <summary>
/// Turns persisted withdrawal request changes into wallet events. Called by the withdrawal repositories after a
/// successful save; best effort: it never throws, and does no work for a wallet nobody subscribes to.
/// </summary>
public interface IWithdrawalEventPublisher
{
    /// <summary>
    /// Reads the stored status and TxId of a request that is about to be updated, for
    /// <see cref="PublishUpdated"/>. Null, without touching the database, when nobody listens to its wallet or the
    /// request was never saved; also null if the read fails.
    /// </summary>
    WithdrawalStateBeforeUpdate? ReadBeforeUpdate(WalletWithdrawalRequest request);

    /// <summary>
    /// Publishes the event a saved update stands for, comparing <paramref name="request"/> with the state read by
    /// <see cref="ReadBeforeUpdate"/>. Does nothing when <paramref name="before"/> is null.
    /// </summary>
    void PublishUpdated(WithdrawalStateBeforeUpdate? before, WalletWithdrawalRequest request);

    /// <summary>
    /// A request was created. <paramref name="request"/> must carry its destinations and its wallet with keys.
    /// </summary>
    void PublishCreated(WalletWithdrawalRequest request);

    void PublishSignatureAdded(int walletId, int requestId);

    void PublishTransition(int walletId, int requestId,
        WalletWithdrawalRequestStatus previousStatus, string? previousTxId,
        WalletWithdrawalRequestStatus status, string? txId);
}

/// <summary>
/// What a withdrawal request looked like in the database right before an update
/// </summary>
public readonly record struct WithdrawalStateBeforeUpdate(int WalletId, int RequestId,
    WalletWithdrawalRequestStatus Status, string? TxId);

public class WithdrawalEventPublisher : IWithdrawalEventPublisher
{
    private readonly IWalletEventHub _hub;
    private readonly IDbContextFactory<ApplicationDbContext> _dbContextFactory;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<WithdrawalEventPublisher> _logger;

    public WithdrawalEventPublisher(IWalletEventHub hub,
        IDbContextFactory<ApplicationDbContext> dbContextFactory,
        TimeProvider timeProvider,
        ILogger<WithdrawalEventPublisher> logger)
    {
        _hub = hub;
        _dbContextFactory = dbContextFactory;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    private bool IsWatched(int walletId) => _hub.HasSubscribers(walletId);

    public WithdrawalStateBeforeUpdate? ReadBeforeUpdate(WalletWithdrawalRequest request)
    {
        if (request.Id <= 0 || !IsWatched(request.WalletId))
            return null;

        try
        {
            using var applicationDbContext = _dbContextFactory.CreateDbContext();

            var stored = applicationDbContext.WalletWithdrawalRequests
                .AsNoTracking()
                .Where(x => x.Id == request.Id)
                .Select(x => new { x.Status, x.TxId })
                .SingleOrDefault();

            return stored == null
                ? null
                : new WithdrawalStateBeforeUpdate(request.WalletId, request.Id, stored.Status, stored.TxId);
        }
        catch (Exception e)
        {
            _logger.LogError(e, "Wallet events: could not read withdrawal request {RequestId} before its update",
                request.Id);
            return null;
        }
    }

    public void PublishUpdated(WithdrawalStateBeforeUpdate? before, WalletWithdrawalRequest request)
    {
        if (before is not { } previous)
            return;

        PublishTransition(previous.WalletId, previous.RequestId, previous.Status, previous.TxId,
            request.Status, request.TxId);
    }

    public void PublishCreated(WalletWithdrawalRequest request)
    {
        if (!IsWatched(request.WalletId))
            return;

        Publish(request.WalletId, request.Id, WALLET_EVENT_TYPE.WalletEventWithdrawalCreated,
            () => WithdrawalEventMapper.ToWithdrawalEvent(request));
    }

    public void PublishSignatureAdded(int walletId, int requestId)
    {
        if (!IsWatched(walletId))
            return;

        Publish(walletId, requestId, WALLET_EVENT_TYPE.WalletEventWithdrawalSignatureAdded,
            () => LoadEvent(requestId, previousStatus: null, lookUpReplacement: false));
    }

    public void PublishTransition(int walletId, int requestId,
        WalletWithdrawalRequestStatus previousStatus, string? previousTxId,
        WalletWithdrawalRequestStatus status, string? txId)
    {
        if (!IsWatched(walletId))
            return;

        var type = WithdrawalEventMapper.TransitionEventType(previousStatus, previousTxId, status, txId);
        if (type == null)
            return;

        var lookUpReplacement = type is WALLET_EVENT_TYPE.WalletEventWithdrawalBumped
            or WALLET_EVENT_TYPE.WalletEventWithdrawalCancelled;

        Publish(walletId, requestId, type.Value, () => LoadEvent(requestId, previousStatus, lookUpReplacement));
    }

    private void Publish(int walletId, int requestId, WALLET_EVENT_TYPE type, Func<WithdrawalEvent?> buildPayload)
    {
        try
        {
            var payload = buildPayload();
            if (payload == null)
            {
                _logger.LogWarning("Wallet events: withdrawal request {RequestId} not found, {Type} not published",
                    requestId, type);
                return;
            }

            _hub.Publish(new WalletEvent
            {
                Type = type,
                WalletId = walletId,
                OccurredAtUnixMs = _timeProvider.GetUtcNow().ToUnixTimeMilliseconds(),
                Withdrawal = payload
            });
        }
        catch (Exception e)
        {
            _logger.LogError(e, "Wallet events: could not publish {Type} for withdrawal request {RequestId}",
                type, requestId);
        }
    }

    /// <summary>
    /// Synchronous on purpose: <see cref="Data.Repositories.WalletWithdrawalRequestRepository.Update"/> is, and the
    /// event has to be built before the next change of the same request is published
    /// </summary>
    private WithdrawalEvent? LoadEvent(int requestId, WalletWithdrawalRequestStatus? previousStatus, bool lookUpReplacement)
    {
        using var applicationDbContext = _dbContextFactory.CreateDbContext();

        var request = applicationDbContext.WalletWithdrawalRequests
            .AsNoTracking()
            .Include(x => x.Wallet).ThenInclude(x => x.Keys)
            .Include(x => x.WalletWithdrawalRequestPSBTs)
            .Include(x => x.WalletWithdrawalRequestDestinations)
            .AsSplitQuery()
            .SingleOrDefault(x => x.Id == requestId);

        if (request == null)
            return null;

        int? replacedBy = null;
        if (lookUpReplacement)
        {
            replacedBy = applicationDbContext.WalletWithdrawalRequests
                .Where(x => x.BumpingWalletWithdrawalRequestId == requestId)
                .OrderByDescending(x => x.Id)
                .Select(x => (int?)x.Id)
                .FirstOrDefault();
        }

        return WithdrawalEventMapper.ToWithdrawalEvent(request, previousStatus, replacedBy);
    }
}
