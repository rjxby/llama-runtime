# llama-runtime

A .NET 10 gRPC inference server backed by `llama.cpp`. Each process hosts one GGUF model, with pooled contexts, a bounded request queue, API key authentication, and readiness after startup warm-up.

Supports macOS on Apple Silicon and Linux. The `llama.v2` API provides `Generate`, `EstimateTokens`, and `GetCapabilities`, with request-level sampling controls and JSON output with optional schema constraints.

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

Extract the archive for your platform and run from its directory:

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

On Linux, add `Llama__Native__NativeLibraryPath=./libllama_adapter.so`. The executable reads standard .NET configuration and environment variables; `.env` loading belongs to the Makefile.

If macOS blocks a trusted downloaded package, run `xattr -rd com.apple.quarantine .` from the extracted directory.

To build a package locally, use `make pack`. `PublishReadyToRun` defaults to `false` because enabling it caused a reproducible .NET 10 startup crash while loading the native libraries. Override it for investigation with `PUBLISH_READY_TO_RUN=true make pack`.

## API

Use the [protobuf contract](src/LlamaRuntime.Presentation.Grpc/Protos/generate.proto) to generate a client. Send your API key as `x-api-key` metadata.

- `Generate` returns content, model identity, token usage, and runtime trace fields.
- `EstimateTokens` reports whether a prompt fits the default output-token reservation.
- `GetCapabilities` reports the loaded model's effective context size and supported features. Query it before budgeting requests.

Generation defaults to greedy decoding with `temperature=0`, `top_p=1`, and the configured `GenerationMaxNewTokens`. Requests may lower `max_output_tokens`, but cannot exceed that ceiling. `temperature` must be finite and non-negative; `top_p` must be finite and in `(0, 1]`. A non-default `top_p` requires `temperature > 0`.

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

Schemas support root and nested objects, arrays, strings, numbers, integers, booleans, null, and string enums. Every object must declare `properties`, `required`, and `additionalProperties: false`, with all properties required. See [the runtime contract](docs/architecture.md#runtime-contract) for schema processing and output validation. Use `text` or omit the response format for ordinary generation.

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

Startup validates native settings: context size `1..65536`, batch size `1..4096` and no larger than context size, output tokens `1..16384` and less than context size, buffer size `1..16777216` bytes. Startup also fails if the created native context size differs from the configured size.

`WorkerCount` limits concurrent inference. `AcquireTimeout` limits queue admission wait, not running inference. Cancellation is immediate while queued and best-effort after native inference starts. `/health/ready` becomes healthy after model loading and warm-up; `/health/live` checks liveness.

## Benchmarks and maintenance

With the gRPC runtime running, use `make bench-llama-runtime-grpc`. For the REST baseline, start `make run-llama-rest-server MODEL_PATH=/absolute/path/to/model.gguf`, then run `make bench-llama-rest` in another terminal.

See [benchmarking](docs/benchmarking.md) for settings and output formats. Benchmark invocation logs include prompts and generated output; runtime operational logs omit both.

Use `make verify` to recheck cached dependencies. Maintainers can change the upstream pin with `make pin-llama LLAMA_VERSION=bNNNN`, which updates checksums, vendor artifacts, and tracked documentation together.

## Reference and contributing

- [Architecture](docs/architecture.md) covers ownership, concurrency, lifecycle, and runtime contracts.
- [Native adapter](native/README.md) covers the C API and native build and test commands.
- [Roadmap](docs/runtime-roadmap.md) tracks planned work.

Open an issue before starting a major change. Follow the existing code style and add tests where applicable. For runtime failures, include .NET logs and native adapter stderr. For queue rejection or timeouts, check queue capacity, worker count, and admission timeout.

## License

[MIT](LICENSE). [`llama.cpp`](https://github.com/ggml-org/llama.cpp) is also MIT licensed, copyright Georgi Gerganov and contributors.
