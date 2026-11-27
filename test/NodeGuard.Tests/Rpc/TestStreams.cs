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

using System.Threading.Channels;
using Grpc.Core;
using Channel = System.Threading.Channels.Channel;

namespace NodeGuard.Rpc;

/// <summary>
/// Server-side response stream that records what the handler writes, so a test can await each message
/// </summary>
public class TestServerStreamWriter<T> : IServerStreamWriter<T> where T : class
{
    private readonly Channel<T> _written = Channel.CreateUnbounded<T>();

    public WriteOptions? WriteOptions { get; set; }

    public Task WriteAsync(T message)
    {
        _written.Writer.TryWrite(message);
        return Task.CompletedTask;
    }

    public async Task<T> ReadNextAsync(TimeSpan? timeout = null)
    {
        using var cts = new CancellationTokenSource(timeout ?? TimeSpan.FromSeconds(5));
        return await _written.Reader.ReadAsync(cts.Token);
    }

    /// <summary>
    /// True when nothing else was written within <paramref name="wait"/>
    /// </summary>
    public async Task<bool> NothingMoreWrittenAsync(TimeSpan? wait = null)
    {
        using var cts = new CancellationTokenSource(wait ?? TimeSpan.FromMilliseconds(200));
        try
        {
            return !await _written.Reader.WaitToReadAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
            return true;
        }
    }
}

/// <summary>
/// Server-side request stream fed by the test: <see cref="Add"/> sends a client message, <see cref="Complete"/>
/// half-closes the call
/// </summary>
public class TestAsyncStreamReader<T> : IAsyncStreamReader<T> where T : class
{
    private readonly Channel<T> _messages = Channel.CreateUnbounded<T>();

    public T Current { get; private set; } = null!;

    public void Add(T message) => _messages.Writer.TryWrite(message);

    public void Complete() => _messages.Writer.TryComplete();

    public async Task<bool> MoveNext(CancellationToken cancellationToken)
    {
        if (await _messages.Reader.WaitToReadAsync(cancellationToken) && _messages.Reader.TryRead(out var message))
        {
            Current = message;
            return true;
        }

        return false;
    }
}
