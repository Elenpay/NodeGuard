using Grpc.Core;

namespace FundsManager.TestHelpers;

public class MockHelpers
{
    public static AsyncUnaryCall<T> CreateUnaryCall<T>(T result)
    {
        return new AsyncUnaryCall<T>(
            Task.FromResult(result),
            Task.FromResult(new Metadata()),
            () => Status.DefaultSuccess,
            () => new Metadata(),
            () => { });
    }
}