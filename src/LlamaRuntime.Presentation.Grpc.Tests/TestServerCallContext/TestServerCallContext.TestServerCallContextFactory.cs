using Grpc.Core;

namespace LlamaRuntime.Presentation.Grpc.Tests;

internal static partial class TestServerCallContext
{
    private sealed class TestServerCallContextFactory : ServerCallContext
    {
        private Metadata _responseTrailers = new();
        private WriteOptions? _writeOptions;
        private Status _status;

        public static ServerCallContext Create(CancellationToken cancellationToken) =>
            new TestServerCallContextFactory(cancellationToken);

        private TestServerCallContextFactory(CancellationToken cancellationToken)
        {
            CancellationTokenCore = cancellationToken;
        }

        protected override string MethodCore => "llama.v2.Generator/Generate";

        protected override string HostCore => "localhost";

        protected override string PeerCore => "127.0.0.1";

        protected override DateTime DeadlineCore => DateTime.UtcNow.AddMinutes(1);

        protected override Metadata RequestHeadersCore => new();

        protected override CancellationToken CancellationTokenCore { get; }

        protected override Metadata ResponseTrailersCore => _responseTrailers;

        protected override Status StatusCore
        {
            get => _status;
            set => _status = value;
        }

        protected override WriteOptions? WriteOptionsCore
        {
            get => _writeOptions;
            set => _writeOptions = value;
        }

        protected override AuthContext AuthContextCore => new(string.Empty, new Dictionary<string, List<AuthProperty>>());

        protected override ContextPropagationToken CreatePropagationTokenCore(ContextPropagationOptions? options) =>
            throw new NotSupportedException();

        protected override Task WriteResponseHeadersAsyncCore(Metadata responseHeaders) => Task.CompletedTask;
    }
}
