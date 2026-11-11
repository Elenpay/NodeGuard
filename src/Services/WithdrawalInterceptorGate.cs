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

using System.Collections.Concurrent;
using NodeGuard.Data.Models;
using NodeGuard.Helpers;
using Nodeguard;

namespace NodeGuard.Services;

public enum WithdrawalInterceptOutcomeKind
{
    /// <summary>No interceptor is connected to the wallet: the check does not apply</summary>
    Skipped,
    Allowed,
    Blocked
}

/// <param name="Interceptors">Who was asked (subscriber names), empty when skipped</param>
/// <param name="DecidedBy">The interceptor whose answer (or silence) blocked the withdrawal</param>
public record WithdrawalInterceptOutcome(
    WithdrawalInterceptOutcomeKind Kind,
    string? DecisionId,
    IReadOnlyList<string> Interceptors,
    string? Reason = null,
    string? DecidedBy = null)
{
    public static WithdrawalInterceptOutcome Skipped { get; } = new(WithdrawalInterceptOutcomeKind.Skipped, null, []);
}

/// <summary>
/// Asks the integrators intercepting a wallet's withdrawals (SubscribeWalletEvents with intercept_withdrawals) to
/// allow or block one. Fail closed: a connected interceptor that blocks, does not answer in time or leaves first
/// blocks the withdrawal. With nobody intercepting, the check is skipped.
/// </summary>
public interface IWithdrawalInterceptorGate
{
    Task<WithdrawalInterceptOutcome> RequestDecisionAsync(WalletWithdrawalRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Records an interceptor's answer; false when there is no such pending decision for that subscription
    /// (unknown, expired, already answered, or asked of another subscription)
    /// </summary>
    bool Decide(Guid subscriptionId, string decisionId, bool allow, string? reason);
}

public class WithdrawalInterceptorGate : IWithdrawalInterceptorGate
{
    private sealed record Answer(bool Allow, string? Reason, string? Subscriber);

    private readonly ConcurrentDictionary<(string DecisionId, Guid SubscriptionId), (TaskCompletionSource<Answer> Answer, string? Subscriber)>
        _pending = new();

    private readonly IWalletEventHub _hub;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<WithdrawalInterceptorGate> _logger;
    private readonly TimeSpan? _timeout;

    public WithdrawalInterceptorGate(IWalletEventHub hub, TimeProvider timeProvider, ILogger<WithdrawalInterceptorGate> logger)
        : this(hub, timeProvider, logger, null)
    {
    }

    internal WithdrawalInterceptorGate(IWalletEventHub hub, TimeProvider timeProvider,
        ILogger<WithdrawalInterceptorGate> logger, TimeSpan? timeout)
    {
        _hub = hub;
        _timeProvider = timeProvider;
        _logger = logger;
        _timeout = timeout;
    }

    private TimeSpan Timeout => _timeout ?? TimeSpan.FromSeconds(Constants.WITHDRAWAL_INTERCEPTOR_TIMEOUT_SECONDS);

    public async Task<WithdrawalInterceptOutcome> RequestDecisionAsync(WalletWithdrawalRequest request,
        CancellationToken cancellationToken = default)
    {
        var interceptors = _hub.GetInterceptors(request.WalletId);
        if (interceptors.Count == 0)
            return WithdrawalInterceptOutcome.Skipped;

        var names = interceptors.Select(x => x.Subscriber ?? x.Id.ToString()).ToList();
        var decisionId = Guid.NewGuid().ToString("N");
        var timeout = Timeout;
        var now = _timeProvider.GetUtcNow();

        var payload = WithdrawalEventMapper.ToWithdrawalEvent(request);
        payload.DecisionId = decisionId;
        payload.DecisionDeadlineUnixMs = (now + timeout).ToUnixTimeMilliseconds();
        var decisionRequested = new WalletEvent
        {
            Type = WALLET_EVENT_TYPE.WalletEventWithdrawalDecisionRequested,
            WalletId = request.WalletId,
            OccurredAtUnixMs = now.ToUnixTimeMilliseconds(),
            Withdrawal = payload
        };

        var registrations = new List<CancellationTokenRegistration>();
        var answers = new List<Task<Answer>>();
        using var deadlineCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        try
        {
            foreach (var interceptor in interceptors)
            {
                var answer = new TaskCompletionSource<Answer>(TaskCreationOptions.RunContinuationsAsynchronously);
                _pending[(decisionId, interceptor.Id)] = (answer, interceptor.Subscriber);
                answers.Add(answer.Task);

                var name = interceptor.Subscriber;
                registrations.Add(interceptor.Closed.Register(() =>
                    answer.TrySetResult(new Answer(false, "the integrator disconnected before deciding", name))));

                if (!_hub.Send(interceptor, decisionRequested))
                    answer.TrySetResult(new Answer(false, "the integrator could not be reached", name));
            }

            _logger.LogInformation(
                "Withdrawal request {RequestId} on wallet {WalletId} waits up to {Timeout} for {Interceptors} (decision {DecisionId})",
                request.Id, request.WalletId, timeout, names, decisionId);

            var deadline = Task.Delay(timeout, _timeProvider, deadlineCts.Token);
            var remaining = new List<Task<Answer>>(answers);
            while (remaining.Count > 0)
            {
                var finished = await Task.WhenAny(remaining.Cast<Task>().Append(deadline));
                if (finished == deadline)
                {
                    var silent = interceptors.Where((_, i) => !answers[i].IsCompleted)
                        .Select(x => x.Subscriber ?? x.Id.ToString()).ToList();
                    var why = cancellationToken.IsCancellationRequested
                        ? "NodeGuard stopped waiting before the integrator decided"
                        : $"no decision from the integrator within {timeout.TotalSeconds:0}s";
                    return new WithdrawalInterceptOutcome(WithdrawalInterceptOutcomeKind.Blocked, decisionId, names,
                        why, string.Join(", ", silent));
                }

                var answerTask = (Task<Answer>)finished;
                remaining.Remove(answerTask);
                var result = await answerTask;
                if (!result.Allow)
                {
                    return new WithdrawalInterceptOutcome(WithdrawalInterceptOutcomeKind.Blocked, decisionId, names,
                        string.IsNullOrWhiteSpace(result.Reason) ? "blocked by the integrator" : result.Reason,
                        result.Subscriber);
                }
            }

            return new WithdrawalInterceptOutcome(WithdrawalInterceptOutcomeKind.Allowed, decisionId, names);
        }
        finally
        {
            await deadlineCts.CancelAsync();
            foreach (var registration in registrations)
                await registration.DisposeAsync();
            foreach (var interceptor in interceptors)
                _pending.TryRemove((decisionId, interceptor.Id), out _);
        }
    }

    public bool Decide(Guid subscriptionId, string decisionId, bool allow, string? reason)
    {
        if (!_pending.TryGetValue((decisionId, subscriptionId), out var pending))
            return false;

        return pending.Answer.TrySetResult(new Answer(allow, reason, pending.Subscriber));
    }
}
