# llama-runtime

A native-first, single-model, **gRPC-based LLM inference runtime** built on top of `llama.cpp`.

---

## Table of contents

* [Overview](#overview)
* [Features](#features)
* [Architecture](#architecture)
* [Documentation](#documentation)
* [Quickstart](#quickstart)

  * [Prerequisites](#prerequisites)
  * [Initialize dependencies](#initialize-dependencies)
  * [Build the runtime](#build-the-runtime)
  * [Run from source](#run-from-source)
* [Running a release (binary distribution)](#running-a-release-binary-distribution)

  * [macOS Gatekeeper / Quarantine note](#macos-gatekeeper--quarantine-note)
* [Benchmarking](#benchmarking)
* [Configuration](#configuration)
* [Troubleshooting & tips](#troubleshooting--tips)
* [Contributing](#contributing)
* [License & third-party](#license--third-party)

---

## Overview

`llama-runtime` is a high-performance inference server designed for local and edge deployments. It pairs the raw performance of `llama.cpp` with a single-file **gRPC runtime** that intentionally hosts one model per process:

> **`llama-runtime-grpc`**

This runtime is optimized for predictable serving: bounded queueing, pooled contexts, explicit readiness, and a small serving surface. The public gRPC contract is `llama.v2`, which includes `Generate`, `EstimateTokens`, and `GetCapabilities`.

The current runtime keeps request-level generation behavior narrow: it uses greedy defaults, rejects non-default `temperature` and `top_p`, and treats `max_output_tokens` as a placeholder compatibility field that is accepted only when it matches configured `Llama:Native:GenerationMaxNewTokens`.

---

## Features

* **Native-first** — direct integration with `llama.cpp` for maximum inference performance.
* **gRPC runtime** — single-file, self-contained `llama-runtime-grpc` built on .NET 10.
* **Normalized runtime contract** — `llama.v2` exposes model identity, usage, runtime trace fields, and capability discovery.
* **Load-time capability discovery** — `GetCapabilities` reports the effective features of the currently loaded model/runtime pair instead of relying on static config assumptions.
* **Structured output** — `json` response format supports either any JSON object or a JSON object constrained by a caller-provided schema. The managed runtime converts schemas to grammar, native applies grammar sampling, and output is validated before success.
* **Secure by default** — API key authentication and environment-based configuration.
* **Benchmarking tools** — compare gRPC runtime vs. `llama.cpp` REST baseline.
* **Cross-platform** — macOS (Apple Silicon) and Linux supported.
* **Bounded concurrency** — request queueing, worker-count limits, and queue-admission timeouts.
* **Explicit lifecycle** — startup model loading, mandatory warm-up before readiness, and deterministic shutdown unload.
* **Strong contracts** — prompt-budget enforcement, structured native error mapping, and health checks tied to model state.

---

## Architecture

```
llama.cpp (C/C++)
   └── Native Adapter (CMake)
         └── llama-runtime-grpc (.NET 10, gRPC)
               └── Client Applications
```

---

## Documentation

* [docs/architecture.md](docs/architecture.md) — runtime ownership, threading, and lifecycle details.
* [docs/benchmarking.md](docs/benchmarking.md) — reproducible gRPC vs. `llama.cpp` REST benchmark workflow.
* [native/README.md](native/README.md) — native adapter design, API surface, and native build/test notes.
* [docs/runtime-roadmap.md](docs/runtime-roadmap.md) — planned runtime work and roadmap notes.

---

## Quickstart

### Prerequisites

* .NET 10 SDK
* CMake (≥ 3.14)
* C/C++ toolchain (Clang / GCC / MSVC)
* Standard tools: `curl`, `tar`, `unzip`

### 1. Initialize dependencies

Downloads headers and platform-specific `llama.cpp` binaries and verifies the downloaded archives against the repo-pinned SHA-256 manifest for the supported `llama.cpp` revision.

```bash
make init
```

Validate the cached upstream archives again at any time with:

```bash
make verify
```

Maintainers should change the supported `llama.cpp` pin with:

```bash
make pin-llama LLAMA_VERSION=bNNNN
```

### 2. Build the package

```bash
make pack
```

This produces a portable distribution package under `dist/`, for example:

```
dist/
├── LlamaRuntime.Presentation.Grpc (executable)
├── libllama_adapter.dylib (native adapter)
├── libllama.dylib (vendor libs)
├── LICENSE
└── ...
```

The package build disables `PublishReadyToRun` by default on all targets. We reproduced a deterministic startup crash in the .NET 10 published runtime with `PublishReadyToRun=true` while the process was loading the native `llama.cpp` stack, so the default package now favors the JIT path for stability. You can still override this for investigation with `PUBLISH_READY_TO_RUN=true make pack`.

### 3. Run from source

```bash
make llama-runtime-grpc-run
```

You must set both the hosted model path and the public model id using environment variables or your `.env` file:

```bash
# example override
HostedModel__ModelPath=/absolute/path/to/your/model.gguf \
HostedModel__ModelId=stories15m \
make llama-runtime-grpc-run
```

---

## Running a release (binary distribution)

If you downloaded a release artifact (example: `llama-runtime-grpc-osx-arm64.tar.gz`), follow these steps:

1. **Extract the archive**

```bash
mkdir llama-runtime && tar -xf llama-runtime-grpc-osx-arm64.tar.gz -C llama-runtime
cd llama-runtime
```

2. **Prepare model directory**

Place your GGUF model file (for example `model.gguf`) inside a `models` directory or point the runtime at an absolute path.

3. **Make the runtime executable (if needed)**

```bash
chmod +x ./LlamaRuntime.Presentation.Grpc
```

4. **Run**

```bash
# example using environment variables
HostedModel__ModelPath="/abs/path/to/model.gguf" \
HostedModel__ModelId="stories15m" \
./LlamaRuntime.Presentation.Grpc
```

### macOS Gatekeeper / Quarantine note

macOS may mark downloaded files as quarantined which prevents them from running immediately (Gatekeeper). If macOS blocks the binaries, remove the quarantine attribute from the extracted files or folder:

```bash
# remove quarantine recursively from the directory
xattr -rd com.apple.quarantine ./llama-runtime

# or for a single executable
xattr -rd com.apple.quarantine ./LlamaRuntime.Presentation.Grpc
```

After clearing quarantine, re-run `chmod +x` if necessary and execute the binary. Use this only for artifacts you trust.

---

## Benchmarking

### gRPC runtime

```bash
make bench-llama-runtime-grpc
```

### `llama.cpp` REST (baseline)

```bash
make run-llama-rest-server
make bench-llama-rest
```

Benchmarks include latency and throughput comparisons. Use them to validate queue sizing, worker counts, and deployment tradeoffs.
For the full harness setup, environment variables, and output format, see [docs/benchmarking.md](docs/benchmarking.md).

---

## Structured output

`GenerateRequest.response_format.type` supports:

* `text` or unset — normal text generation.
* `json` — a complete JSON object. Omit `response_format.json_schema`, pass an empty string, or pass `{}` for any object. Pass a non-empty schema to constrain the object shape.

`json` uses a raw JSON Schema string when constraints are needed:

```json
{
  "request_id": "extract-1",
  "prompt": "Extract a short support-ticket summary.",
  "response_format": {
    "type": "json",
    "json_schema": "{\"type\":\"object\",\"properties\":{\"title\":{\"type\":\"string\"}},\"required\":[\"title\"],\"additionalProperties\":false}"
  }
}
```

The v1 schema subset is intentionally strict: every object schema must declare `properties`, `required`, and `additionalProperties: false`, and `required` must list exactly every declared property. It supports root object schemas, nested strict objects, arrays, strings, numbers, integers, booleans, null, and string enums. The schema constrains shape; the prompt should still describe field meaning and task intent. Native inference receives only text mode or a ready-made grammar string; it does not parse JSON Schema.

---

## Configuration

Call `GetCapabilities` before sending inference traffic if your client or proxy needs prompt-budget guarantees. The runtime reports the effective request-time context size from the actual created `llama_context`, and startup fails if the configured runtime context does not match what `llama.cpp` actually created for the loaded model.

Core environment variables usually live in platform-specific env files:

* `.env.macos`
* `.env` overrides for other supported platforms such as `ubuntu-x64` or `ubuntu-arm64`

Example `.env` values (required):

```env
PLATFORM=macos-arm64
# llama.cpp source/release version embedded into the native adapter
LLAMA_VERSION=b8868
DOTNET_RUNTIME=osx-arm64
LLAMA_REST_PORT=4999
```

`LLAMA_VERSION` is a repo pin, not a per-machine convenience override. When intentionally changing the supported upstream revision, use `make pin-llama LLAMA_VERSION=bNNNN` so the checksum manifest, vendored artifacts, and tracked docs stay aligned.

Packaging overrides:

```env
PUBLISH_READY_TO_RUN=false
PUBLISH_SINGLE_FILE=true
```

You can also supply configuration via standard environment variables at runtime. Important runtime settings:

```env
HostedModel__ModelPath=/absolute/path/to/model.gguf
Inference__ChannelCapacity=100
Inference__WorkerCount=4
Inference__AcquireTimeout=00:00:30
Inference__StartupWarmupPrompt=Hello
```

The runtime serves a single hosted model. Requests enter a bounded queue and are executed by a fixed worker pool. `Inference__WorkerCount` controls concurrent inference capacity. Startup always performs one warm-up inference before readiness goes healthy. Cancellation is immediate while queued and best-effort once native inference has started.

`Inference__AcquireTimeout` controls how long a request may wait to enter the bounded queue. It is not a hard kill timeout for native inference that has already started.

Native runtime settings are validated on startup and the process fails fast if any value is invalid. The current rules are:

```env
Llama__Native__NativeLibraryPath=required
Llama__Native__ContextSize=1..65536
Llama__Native__BatchSize=1..4096 and <= ContextSize
Llama__Native__GenerationMaxNewTokens=1..16384 and < ContextSize
Llama__Native__InferenceBufferSize=1..16777216
```

---

## Troubleshooting & tips

* **Model not found** — ensure `HostedModel__ModelPath` points to an existing GGUF file or place `model.gguf` inside `models/`.
* **macOS: permission denied / quarantined** — see the macOS Gatekeeper section above.
* **Queue rejection / timeout** — tune `Inference__ChannelCapacity`, `Inference__WorkerCount`, and `Inference__AcquireTimeout` for queue admission behavior.
* **Logs** — the runtime emits structured operational logs without prompt or generated-text payloads. Native adapter failures are mapped into stable error codes.

If you hit an obscure issue, include the `dotnet` runtime logs and the native adapter stderr when filing an issue.

---

## Contributing

Contributions are welcome. Please open an issue for discussion before starting work on a major change. Follow the existing code style and add tests where applicable.

---

## License & third-party

This project is licensed under the MIT License — see [LICENSE](LICENSE).

Third-party notices:

* **llama.cpp** — MIT. © Georgi Gerganov & contributors. [https://github.com/ggml-org/llama.cpp](https://github.com/ggml-org/llama.cpp)

## Why gRPC?

* **Strong contracts** — type-safe, well-defined service interfaces.
* **First-class .NET support** — native integration with ASP.NET Core.
* **Stable inference on limited resources** — bounded queueing and worker limits keep concurrency explicit.

This runtime is intentionally narrow: one hosted model, gRPC only, greedy generation, and explicit operational limits.
