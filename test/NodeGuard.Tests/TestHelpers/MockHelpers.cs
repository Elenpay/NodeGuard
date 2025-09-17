// NodeGuard
// Copyright (C) 2025  Elenpay
//
// This program is free software: you can redistribute it and/or modify
// it under the terms of the GNU Affero General Public License as published by
// the Free Software Foundation, either version 3 of the License, or
// (at your option) any later version.
//
// This program is distributed in the hope that it will be useful,
// but WITHOUT ANY WARRANTY, without even the implied warranty of
// MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
// GNU Affero General Public License for more details.
//
// You should have received a copy of the GNU Affero General Public License
// along with this program.  If not, see http://www.gnu.org/licenses/.



using Grpc.Core;
using NodeGuard.Data;
using NodeGuard.Data.Repositories.Interfaces;

namespace NodeGuard.TestHelpers;

public class MockHelpers
{
    public static AsyncUnaryCall<T> CreateAsyncUnaryCall<T>(T result)
    {
        return new AsyncUnaryCall<T>(
            Task.FromResult(result),
            Task.FromResult(new Metadata()),
            () => Status.DefaultSuccess,
            () => new Metadata(),
            () => { });
    }

    public static AsyncServerStreamingCall<T> CreateAsyncServerStreamingCall<T>(IEnumerable<T> result)
    {
        return new AsyncServerStreamingCall<T>(
            new MockAsyncStreamReader<T>(result),
            Task.FromResult(new Metadata()),
            () => Status.DefaultSuccess,
            () => new Metadata(),
            () => { });
    }

    public static Mock<IRepository<T>> GetMockRepository<T>() where T : class
    {
        var mock = new Mock<IRepository<T>>();

        mock.Setup(x => x.AddAsync(It.IsAny<T>(), It.IsAny<ApplicationDbContext>())).ReturnsAsync((true, null));
        mock.Setup(x => x.Update(It.IsAny<T>(), It.IsAny<ApplicationDbContext>())).Returns((true, null));
        mock.Setup(x => x.Remove(It.IsAny<T>(), It.IsAny<ApplicationDbContext>())).Returns((true, null));

        return mock;
    }
}

internal class MockAsyncStreamReader<T> : IAsyncStreamReader<T>
{
    private readonly IEnumerator<T> enumerator;

    public MockAsyncStreamReader(IEnumerable<T> results)
    {
        enumerator = results.GetEnumerator();
    }

    public T Current => enumerator.Current;

    public Task<bool> MoveNext(CancellationToken cancellationToken) =>
        Task.Run(() => enumerator.MoveNext());
}
