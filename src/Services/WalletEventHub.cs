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
using System.Threading.Channels;
using Nodeguard;

namespace NodeGuard.Services;

/// <summary>
/// The subscriber could not keep up: its buffer filled, so it was dropped rather than block the publisher
/// </summary>
public class WalletEventSubscriberTooSlowException(int capacity)
    : Exception($"The subscriber fell {capacity} events behind and was disconnected; resubscribe and reconcile");

/// <summary>
/// NodeGuard is shutting down and closed every subscription
/// </summary>
public class WalletEventHubStoppingException() : Exception("NodeGuard is shutting down; resubscribe");

/// <summary>
/// One open subscription to a wallet's events. Dispose it to unsubscribe.
/// </summary>
public sealed class WalletEventSubscription : IDisposable
{
    private readonly Channel<WalletEvent> _channel;
    private readonly Action<WalletEventSubscription> _unsubscribe;
    private readonly CancellationTokenSource _closed = new();

    internal WalletEventSubscription(int walletId, string? subscriber, bool interceptWithdrawals, int capacity,
        Action<WalletEventSubscription> unsubscribe)
    {
        WalletId = walletId;
        Subscriber = subscriber;
        InterceptWithdrawals = interceptWithdrawals;
        _unsubscribe = unsubscribe;
        _channel = Channel.CreateBounded<WalletEvent>(new BoundedChannelOptions(capacity)
        {
            SingleReader = true,
            FullMode = BoundedChannelFullMode.Wait
        });
    }

    public Guid Id { get; } = Guid.NewGuid();

    public int WalletId { get; }

    /// <summary>
    /// Who subscribed (e.g. the API token name), for logs
    /// </summary>
    public string? Subscriber { get; }

    /// <summary>
    /// The subscriber decides on this wallet's withdrawals before they are signed and broadcast
    /// </summary>
    public bool InterceptWithdrawals { get; }

    public ChannelReader<WalletEvent> Reader => _channel.Reader;

    /// <summary>
    /// Cancelled once the subscription ends, whatever the reason
    /// </summary>
    public CancellationToken Closed => _closed.Token;

    internal bool TryWrite(WalletEvent walletEvent) => _channel.Writer.TryWrite(walletEvent);

    internal void Complete(Exception? error = null)
    {
        _channel.Writer.TryComplete(error);
        try
        {
            _closed.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }
    }

    public void Dispose()
    {
        _unsubscribe(this);
        Complete();
    }
}

/// <summary>
/// In-process fan-out of wallet events to the gRPC streams subscribed to each wallet. Live only: nothing is stored,
/// and an event published while nobody listens is dropped.
/// </summary>
public interface IWalletEventHub
{
    WalletEventSubscription Subscribe(int walletId, string? subscriber = null, bool interceptWithdrawals = false);

    bool HasSubscribers(int walletId);

    /// <summary>
    /// The open subscriptions that intercept the wallet's withdrawals
    /// </summary>
    IReadOnlyList<WalletEventSubscription> GetInterceptors(int walletId);

    /// <summary>
    /// Hands the event to one subscriber only, as <see cref="Publish"/> does; false when that subscriber was full and
    /// got disconnected, or is gone
    /// </summary>
    bool Send(WalletEventSubscription subscription, WalletEvent walletEvent);

    /// <summary>
    /// Hands the event to every subscriber of its wallet without blocking: a subscriber whose buffer is full is
    /// disconnected with <see cref="WalletEventSubscriberTooSlowException"/>
    /// </summary>
    void Publish(WalletEvent walletEvent);
}

public class WalletEventHub : IWalletEventHub
{
    public const int SubscriberCapacity = 1024;

    private readonly ConcurrentDictionary<int, ConcurrentDictionary<Guid, WalletEventSubscription>> _subscriptions = new();
    private readonly ILogger<WalletEventHub> _logger;
    private readonly int _capacity;

    public WalletEventHub(ILogger<WalletEventHub> logger, IHostApplicationLifetime lifetime)
        : this(logger, lifetime, SubscriberCapacity)
    {
    }

    internal WalletEventHub(ILogger<WalletEventHub> logger, IHostApplicationLifetime lifetime, int capacity)
    {
        _logger = logger;
        _capacity = capacity;
        lifetime.ApplicationStopping.Register(CompleteAll);
    }

    public WalletEventSubscription Subscribe(int walletId, string? subscriber = null, bool interceptWithdrawals = false)
    {
        var subscription = new WalletEventSubscription(walletId, subscriber, interceptWithdrawals, _capacity, Unsubscribe);
        _subscriptions.GetOrAdd(walletId, _ => new ConcurrentDictionary<Guid, WalletEventSubscription>())
            [subscription.Id] = subscription;

        _logger.LogInformation(
            "Wallet events: {Subscriber} subscribed to wallet {WalletId} ({SubscriptionId}), intercepting withdrawals: {Intercept}",
            subscriber, walletId, subscription.Id, interceptWithdrawals);

        return subscription;
    }

    public bool HasSubscribers(int walletId) =>
        _subscriptions.TryGetValue(walletId, out var subscriptions) && !subscriptions.IsEmpty;

    public IReadOnlyList<WalletEventSubscription> GetInterceptors(int walletId) =>
        _subscriptions.TryGetValue(walletId, out var subscriptions)
            ? subscriptions.Values.Where(x => x.InterceptWithdrawals).ToList()
            : [];

    public void Publish(WalletEvent walletEvent)
    {
        if (!_subscriptions.TryGetValue(walletEvent.WalletId, out var subscriptions))
            return;

        foreach (var subscription in subscriptions.Values)
            Send(subscription, walletEvent);
    }

    public bool Send(WalletEventSubscription subscription, WalletEvent walletEvent)
    {
        if (subscription.TryWrite(walletEvent))
            return true;

        // Already closed (unsubscribed, dropped, shutting down): nothing to disconnect
        if (subscription.Closed.IsCancellationRequested)
            return false;

        _logger.LogWarning(
            "Wallet events: {Subscriber} on wallet {WalletId} ({SubscriptionId}) is {Capacity} events behind; disconnecting it",
            subscription.Subscriber, subscription.WalletId, subscription.Id, _capacity);
        Unsubscribe(subscription);
        subscription.Complete(new WalletEventSubscriberTooSlowException(_capacity));
        return false;
    }

    private void Unsubscribe(WalletEventSubscription subscription)
    {
        if (_subscriptions.TryGetValue(subscription.WalletId, out var subscriptions)
            && subscriptions.TryRemove(subscription.Id, out _))
        {
            _logger.LogInformation("Wallet events: {Subscriber} unsubscribed from wallet {WalletId} ({SubscriptionId})",
                subscription.Subscriber, subscription.WalletId, subscription.Id);
        }
    }

    private void CompleteAll()
    {
        foreach (var subscription in _subscriptions.Values.SelectMany(x => x.Values).ToList())
        {
            Unsubscribe(subscription);
            subscription.Complete(new WalletEventHubStoppingException());
        }
    }
}
