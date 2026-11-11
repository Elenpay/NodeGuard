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
using Nodeguard;
using NSubstitute;

namespace NodeGuard.Services;

public class WalletEventHubTests
{
    private readonly CancellationTokenSource _stopping = new();

    private WalletEventHub CreateHub(int capacity = WalletEventHub.SubscriberCapacity)
    {
        var lifetime = Substitute.For<IHostApplicationLifetime>();
        lifetime.ApplicationStopping.Returns(_stopping.Token);
        return new WalletEventHub(NullLogger<WalletEventHub>.Instance, lifetime, capacity);
    }

    private static WalletEvent Event(int walletId, int requestId = 1) => new()
    {
        Type = WALLET_EVENT_TYPE.WalletEventWithdrawalCreated,
        WalletId = walletId,
        Withdrawal = new WithdrawalEvent { RequestId = requestId }
    };

    [Fact]
    public void Publish_ReachesEverySubscriberOfThatWalletOnly()
    {
        var hub = CreateHub();
        using var first = hub.Subscribe(1);
        using var second = hub.Subscribe(1);
        using var otherWallet = hub.Subscribe(2);

        hub.Publish(Event(1));

        first.Reader.TryRead(out var a).Should().BeTrue();
        second.Reader.TryRead(out var b).Should().BeTrue();
        a!.WalletId.Should().Be(1);
        b!.WalletId.Should().Be(1);
        otherWallet.Reader.TryRead(out _).Should().BeFalse();
    }

    [Fact]
    public void Publish_WithoutSubscribers_IsANoOp()
    {
        var hub = CreateHub();

        var act = () => hub.Publish(Event(1));

        act.Should().NotThrow();
        hub.HasSubscribers(1).Should().BeFalse();
    }

    [Fact]
    public void Dispose_Unsubscribes()
    {
        var hub = CreateHub();
        var subscription = hub.Subscribe(1);
        hub.HasSubscribers(1).Should().BeTrue();

        subscription.Dispose();

        hub.HasSubscribers(1).Should().BeFalse();
        subscription.Reader.Completion.IsCompleted.Should().BeTrue();
    }

    [Fact]
    public async Task Publish_SubscriberThatFallsBehind_IsDisconnectedWithoutAffectingTheOthers()
    {
        var hub = CreateHub(capacity: 2);
        using var slow = hub.Subscribe(1);
        using var fast = hub.Subscribe(1);

        for (var i = 1; i <= 3; i++)
        {
            hub.Publish(Event(1, i));
            fast.Reader.TryRead(out var received).Should().BeTrue();
            received!.Withdrawal.RequestId.Should().Be(i);
        }

        // The two buffered events are still delivered, then the disconnection surfaces
        var buffered = new List<int>();
        var act = async () =>
        {
            await foreach (var e in slow.Reader.ReadAllAsync())
                buffered.Add(e.Withdrawal.RequestId);
        };
        await act.Should().ThrowAsync<WalletEventSubscriberTooSlowException>();
        buffered.Should().Equal(1, 2);

        hub.HasSubscribers(1).Should().BeTrue("the subscriber that kept up stays subscribed");
        hub.Publish(Event(1, 4));
        fast.Reader.TryRead(out var fourth).Should().BeTrue();
        fourth!.Withdrawal.RequestId.Should().Be(4);
    }

    [Fact]
    public async Task ApplicationStopping_ClosesEverySubscription()
    {
        var hub = CreateHub();
        using var first = hub.Subscribe(1);
        using var second = hub.Subscribe(2);

        await _stopping.CancelAsync();

        await first.Invoking(s => s.Reader.Completion).Should().ThrowAsync<WalletEventHubStoppingException>();
        await second.Invoking(s => s.Reader.Completion).Should().ThrowAsync<WalletEventHubStoppingException>();
        hub.HasSubscribers(1).Should().BeFalse();
        hub.HasSubscribers(2).Should().BeFalse();
    }
}
