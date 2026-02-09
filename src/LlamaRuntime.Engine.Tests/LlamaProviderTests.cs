using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;

using LlamaRuntime.Engine.Contracts;
using LlamaRuntime.Engine.Contracts.Configuration;
using LlamaRuntime.Native.Contracts;
using LlamaRuntime.Common.Tests;

namespace LlamaRuntime.Engine.Tests;

[Trait(TestCategories.Name, TestCategories.Unit)]
public sealed class LlamaProviderTests
{
      private static LlamaModelHandle CreateModelHandle() =>
          LlamaModelHandle.FromIntPtr(new IntPtr(1));

      private static LlamaContextHandle CreateContextHandle(int id) =>
          LlamaContextHandle.FromIntPtr(new IntPtr(id));

      private static LlamaProvider CreateProvider(
          Mock<ILlamaNative> nativeMock,
          int poolSize = 2)
      {
            var options = Options.Create(new LlamaProviderOptions
            {
                  DefaultPoolSize = poolSize
            });

            return new LlamaProvider(
                nativeMock.Object,
                options,
                NullLogger<LlamaProvider>.Instance);
      }

      [Fact]
      public async Task LoadModelAsync_Returns_Model()
      {
            var native = new Mock<ILlamaNative>();
            native.Setup(n => n.LoadModel(It.IsAny<string>()))
                  .Returns(CreateModelHandle());

            using var provider = CreateProvider(native);

            var model = await provider.LoadModelAsync("model.gguf");

            Assert.NotNull(model);
            Assert.Equal("model.gguf", model.Id);
            native.Verify(n => n.LoadModel("model.gguf"), Times.Once);
      }

      [Fact]
      public async Task LoadModelAsync_NativeFailure_Throws_ModelLoadException()
      {
            var native = new Mock<ILlamaNative>();
            native.Setup(n => n.LoadModel(It.IsAny<string>()))
                  .Throws(new NativeLoadModelException("fail"));

            using var provider = CreateProvider(native);

            await Assert.ThrowsAsync<ModelLoadException>(() =>
                provider.LoadModelAsync("model.gguf"));
      }

      [Fact]
      public async Task InferAsync_Uses_Native_Infer()
      {
            var modelHandle = CreateModelHandle();
            var ctxHandle = CreateContextHandle(1);

            var native = new Mock<ILlamaNative>();
            native.Setup(n => n.LoadModel(It.IsAny<string>()))
                  .Returns(modelHandle);
            native.Setup(n => n.CreateContext(modelHandle))
                  .Returns(ctxHandle);
            native.Setup(n => n.Infer(modelHandle, ctxHandle, "hi"))
                  .Returns("ok");

            using var provider = CreateProvider(native, poolSize: 1);
            var model = await provider.LoadModelAsync("model");

            var result = await provider.InferAsync(model, "hi");

            Assert.Equal("ok", result);
            native.Verify(n => n.Infer(modelHandle, ctxHandle, "hi"), Times.Once);
      }

      [Fact]
      public async Task InferAsync_Context_Is_Reused()
      {
            var modelHandle = CreateModelHandle();
            var ctxHandle = CreateContextHandle(1);

            var native = new Mock<ILlamaNative>();
            native.Setup(n => n.LoadModel(It.IsAny<string>()))
                  .Returns(modelHandle);
            native.Setup(n => n.CreateContext(modelHandle))
                  .Returns(ctxHandle);
            native.Setup(n => n.Infer(It.IsAny<LlamaModelHandle>(), It.IsAny<LlamaContextHandle>(), It.IsAny<string>()))
                  .Returns("ok");

            using var provider = CreateProvider(native, poolSize: 1);
            var model = await provider.LoadModelAsync("model");

            await provider.InferAsync(model, "a");
            await provider.InferAsync(model, "b");

            native.Verify(n => n.CreateContext(modelHandle), Times.Once);
      }

