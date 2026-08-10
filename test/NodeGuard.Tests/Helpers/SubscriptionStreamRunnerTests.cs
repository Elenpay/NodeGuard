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
using Grpc.Core;
using Grpc.Core.Testing;
using Microsoft.Extensions.Logging.Abstractions;
using NodeGuard.Helpers;
using Quartz;
using Node = NodeGuard.Data.Models.Node;

namespace NodeGuard.Tests.Helpers;

public class SubscriptionStreamRunnerTests
{
    // Tiny backoff so reconnect tests don't wait real seconds.
    private static readonly TimeSpan FastBackoff = TimeSpan.FromMilliseconds(1);

    [Fact]
    public async Task RunAsync_WhenNodeNotEligible_DoesNotSubscribe()
    {
        using var cts = new CancellationTokenSource();
        var subscribeCount = 0;

        await SubscriptionStreamRunner.RunAsync<string>(
            ContextWith(cts.Token),
            NullLogger.Instance,
            "test",
            nodeId: 1,
            getEligibleNode: () => Task.FromResult<Node?>(null),
            subscribe: _ => { subscribeCount++; return StreamOf(Array.Empty<string>()); },
            handleEvent: (_, _) => Task.CompletedTask,
            invalidateClient: _ => { },
            initialBackoff: FastBackoff,
            maxBackoff: FastBackoff);

        subscribeCount.Should().Be(0);
    }

    [Fact]
    public async Task RunAsync_WhenAlreadyCancelled_DoesNothing()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var eligibleCalls = 0;

        await SubscriptionStreamRunner.RunAsync<string>(
            ContextWith(cts.Token),
            NullLogger.Instance,
            "test",
            nodeId: 1,
            getEligibleNode: () => { eligibleCalls++; return Task.FromResult<Node?>(new Node()); },
            subscribe: _ => StreamOf(Array.Empty<string>()),
            handleEvent: (_, _) => Task.CompletedTask,
            invalidateClient: _ => { },
            initialBackoff: FastBackoff,
            maxBackoff: FastBackoff);

        eligibleCalls.Should().Be(0);
    }

    [Fact]
    public async Task RunAsync_HandlesEachEvent_UntilCancelled()
    {
        using var cts = new CancellationTokenSource();
        var handled = new List<string>();
        var node = new Node { Endpoint = "host:1" };

        await SubscriptionStreamRunner.RunAsync<string>(
            ContextWith(cts.Token),
            NullLogger.Instance,
            "test",
            nodeId: 1,
            getEligibleNode: () => Task.FromResult<Node?>(node),
            subscribe: _ => StreamOf(new[] { "a", "b" }),
            handleEvent: (ev, _) =>
            {
                handled.Add(ev);
                if (ev == "b") cts.Cancel(); // stop after consuming both events
                return Task.CompletedTask;
            },
            invalidateClient: _ => { },
            initialBackoff: FastBackoff,
            maxBackoff: FastBackoff);

        handled.Should().Equal("a", "b");
    }

    [Fact]
    public async Task RunAsync_ResubscribesAfterCleanStreamEnd()
    {
        // The core fix: a stream that ends cleanly (MoveNext -> false) must resubscribe, not exit.
        using var cts = new CancellationTokenSource();
        var subscribeCount = 0;
        var handled = new List<string>();
        var node = new Node { Endpoint = "host:1" };

        await SubscriptionStreamRunner.RunAsync<string>(
            ContextWith(cts.Token),
            NullLogger.Instance,
            "test",
            nodeId: 1,
            getEligibleNode: () => Task.FromResult<Node?>(node),
            subscribe: _ =>
            {
                subscribeCount++;
                // First stream ends cleanly with no events; the second yields one.
                return subscribeCount == 1
                    ? StreamOf(Array.Empty<string>())
                    : StreamOf(new[] { "x" });
            },
            handleEvent: (ev, _) =>
            {
                handled.Add(ev);
                cts.Cancel();
                return Task.CompletedTask;
            },
            invalidateClient: _ => { },
            initialBackoff: FastBackoff,
            maxBackoff: FastBackoff);

        subscribeCount.Should().Be(2);
        handled.Should().Equal("x");
    }

    [Fact]
    public async Task RunAsync_WhenStreamThrows_InvalidatesChannelAndRetries()
    {
        using var cts = new CancellationTokenSource();
        var invalidated = new List<string?>();
        var node = new Node { Endpoint = "host:1" };
        var eligibleCalls = 0;

        await SubscriptionStreamRunner.RunAsync<string>(
            ContextWith(cts.Token),
            NullLogger.Instance,
            "test",
            nodeId: 1,
            // Run one failing subscription, then report the node ineligible to stop the loop.
            getEligibleNode: () =>
            {
                eligibleCalls++;
                return Task.FromResult(eligibleCalls == 1 ? node : null);
            },
            subscribe: _ => StreamOf<string>(Array.Empty<string>(), new InvalidOperationException("boom")),
            handleEvent: (_, _) => Task.CompletedTask,
            invalidateClient: endpoint => invalidated.Add(endpoint),
            initialBackoff: FastBackoff,
            maxBackoff: FastBackoff);

        invalidated.Should().Equal("host:1");
    }

    private static IJobExecutionContext ContextWith(CancellationToken token)
    {
        var context = new Mock<IJobExecutionContext>();
        context.Setup(c => c.CancellationToken).Returns(token);
        return context.Object;
    }

    private static AsyncServerStreamingCall<T> StreamOf<T>(IEnumerable<T> items, Exception? throwWhenExhausted = null)
        => TestCalls.AsyncServerStreamingCall(
            new FakeAsyncStreamReader<T>(items, throwWhenExhausted),
            Task.FromResult(new Metadata()),
            () => Status.DefaultSuccess,
            () => new Metadata(),
            () => { });

    private sealed class FakeAsyncStreamReader<T> : IAsyncStreamReader<T>
    {
        private readonly IEnumerator<T> _items;
        private readonly Exception? _throwWhenExhausted;

        public FakeAsyncStreamReader(IEnumerable<T> items, Exception? throwWhenExhausted)
        {
            _items = items.GetEnumerator();
            _throwWhenExhausted = throwWhenExhausted;
        }

        public T Current { get; private set; } = default!;

        public Task<bool> MoveNext(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (_items.MoveNext())
            {
                Current = _items.Current;
                return Task.FromResult(true);
            }

            if (_throwWhenExhausted != null)
            {
                throw _throwWhenExhausted;
            }

            return Task.FromResult(false);
        }
    }
}
