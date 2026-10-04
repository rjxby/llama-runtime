# Change rules

- Make each change as small and simple as possible while solving the selected problem completely.
- Prefer removing unnecessary code, branches, state, and dependencies over adding abstractions.
- Reuse the existing design. Do not rearchitect the project to fix a local problem.
- Work on one user-selected concern at a time. A saved finding or plan is future work, not authorization to implement it.
- Keep unrelated refactors, renames, formatting, dependency upgrades, and documentation rewrites out of the change.
- Add a helper, interface, service, project, or dependency only when the selected problem needs it and existing code cannot solve it more simply.
- Preserve existing behavior and public contracts unless the selected task requires changing them.
- If a local fix proves insufficient, explain the concrete reason and agree on the larger scope before expanding the work.
- Use focused verification that proves the fix. Avoid tests that only mirror the implementation.
- In the result, explain what changed, what was removed, and how it was verified.

# Writing

Use the `unslop` skill to review user-facing prose. Preserve technical accuracy, citations, required formatting, and code syntax.

# Code style

- Use multiline braces for every `if`/`else`, loop, `lock`, `try`, `catch`, and `finally` body in hand-written C# and C++, including tests. Keep conventional `else if` chains with braced branch bodies.
- Keep expression-bodied methods, properties, and compact constructors.
- Remove comments that repeat names or statements. Retain comments that explain ordering, ownership, thread safety, or nonthrowing cleanup.
- Keep each documented contract in one place and link to it elsewhere. Put change history and review explanations in commit or PR descriptions.
- Exclude generated code and vendored dependencies from this cleanup.
- Verify C# braces without changing files: `dotnet format style src/llama-runtime.slnx --verify-no-changes --diagnostics IDE0011 --no-restore`.

# llama-runtime

Native-first, single-model gRPC inference runtime built on top of `llama.cpp`.

## Commands

- Bootstrap vendor headers and binaries: `make init`
- Change the supported `llama.cpp` pin and regenerate the pinned manifest: `make pin-llama LLAMA_VERSION=bNNNN`
- Re-verify cached upstream archives and extracted vendor binaries: `make verify`
- Build the native adapter: `make native-build`
- Run native integration tests (requires a real GGUF at `MODEL_PATH`): `make native-integration-tests`
- Build and package the runtime into `dist/`: `make pack`
- Run the packaged gRPC runtime locally: `HostedModel__ModelPath=/absolute/path/to/model.gguf make llama-runtime-grpc-run`
- Run managed tests: `dotnet test src/llama-runtime.slnx --no-restore`
- Run managed quality checks: `make check`
- Benchmark the gRPC runtime: `make bench-llama-runtime-grpc`
- Run the upstream `llama.cpp` REST baseline: `make run-llama-rest-server`
- Benchmark the REST baseline: `make bench-llama-rest`

## Environment And Build Notes

- The `Makefile` fails fast if neither `.env` nor the selected platform env file exists. This repo currently includes `.env.macos` and `.env.example`.
- `LLAMA_VERSION` is pinned to `b10964`. Treat compatibility with other `llama.cpp` revisions as unverified until the native adapter and integration tests have been validated.
- Maintain exactly one supported upstream revision at a time. Use `make pin-llama LLAMA_VERSION=bNNNN` when intentionally changing that pin.
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
- `docs/architecture.md`
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

- Read `docs/architecture.md` and `native/README.md` before changing native ownership, interop, or concurrency.
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

- Run the documented verification commands directly. Do not recreate existing checks with ad hoc Python or shell scripts. If a required check is missing, explain the gap before adding tooling.
- Run focused checks while iterating, then run the required checks once at the end. Repeat them only after further changes or when investigating a failure.
- Restore managed dependencies once with `dotnet restore src/llama-runtime.slnx`. Run the relevant test project or class while iterating, then run `make check` before finishing code changes. See [managed checks](README.md#managed-checks) for the checks and filter examples.
- For bug fixes, demonstrate a regression test failing before the fix and passing afterward. If the failure cannot be reproduced, report that limitation.
- Never remove, skip, or weaken an existing assertion merely to make a change pass.
- Treat zero discovered tests as a failed check. Keep test category traits complete when using category filters.
- Use synchronization signals in concurrency tests. Use timeouts to bound hangs, not delays to establish ordering.
- Report verification commands, test counts, failures, and checks blocked by missing prerequisites.
- Explain each new dependency or project reference.
- For changes affecting packaging, startup, or native interop, also run the relevant `make` targets.
- Native integration tests require vendored artifacts from `make init` and a valid GGUF referenced by `MODEL_PATH`. Missing prerequisites mean native behavior is unverified, not passed.

## gRPC smoke verification

For changes affecting packaging, startup, native loading, or inference through gRPC, use the existing runtime and benchmark commands to verify the packaged runtime with a real model:

1. Use an existing local GGUF with an absolute `HostedModel__ModelPath`, a configured `HostedModel__ModelId`, and matching `ApiKeys__Keys__0` and `BENCH_APIKEY`. Run `make init` if vendor artifacts are missing. See [quickstart](README.md#quickstart) for configuration.
2. Start `make llama-runtime-grpc-run` in a separate terminal or tool session and retain its logs. Wait for the model to reach `Loaded` within a bounded startup deadline. If polling `/health/ready`, use an HTTP/2-capable client and require the body `Healthy`; HTTP 200 alone can also mean the model is still loading or warming up.
3. Send real gRPC requests through the benchmark client:

   ```bash
   make bench-llama-runtime-grpc BENCH_ITERATIONS=1 BENCH_CONCURRENCY=1
   ```

   Set `BENCH_GRPCURL` to the runtime's address if it differs from `http://localhost:5000`. Keep strict response validation enabled. The harness also sends five warm-up requests before the measured request.
4. Require no warm-up failures, measured `Success=1`, `Errors=0`, and no diagnostic errors. Inspect console output and the CSV/JSONL results; the benchmark can exit successfully despite failed requests. Default text validation checks for nonblank generated content. For structured-output changes, repeat with `BENCH_RESPONSEFORMAT=json` and a prompt matching the [benchmark schema](docs/benchmarking.md#run-json-benchmarks).
5. Bound startup and request verification with tool-session deadlines. Stop only the runtime process started for this check, including after failure. Report the commands, request results, and any blocked prerequisites. Missing model or vendor artifacts means this check is unverified. This smoke check supplements `make check` and applicable native integration tests; it does not establish inference quality or performance.

## Quality gates

- Use `make check-fast` for managed unit feedback after restoring dependencies. Use `TEST_PROJECT=path/to/test.csproj TEST_FILTER='FullyQualifiedName~TestClass'` for a narrower run. A solution-wide filter must select tests in each test project. Finish with `make check`.
- ArchitectureBoundaryTests enforces the project-reference rules in docs/architecture.md. Update the rule deliberately when a boundary changes.
- For new bug fixes, preserve the failing-before and passing-after evidence. Explain changed fixtures, skipped tests, analyzer suppressions, and any reduced assertion coverage.
- Report test counts and exact commands. An empty test selection is a failed check.
- Keep inference quality and performance measurements separate from deterministic managed checks. Pin model hashes, runtime settings, and upstream revision when comparing runs.
