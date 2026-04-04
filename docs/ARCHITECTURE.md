# Architecture

`llama-runtime` is a single-model gRPC inference runtime with four layers:

1. Native adapter (`native/`): wraps `llama.cpp` behind a small C ABI.
2. Managed native wrapper (`LlamaRuntime.Native`): owns native loading, handle translation, and error mapping.
3. Engine (`LlamaRuntime.Engine`): owns model loading, context pooling, and inference sessions.
4. gRPC host (`LlamaRuntime.Presentation.Grpc`): owns startup/shutdown, model state, request queueing, auth, and health checks.

## Ownership and lifecycle

- The native library is loaded once per process and reused for the lifetime of the host process.
- A single hosted model is loaded at startup and unloaded during shutdown.
- Model state is explicit: `NotLoaded`, `Loading`, `Loaded`, `Failed`, `Stopping`.
- Contexts are pooled per loaded model and reset before reuse.

## Concurrency model

- Requests enter a bounded in-memory queue.
- Worker count is controlled by `Inference:WorkerCount`.
- Queue backpressure and execution timeout are controlled by `Inference:AcquireTimeout`.
- Requests can be cancelled while queued. Once native inference starts, cancellation is best-effort because `llama.cpp` inference is synchronous in this runtime.

## Runtime contract

- Only one model is hosted at a time.
- gRPC is the only serving interface.
- Readiness is healthy only when the model is fully loaded.
- Prompt budget is enforced before generation using `ContextSize - GenerationMaxNewTokens`.
- Native output that exceeds the configured managed buffer fails with a dedicated buffer-too-small error instead of silent truncation.

## Compatibility

- The repo is currently pinned to `llama.cpp` release `b7932`.
- The native adapter is intended for the pinned vendor version first; compatibility with other revisions is not guaranteed without validation.
