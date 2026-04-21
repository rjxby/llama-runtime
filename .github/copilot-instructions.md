# Repository Instructions

This repository is a native-first, single-model gRPC inference runtime built on top of `llama.cpp`. Optimize for small, correct changes that preserve the current layer boundaries and runtime contract.

## Use These Commands

- Bootstrap vendor dependencies: `make init`
- Verify pinned upstream artifacts: `make verify`
- Build the native adapter: `make native-build`
- Build/package the runtime: `make pack`
- Run managed tests: `dotnet test src/llama-runtime.slnx --no-restore`
- Run native integration tests only when a real model is available: `make native-integration-tests`

## Repo Boundaries

- `native/`: C++ adapter over pinned `llama.cpp` release `b8868`
- `src/LlamaRuntime.Native*`: native loading, handles, P/Invoke, error mapping
- `src/LlamaRuntime.Engine*`: model lifecycle, context pooling, inference sessions
- `src/LlamaRuntime.Presentation.Grpc*`: gRPC host, auth, rate limiting, health checks, bounded queueing
- `src/LlamaRuntime.Benchmarks/`: benchmark harnesses

## Invariants To Preserve

- One hosted model per process.
- Readiness is healthy only after model load and startup warm-up complete.
- `Inference__AcquireTimeout` is a queue-admission timeout, not a hard timeout for native inference already running.
- Each request uses a leased context from the pool, and contexts are reset before reuse.
- Native interop must remain ABI-safe and return explicit failures instead of silent truncation or cross-boundary exceptions.
- Keep structured logs free of prompt and generated-text payloads.
- Do not enable `PublishReadyToRun` by default; the repo documents a startup crash in that mode with the native stack.

## Change Expectations

- Prefer existing options binding and environment-variable conventions over ad hoc config.
- Do not edit generated or downloaded contents under `vendor/`, `native/build/`, or `dist/` by hand.
- When changing the pinned `llama.cpp` version or artifact layout, update checksum manifests and validate both managed and native paths.
- Add or update xUnit tests for managed behavior changes.
