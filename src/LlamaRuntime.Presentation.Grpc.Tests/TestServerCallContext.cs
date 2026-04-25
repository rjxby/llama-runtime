using Grpc.Core;

namespace LlamaRuntime.Presentation.Grpc.Tests;

internal static partial class TestServerCallContext
{
    public static ServerCallContext Create(CancellationToken cancellationToken = default) =>
        TestServerCallContextFactory.Create(cancellationToken);
}
