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
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using NodeGuard.Data.Models;
using Nodeguard;
using NSubstitute;
using Wallet = NodeGuard.Data.Models.Wallet;

namespace NodeGuard.Services;

public class WithdrawalInterceptorGateTests
{
    private const int WalletId = 9;

    private readonly WalletEventHub _hub;

    public WithdrawalInterceptorGateTests()
    {
        var lifetime = Substitute.For<IHostApplicationLifetime>();
        lifetime.ApplicationStopping.Returns(CancellationToken.None);
        _hub = new WalletEventHub(NullLogger<WalletEventHub>.Instance, lifetime);
    }

    private WithdrawalInterceptorGate Gate(TimeSpan? timeout = null) =>
        new(_hub, TimeProvider.System, NullLogger<WithdrawalInterceptorGate>.Instance, timeout ?? TimeSpan.FromSeconds(10));

    private static WalletWithdrawalRequest Request() => new()
    {
        Id = 77,
        WalletId = WalletId,
        Status = WalletWithdrawalRequestStatus.PSBTSignaturesPending,
        Wallet = new Wallet { Id = WalletId, IsHotWallet = true },
        WalletWithdrawalRequestDestinations = [new() { Address = "bcrt1qdest", Amount = 0.01m }]
    };

    /// <summary>Waits for the decision request the gate sends to this interceptor</summary>
    private static async Task<WalletEvent> DecisionRequestedAsync(WalletEventSubscription interceptor)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var e = await interceptor.Reader.ReadAsync(cts.Token);
        e.Type.Should().Be(WALLET_EVENT_TYPE.WalletEventWithdrawalDecisionRequested);
        return e;
    }

    [Fact]
    public async Task RequestDecision_NoInterceptor_IsSkippedAndObserversAreNotAsked()
    {
        using var observer = _hub.Subscribe(WalletId, "observer");

        var outcome = await Gate().RequestDecisionAsync(Request());

        outcome.Kind.Should().Be(WithdrawalInterceptOutcomeKind.Skipped);
        observer.Reader.TryRead(out _).Should().BeFalse();
    }

    [Fact]
    public async Task RequestDecision_InterceptorAllows_IsAllowed()
    {
        using var interceptor = _hub.Subscribe(WalletId, "integrator", interceptWithdrawals: true);
        var gate = Gate();

        var pending = gate.RequestDecisionAsync(Request());
        var asked = await DecisionRequestedAsync(interceptor);
        asked.Withdrawal.RequestId.Should().Be(77);
        asked.Withdrawal.AmountSats.Should().Be(1_000_000);
        asked.Withdrawal.Destinations.Should().ContainSingle(d => d.Address == "bcrt1qdest");
        asked.Withdrawal.HasDecisionDeadlineUnixMs.Should().BeTrue();

        gate.Decide(interceptor.Id, asked.Withdrawal.DecisionId, allow: true, reason: null).Should().BeTrue();

        var outcome = await pending;
        outcome.Kind.Should().Be(WithdrawalInterceptOutcomeKind.Allowed);
        outcome.DecisionId.Should().Be(asked.Withdrawal.DecisionId);
        outcome.Interceptors.Should().Equal("integrator");
    }

    [Fact]
    public async Task RequestDecision_AnyInterceptorBlocks_IsBlockedWithItsReason()
    {
        using var allowing = _hub.Subscribe(WalletId, "treasury", interceptWithdrawals: true);
        using var blocking = _hub.Subscribe(WalletId, "compliance", interceptWithdrawals: true);
        var gate = Gate();

        var pending = gate.RequestDecisionAsync(Request());
        var askedAllowing = await DecisionRequestedAsync(allowing);
        var askedBlocking = await DecisionRequestedAsync(blocking);
        askedBlocking.Withdrawal.DecisionId.Should().Be(askedAllowing.Withdrawal.DecisionId);

        gate.Decide(allowing.Id, askedAllowing.Withdrawal.DecisionId, true, null);
        gate.Decide(blocking.Id, askedBlocking.Withdrawal.DecisionId, false, "sanctioned address");

        var outcome = await pending;
        outcome.Kind.Should().Be(WithdrawalInterceptOutcomeKind.Blocked);
        outcome.Reason.Should().Be("sanctioned address");
        outcome.DecidedBy.Should().Be("compliance");
    }

    [Fact]
    public async Task RequestDecision_NoAnswerInTime_IsBlocked()
    {
        using var interceptor = _hub.Subscribe(WalletId, "sleepy", interceptWithdrawals: true);

        var outcome = await Gate(TimeSpan.FromMilliseconds(200)).RequestDecisionAsync(Request());

        outcome.Kind.Should().Be(WithdrawalInterceptOutcomeKind.Blocked);
        outcome.Reason.Should().Contain("no decision");
        outcome.DecidedBy.Should().Be("sleepy");
    }

    [Fact]
    public async Task RequestDecision_InterceptorLeavesBeforeAnswering_IsBlocked()
    {
        var interceptor = _hub.Subscribe(WalletId, "flaky", interceptWithdrawals: true);
        var gate = Gate();

        var pending = gate.RequestDecisionAsync(Request());
        await DecisionRequestedAsync(interceptor);
        interceptor.Dispose();

        var outcome = await pending.WaitAsync(TimeSpan.FromSeconds(5));
        outcome.Kind.Should().Be(WithdrawalInterceptOutcomeKind.Blocked);
        outcome.Reason.Should().Contain("disconnected");
        outcome.DecidedBy.Should().Be("flaky");
    }

    [Fact]
    public async Task RequestDecision_Cancelled_IsBlocked()
    {
        using var interceptor = _hub.Subscribe(WalletId, "integrator", interceptWithdrawals: true);
        using var cts = new CancellationTokenSource();

        var pending = Gate().RequestDecisionAsync(Request(), cts.Token);
        await DecisionRequestedAsync(interceptor);
        await cts.CancelAsync();

        var outcome = await pending.WaitAsync(TimeSpan.FromSeconds(5));
        outcome.Kind.Should().Be(WithdrawalInterceptOutcomeKind.Blocked);
        outcome.Reason.Should().Contain("stopped waiting");
    }

    [Fact]
    public async Task Decide_OnlyMatchesTheSubscriptionThatWasAsked_AndOnlyOnce()
    {
        using var interceptor = _hub.Subscribe(WalletId, "integrator", interceptWithdrawals: true);
        using var stranger = _hub.Subscribe(WalletId + 1, "other wallet", interceptWithdrawals: true);
        var gate = Gate();

        var pending = gate.RequestDecisionAsync(Request());
        var decisionId = (await DecisionRequestedAsync(interceptor)).Withdrawal.DecisionId;

        gate.Decide(interceptor.Id, "not-a-decision", true, null).Should().BeFalse();
        gate.Decide(stranger.Id, decisionId, true, null).Should().BeFalse();
        gate.Decide(interceptor.Id, decisionId, true, null).Should().BeTrue();
        (await pending).Kind.Should().Be(WithdrawalInterceptOutcomeKind.Allowed);
        gate.Decide(interceptor.Id, decisionId, false, "too late").Should().BeFalse("the decision is closed");
    }
}
