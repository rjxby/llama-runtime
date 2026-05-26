using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

public class TestWebApplicationFactory : WebApplicationFactory<Program>
{
    public const string ApiKey = "dev-key-123";
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.ConfigureAppConfiguration((context, cfg) =>
        {
            cfg.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ApiKeys:Keys:0"] = ApiKey,
                ["HostedModel:ModelPath"] = "test-model.gguf",
                ["HostedModel:ModelId"] = "test-model",
                ["Llama:Native:ContextSize"] = "32",
                ["Llama:Native:BatchSize"] = "8",
                ["Llama:Native:GenerationMaxNewTokens"] = "8",
                ["Inference:ChannelCapacity"] = "4",
                ["Inference:WorkerCount"] = "2",
                ["Inference:AcquireTimeout"] = "00:00:01"
            });
        });
        builder.ConfigureServices(services =>
        {
            var nativeMock = new Moq.Mock<LlamaRuntime.Native.Contracts.ILlamaNative>();
            // Add basic setup to avoid null refs
            nativeMock.Setup(x => x.LoadModel(Moq.It.IsAny<string>()))
                .Returns(LlamaRuntime.Native.Contracts.LlamaModelHandle.FromIntPtr(new IntPtr(1)));
            nativeMock.Setup(x => x.GetModelMetadata(Moq.It.IsAny<LlamaRuntime.Native.Contracts.LlamaModelHandle>()))
                .Returns(new LlamaRuntime.Native.Contracts.NativeModelMetadata(
                    32,
                    LlamaRuntime.Native.Contracts.NativeTokenizerType.SentencePiece));
            nativeMock.Setup(x => x.CreateContext(Moq.It.IsAny<LlamaRuntime.Native.Contracts.LlamaModelHandle>()))
                .Returns(LlamaRuntime.Native.Contracts.LlamaContextHandle.FromIntPtr(new IntPtr(1)));
            nativeMock.Setup(x => x.GetContextMetadata(Moq.It.IsAny<LlamaRuntime.Native.Contracts.LlamaContextHandle>()))
                .Returns(new LlamaRuntime.Native.Contracts.NativeContextMetadata(32));
            nativeMock.Setup(x => x.CountTokens(Moq.It.IsAny<LlamaRuntime.Native.Contracts.LlamaContextHandle>(), Moq.It.IsAny<string>()))
                .Returns<LlamaRuntime.Native.Contracts.LlamaContextHandle, string>((_, prompt) => prompt.Length);
            nativeMock.Setup(x => x.Infer(
                    Moq.It.IsAny<LlamaRuntime.Native.Contracts.LlamaContextHandle>(),
                    Moq.It.IsAny<string>(),
                    Moq.It.IsAny<LlamaRuntime.Native.Contracts.NativeInferenceResponseFormat>(),
                    Moq.It.IsAny<string?>()))
                .Returns<LlamaRuntime.Native.Contracts.LlamaContextHandle, string, LlamaRuntime.Native.Contracts.NativeInferenceResponseFormat, string?>((_, prompt, responseFormat, _) =>
                {
                    if (prompt.Length > 24)
                    {
                        throw new LlamaRuntime.Native.Contracts.NativeInvalidArgumentException("too long");
                    }

                    var content = responseFormat == LlamaRuntime.Native.Contracts.NativeInferenceResponseFormat.Grammar
                        ? """{"ok":true}"""
                        : "mocked response";

                    return new LlamaRuntime.Native.Contracts.NativeInferenceResult(
                        content,
                        prompt.Length,
                        content.Length,
                        prompt.Length + content.Length);
                });

            services.RemoveAll<LlamaRuntime.Native.Contracts.ILlamaNative>();
            services.AddSingleton(nativeMock.Object);
        });
    }
}
