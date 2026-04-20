# llama-runtime

Native-first, single-model gRPC inference runtime built on top of `llama.cpp`.

## Commands

- Bootstrap vendor headers and binaries: `make init`
- Re-verify cached upstream archives and extracted vendor binaries: `make verify`
- Build the native adapter: `make native-build`
- Run native integration tests (requires a real GGUF at `MODEL_PATH`): `make native-integration-tests`
- Build and package the runtime into `dist/`: `make pack`
- Run the packaged gRPC runtime locally: `HostedModel__ModelPath=/absolute/path/to/model.gguf make llama-runtime-grpc-run`
- Run managed tests: `dotnet test src/llama-runtime.slnx --no-restore`
- Benchmark the gRPC runtime: `make bench-llama-runtime-grpc`
- Run the upstream `llama.cpp` REST baseline: `make run-llama-rest-server`
- Benchmark the REST baseline: `make bench-llama-rest`

## Environment And Build Notes

- The `Makefile` fails fast if neither `.env` nor the selected platform env file exists. This repo currently includes `.env.macos` and `.env.example`.
- `LLAMA_VERSION` is pinned to `b8672`. Treat compatibility with other `llama.cpp` revisions as unverified until the native adapter and integration tests have been validated.
- `make pack` publishes `src/LlamaRuntime.Presentation.Grpc` as a self-contained single-file app and then copies native libraries and license files into `dist/`.
- Keep `PublishReadyToRun` disabled by default. The repo documents a deterministic startup crash with `PublishReadyToRun=true` while loading the native `llama.cpp` stack.
- `vendor/`, `native/build/`, and `dist/` are build/download outputs. Do not edit generated contents by hand.

## Project Structure

- `native/`
  - C++ adapter layer over `llama.cpp`
  - `src/llama_adapter_core.cpp` and `include/llama_adapter_core.h`: internal OO wrapper
  - `src/llama_adapter.cpp` and `include/llama_adapter.h`: public C ABI bridge consumed by .NET
- `src/LlamaRuntime.Native.Contracts/`
  - native handles, error types, binding configuration
- `src/LlamaRuntime.Native/`
  - native library loading, P/Invoke bindings, managed error translation
- `src/LlamaRuntime.Engine.Contracts/`
  - engine-facing abstractions and configuration contracts
- `src/LlamaRuntime.Engine/`
  - model lifecycle, context pooling, inference sessions, provider orchestration
- `src/LlamaRuntime.Presentation.Grpc/`
  - ASP.NET Core gRPC host, API key auth, rate limiting, readiness/liveness, hosted services
- `src/LlamaRuntime.*.Tests/`
  - xUnit test projects for shared contracts, engine behavior, and gRPC host behavior
- `src/LlamaRuntime.Benchmarks/`
  - benchmark harnesses for the runtime and `llama.cpp` REST baseline
- `checksums/llama/`
  - pinned SHA-256 manifests for vendored upstream artifacts
- `docs/ARCHITECTURE.md`
  - concise description of layer boundaries, lifecycle, and concurrency expectations

## Runtime Invariants

- The process hosts exactly one model at a time.
- Startup loads the model, performs one warm-up inference, and only then reports readiness.
- Request admission is bounded by an in-memory channel. `Inference__AcquireTimeout` applies to queue admission, not as a hard kill timeout for native inference already in progress.
- Each request receives an isolated inference session backed by one leased context from the pool. Contexts are reset before reuse.
- Prompt budget is enforced before generation using `ContextSize - GenerationMaxNewTokens`.
- Native output that exceeds the configured managed buffer should fail explicitly instead of truncating silently.
- Structured logs should stay operationally useful without logging prompt or generated-text payloads.

## Change Guidance

- Prefer existing `Makefile` and project entry points over one-off scripts.
- Keep responsibilities separated by layer:
  - native adapter: ABI-safe interop with `llama.cpp`
  - `LlamaRuntime.Native`: library loading and managed/native translation
  - engine: model ownership, context pooling, inference sessions
  - gRPC host: transport, auth, health checks, rate limiting, queueing
- When changing configuration, wire it through the existing options classes and environment-variable naming pattern instead of introducing parallel config paths.
- When touching the native adapter, preserve explicit ownership rules and never throw across the C ABI boundary.
- When changing queueing or lifecycle code, preserve hosted model state transitions: `NotLoaded`, `Loading`, `WarmingUp`, `Loaded`, `Failed`, `Stopping`.
- When updating `llama.cpp` artifacts or version pins, update checksums, rebuild the native adapter, and validate both managed tests and native integration behavior.
- Add or update tests with code changes:
  - managed behavior: xUnit projects under `src/LlamaRuntime.*.Tests`
  - native interop/inference behavior: `make native-integration-tests` when a real model is available

## Testing Expectations

- For managed-only changes, start with `dotnet test src/llama-runtime.slnx --no-restore`.
- For changes affecting packaging, startup, or native interop, also run the relevant `make` targets.
- Native integration tests require vendored artifacts from `make init` and a valid GGUF referenced by `MODEL_PATH`.
