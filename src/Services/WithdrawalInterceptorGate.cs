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

/// <summary>
/// Coordinates one withdrawal decision: asks every interceptor through its own <see cref="InterceptorDecision"/> and
/// applies the policy. The first block wins; the withdrawal is allowed only when every interceptor allows it.
/// </summary>
public class WithdrawalInterceptorGate : IWithdrawalInterceptorGate
{
    private sealed record Answer(bool Allow, string? Reason, string Subscriber);

    private readonly ConcurrentDictionary<(string DecisionId, Guid SubscriptionId), InterceptorDecision> _pending = new();

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

        var decisions = interceptors
            .Select(x => new InterceptorDecision(this, decisionId, x, timeout, cancellationToken))
            .ToList();
        var names = decisions.Select(x => x.Subscriber).ToList();
        try
        {
            foreach (var decision in decisions)
                decision.Ask(decisionRequested);

            _logger.LogInformation(
                "Withdrawal request {RequestId} on wallet {WalletId} waits up to {Timeout} for {Interceptors} (decision {DecisionId})",
                request.Id, request.WalletId, timeout, names, decisionId);

            var remaining = decisions.Select(x => x.Result).ToList();
            while (remaining.Count > 0)
            {
                var finished = await Task.WhenAny(remaining);
                remaining.Remove(finished);
                var answer = await finished;
                if (!answer.Allow)
                {
                    return new WithdrawalInterceptOutcome(WithdrawalInterceptOutcomeKind.Blocked, decisionId, names,
                        string.IsNullOrWhiteSpace(answer.Reason) ? "blocked by the integrator" : answer.Reason,
                        answer.Subscriber);
                }
            }

            return new WithdrawalInterceptOutcome(WithdrawalInterceptOutcomeKind.Allowed, decisionId, names);
        }
        finally
        {
            foreach (var decision in decisions)
                await decision.DisposeAsync();
        }
    }

    public bool Decide(Guid subscriptionId, string decisionId, bool allow, string? reason) =>
        _pending.TryGetValue((decisionId, subscriptionId), out var decision) && decision.Settle(allow, reason);

    /// <summary>
    /// Asks one interceptor and always ends in an answer: its own, or a block when it cannot be reached, disconnects,
    /// does not answer within the timeout, or NodeGuard stops waiting
    /// </summary>
    private sealed class InterceptorDecision : IAsyncDisposable
    {
        private readonly TaskCompletionSource<Answer> _answer = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly WithdrawalInterceptorGate _gate;
        private readonly (string DecisionId, Guid SubscriptionId) _key;
        private readonly WalletEventSubscription _interceptor;
        private readonly TimeSpan _timeout;
        private readonly CancellationToken _cancellationToken;
        private readonly List<CancellationTokenRegistration> _registrations = [];
        private CancellationTokenSource? _deadline;

        public InterceptorDecision(WithdrawalInterceptorGate gate, string decisionId,
            WalletEventSubscription interceptor, TimeSpan timeout, CancellationToken cancellationToken)
        {
            _gate = gate;
            _key = (decisionId, interceptor.Id);
            _interceptor = interceptor;
            _timeout = timeout;
            _cancellationToken = cancellationToken;
            Subscriber = interceptor.Subscriber ?? interceptor.Id.ToString();
        }

        public string Subscriber { get; }

        public Task<Answer> Result => _answer.Task;

        public void Ask(WalletEvent decisionRequested)
        {
            // Registered before sending, so an answer that comes back at once finds its decision
            _gate._pending[_key] = this;

            _deadline = new CancellationTokenSource(_timeout, _gate._timeProvider);
            _registrations.Add(_deadline.Token.Register(() =>
                Settle(false, $"no decision from the integrator within {_timeout.TotalSeconds:0}s")));
            _registrations.Add(_cancellationToken.Register(() =>
                Settle(false, "NodeGuard stopped waiting before the integrator decided")));
            _registrations.Add(_interceptor.Closed.Register(() =>
                Settle(false, "the integrator disconnected before deciding")));

            if (!_gate._hub.Send(_interceptor, decisionRequested))
                Settle(false, "the integrator could not be reached");
        }

        /// <summary>Settles the decision; false when it was already settled</summary>
        public bool Settle(bool allow, string? reason) => _answer.TrySetResult(new Answer(allow, reason, Subscriber));

        public async ValueTask DisposeAsync()
        {
            _gate._pending.TryRemove(_key, out _);
            foreach (var registration in _registrations)
                await registration.DisposeAsync();
            _deadline?.Dispose();
        }
    }
}