      [Fact]
      public async Task InferAsync_Concurrent_Requests_Do_Not_Share_Context()
      {
            var modelHandle = CreateModelHandle();
            var contexts = new Queue<LlamaContextHandle>(new[]
            {
                CreateContextHandle(1),
                CreateContextHandle(2)
            });

            var native = new Mock<ILlamaNative>();
            native.Setup(n => n.LoadModel(It.IsAny<string>()))
                  .Returns(modelHandle);
            native.Setup(n => n.CreateContext(modelHandle))
                  .Returns(() => contexts.Dequeue());
            native.Setup(n => n.Infer(It.IsAny<LlamaModelHandle>(), It.IsAny<LlamaContextHandle>(), It.IsAny<string>()))
                  .Returns("ok");

            using var provider = CreateProvider(native, poolSize: 2);
            var model = await provider.LoadModelAsync("model");

            var tasks = Enumerable.Range(0, 2)
                .Select(_ => provider.InferAsync(model, "hi"))
                .ToArray();

            await Task.WhenAll(tasks);

            native.Verify(n => n.CreateContext(modelHandle), Times.Exactly(2));
      }

      [Fact]
      public async Task InferAsync_When_Model_Not_Loaded_Throws()
      {
            var native = new Mock<ILlamaNative>();
            using var provider = CreateProvider(native);

            var fakeModel = Mock.Of<IEngineModel>(m => m.Id == "x");

            await Assert.ThrowsAsync<ModelNotFoundException>(() =>
                provider.InferAsync(fakeModel, "hi"));
      }

      [Fact]
      public async Task InferAsync_NativeFailure_Throws_InferenceException()
      {
            var modelHandle = CreateModelHandle();
            var ctxHandle = CreateContextHandle(1);

            var native = new Mock<ILlamaNative>();
            native.Setup(n => n.LoadModel(It.IsAny<string>()))
                  .Returns(modelHandle);
            native.Setup(n => n.CreateContext(modelHandle))
                  .Returns(ctxHandle);
            native.Setup(n => n.Infer(It.IsAny<LlamaModelHandle>(), It.IsAny<LlamaContextHandle>(), It.IsAny<string>()))
                  .Throws(new NativeInferException("fail"));

            using var provider = CreateProvider(native);
            var model = await provider.LoadModelAsync("model");

            await Assert.ThrowsAsync<InferenceException>(() =>
                provider.InferAsync(model, "hi"));
      }

      [Fact]
      public async Task InferAsync_Cancellation_While_Waiting_For_Context_Throws()
      {
            var modelHandle = CreateModelHandle();
            var ctxHandle = CreateContextHandle(1);

            var native = new Mock<ILlamaNative>();
            native.Setup(n => n.LoadModel(It.IsAny<string>()))
                  .Returns(modelHandle);
            native.Setup(n => n.CreateContext(modelHandle))
                  .Returns(ctxHandle);
            native.Setup(n => n.Infer(It.IsAny<LlamaModelHandle>(), It.IsAny<LlamaContextHandle>(), It.IsAny<string>()))
                  .Returns("ok");

            using var provider = CreateProvider(native, poolSize: 1);
            var model = await provider.LoadModelAsync("model");

            var cts = new CancellationTokenSource();
            var first = provider.InferAsync(model, "a");

            cts.Cancel();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                provider.InferAsync(model, "b", cts.Token));

            await first;
      }

      [Fact]
      public async Task UnloadModelAsync_Removes_Model_From_Provider()
      {
            var modelHandle = CreateModelHandle();

            var native = new Mock<ILlamaNative>();
            native.Setup(n => n.LoadModel(It.IsAny<string>()))
                  .Returns(modelHandle);

            using var provider = CreateProvider(native);
            var model = await provider.LoadModelAsync("model");

            await provider.UnloadModelAsync(model);

            await Assert.ThrowsAsync<ModelNotFoundException>(() =>
                provider.InferAsync(model, "hi"));
      }

      [Fact]
      public async Task Dispose_Provider_Disposes_Models()
      {
            var modelHandle = CreateModelHandle();
            var native = new Mock<ILlamaNative>();

            native.Setup(n => n.LoadModel(It.IsAny<string>()))
                  .Returns(modelHandle);

            var provider = CreateProvider(native);
            var model = await provider.LoadModelAsync("model");

            provider.Dispose();

            await Assert.ThrowsAsync<ObjectDisposedException>(() =>
                provider.InferAsync(model, "hi"));
      }
}
