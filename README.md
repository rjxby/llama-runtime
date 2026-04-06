# llama-runtime

A native-first, single-model, **gRPC-based LLM inference runtime** built on top of `llama.cpp`.

---

## Table of contents

* [Overview](#overview)
* [Features](#features)
* [Architecture](#architecture)
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

This runtime is optimized for predictable serving: bounded queueing, pooled contexts, explicit readiness, and a small serving surface.

---

## Features

* **Native-first** — direct integration with `llama.cpp` for maximum inference performance.
* **gRPC runtime** — single-file, self-contained `llama-runtime-grpc` built on .NET 10.
* **Secure by default** — API key authentication and environment-based configuration.
* **Benchmarking tools** — compare gRPC runtime vs. `llama.cpp` REST baseline.
* **Cross-platform** — macOS (Apple Silicon) and Linux supported.
* **Bounded concurrency** — request queueing, worker-count limits, and per-request execution timeouts.
* **Explicit lifecycle** — startup model loading, readiness state transitions, and deterministic shutdown unload.
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

## Quickstart

### Prerequisites

* .NET 10 SDK
* CMake (≥ 3.14)
* C/C++ toolchain (Clang / GCC / MSVC)
* Standard tools: `curl`, `tar`, `unzip`

### 1. Initialize dependencies

Downloads headers and platform-specific `llama.cpp` binaries.

```bash
make init
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

### 3. Run from source

```bash
make llama-runtime-grpc-run
```

You can set the hosted model path using an environment variable or your `.env` file:

```bash
# example override
HostedModel__ModelPath=/absolute/path/to/your/model.gguf make llama-runtime-grpc-run
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
# example using an environment variable
HostedModel__ModelPath="/abs/path/to/model.gguf" ./LlamaRuntime.Presentation.Grpc
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

---

## Configuration

Core environment variables usually live in platform-specific env files:

* `.env.macos`
* `.env.linux`

Example `.env` values (required):

```env
PLATFORM=macos-arm64
# llama.cpp source/release version embedded into the native adapter
LLAMA_VERSION=b8672
DOTNET_RUNTIME=osx-arm64
LLAMA_REST_PORT=4999
```

You can also supply configuration via standard environment variables at runtime. Important runtime settings:

```env
HostedModel__ModelPath=/absolute/path/to/model.gguf
Inference__ChannelCapacity=100
Inference__WorkerCount=4
Inference__AcquireTimeout=00:00:30
Inference__EnableStartupWarmup=true
```

The runtime serves a single hosted model. Requests enter a bounded queue and are executed by a fixed worker pool. Cancellation is immediate while queued and best-effort once native inference has started.

---

## Troubleshooting & tips

* **Model not found** — ensure `HostedModel__ModelPath` points to an existing GGUF file or place `model.gguf` inside `models/`.
* **macOS: permission denied / quarantined** — see the macOS Gatekeeper section above.
* **Queue rejection / timeout** — tune `Inference__ChannelCapacity`, `Inference__WorkerCount`, and `Inference__AcquireTimeout`.
* **Logs** — the runtime emits structured managed logs. Native adapter failures are mapped into stable error codes.

If you hit an obscure issue, include the `dotnet` runtime logs and the native adapter stderr when filing an issue.

---

## Contributing

Contributions are welcome. Please open an issue for discussion before starting work on a major change. Follow the existing code style and add tests where applicable.

---

## License & third-party

This project is licensed under the MIT License — see [LICENSE](LICENSE).

Third-party notices:

* **llama.cpp** — MIT. © Georgi Gerganov & contributors. [https://github.com/ggml-org/llama.cpp](https://github.com/ggml-org/llama.cpp)

---

## Architecture Notes

See [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md) for runtime ownership, threading, and lifecycle details.

---

## Why gRPC?

* **Strong contracts** — type-safe, well-defined service interfaces.
* **First-class .NET support** — native integration with ASP.NET Core.
* **Stable inference on limited resources** — bounded queueing and worker limits keep concurrency explicit.

This runtime is intentionally narrow: one hosted model, gRPC only, greedy generation, and explicit operational limits.
