# llama-runtime

A .NET 10 gRPC inference server for local GGUF models, backed by `llama.cpp`.

The `llama.v2` API provides `Generate`, `EstimateTokens`, and `GetCapabilities`, with request-level sampling controls and JSON output with optional schema constraints. See [platform compatibility](docs/architecture.md#compatibility) for the validated platform and available build targets.

## Quickstart

You need the .NET 10 SDK, CMake 3.16 or later, a C++17 toolchain, `make`, `curl`, `tar`, `unzip`, `shasum`, and a local GGUF model.

1. Create your local configuration:

   ```bash
   cp .env.example .env
   ```

   Edit `.env` to set an absolute `HostedModel__ModelPath`, a public `HostedModel__ModelId`, and your own `ApiKeys__Keys__0`. Set `BENCH_APIKEY` to the same key if you plan to benchmark.

   Choose the platform and .NET runtime pair:

   | `PLATFORM` | `DOTNET_RUNTIME` |
   | --- | --- |
   | `macos-arm64` | `osx-arm64` |
   | `ubuntu-x64` | `linux-x64` |
   | `ubuntu-arm64` | `linux-arm64` |

   On Linux, also set `Llama__Native__NativeLibraryPath=./libllama_adapter.so`. Keep the pinned `LLAMA_VERSION=b10964`.

2. Download and verify the pinned native dependencies:

   ```bash
   make init
   ```

3. Build and start the runtime:

   ```bash
   make llama-runtime-grpc-run
   ```

   This builds a self-contained package in `dist/` and runs it with the Development environment. The default gRPC address is `http://localhost:5000`.

The Makefile reads `.env` first, then falls back to `.env.macos` or `.env.linux` for the host. Use absolute model paths because the run target starts inside `dist/`.

## Run a release

Extract the macOS arm64 release archive and run from its directory:

```bash
mkdir llama-runtime
tar -xf llama-runtime-grpc-osx-arm64.tar.gz -C llama-runtime
cd llama-runtime
chmod +x ./LlamaRuntime.Presentation.Grpc

HostedModel__ModelPath=/absolute/path/to/model.gguf \
HostedModel__ModelId=my-model \
ApiKeys__Keys__0=replace-with-your-api-key \
./LlamaRuntime.Presentation.Grpc
```

For a local Linux package, add `Llama__Native__NativeLibraryPath=./libllama_adapter.so`. See [configuration](#configuration) for environment variables.

If macOS blocks a trusted downloaded package, run `xattr -rd com.apple.quarantine .` from the extracted directory.

To build a package locally, use `make pack`. See [packaging](docs/architecture.md#packaging) for native dependencies and the `PublishReadyToRun` restriction.

## API

Use the [protobuf contract](src/LlamaRuntime.Presentation.Grpc/Protos/generate.proto) to generate a client. Send your API key as `x-api-key` metadata.

Prompts must be nonblank and must not contain NUL characters.

- `Generate` returns content, model identity, token usage, and runtime trace fields.
- `EstimateTokens` reports whether a prompt fits the default output-token reservation.
- `GetCapabilities` reports the loaded model's effective context size and supported features. Query it before budgeting requests.

Use the optional `generation` fields to set sampling and output-token limits. See [generation controls](docs/architecture.md#generation-controls) for defaults and validation.

Set `response_format.type` to `json` to require a JSON object. Omit `json_schema`, pass an empty string, or pass `{}` for any object; otherwise supply a JSON Schema string:

```json
{
  "request_id": "extract-1",
  "prompt": "Extract a short support-ticket title.",
  "response_format": {
    "type": "json",
    "json_schema": "{\"type\":\"object\",\"properties\":{\"title\":{\"type\":\"string\"}},\"required\":[\"title\"],\"additionalProperties\":false}"
  }
}
```

See [structured output](docs/architecture.md#structured-output) for supported schemas and output validation. Use `text` or omit the response format for ordinary generation.

## Configuration

Override [appsettings.json](src/LlamaRuntime.Presentation.Grpc/appsettings.json) with environment variables. Model path, model ID, and API keys require explicit configuration.

| Environment variable | Default or requirement |
| --- | --- |
| `HostedModel__ModelPath` | Required GGUF path |
| `HostedModel__ModelId` | Required public model ID |
| `ApiKeys__Keys__0` | Configure at least one API key |
| `Llama__Native__NativeLibraryPath` | `./libllama_adapter.dylib`; use `.so` on Linux |
| `Llama__Native__ContextSize` | `4096` |
| `Llama__Native__BatchSize` | `512` |
| `Llama__Native__GenerationMaxNewTokens` | `512` |
| `Llama__Native__InferenceBufferSize` | `1048576` bytes |
| `Inference__ChannelCapacity` | `100` |
| `Inference__WorkerCount` | `4` |
| `Inference__AcquireTimeout` | `00:00:30` |
| `Inference__StartupWarmupPrompt` | `Hello` |

See [configuration and health](docs/architecture.md#configuration-and-health) for validation limits, queue admission, cancellation, and health endpoint behavior.

## Benchmarks and maintenance

With the gRPC runtime running, use `make bench-llama-runtime-grpc`. For the REST baseline, start `make run-llama-rest-server MODEL_PATH=/absolute/path/to/model.gguf`, then run `make bench-llama-rest` in another terminal.

See [benchmarking](docs/benchmarking.md) for settings, output formats, and invocation logging.

Use `make verify` to recheck cached dependencies. Maintainers can change the upstream pin with `make pin-llama LLAMA_VERSION=bNNNN`, which updates checksums, vendor artifacts, and tracked documentation together.

## Managed checks

Restore managed dependencies once, then run the quality checks after code changes:

```bash
dotnet restore src/llama-runtime.slnx
make check
```

`make check` builds Release with warnings treated as errors and code style enforced, runs all managed tests with empty test runs treated as failures, verifies C# braces, and checks the working diff for whitespace errors. It stops at the first failure. Managed tests use a mocked native backend and do not require vendor downloads or a GGUF model.

While iterating, run the relevant test project or class. For example:

```bash
dotnet test src/LlamaRuntime.Engine.Tests --no-restore \
  --filter 'FullyQualifiedName~EngineModelTests' \
  -- RunConfiguration.TreatNoTestsAsError=true
```

See [agent testing expectations](AGENTS.md#testing-expectations) for verification requirements. Native changes also require `make native-integration-tests MODEL_PATH=/absolute/path/to/model.gguf`; packaging and startup changes require the relevant build and runtime checks.

## Reference and contributing

- [Architecture](docs/architecture.md) covers ownership, concurrency, lifecycle, and runtime contracts.
- [Native adapter](native/README.md) covers the C API and native build and test commands.
- [Backlog](docs/backlog.md) tracks open and deferred work.

Open an issue before starting a major change. Follow the existing code style and add tests where applicable. For runtime failures, include .NET logs and native adapter stderr. For queue rejection or timeouts, check queue capacity, worker count, and admission timeout.

## License

[MIT](LICENSE). [`llama.cpp`](https://github.com/ggml-org/llama.cpp) is also MIT licensed, copyright Georgi Gerganov and contributors.

### Fast managed feedback

After `dotnet restore src/llama-runtime.slnx`, use `make check-fast` for managed unit tests, compiler checks, the brace rule, and whitespace checks. Narrow the tests with `make check-fast TEST_PROJECT=src/LlamaRuntime.Engine.Tests/LlamaRuntime.Engine.Tests.csproj TEST_FILTER='FullyQualifiedName~LlamaSessionTests'`. Finish code changes with `make check`, which includes managed integration tests. Both commands fail on an empty test selection.

Project-reference architecture tests keep native interop, engine logic, and the gRPC host in their declared layers. Native integration and benchmarks still require their own model and vendor prerequisites. Model-dependent quality measurements should record the model SHA-256, llama.cpp pin, generation settings, and context size.
