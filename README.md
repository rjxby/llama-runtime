# llama-runtime

A native-first, secure, **gRPC-based LLM inference runtime** built on top of `llama.cpp`.

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

`llama-runtime` is a high-performance inference server designed for local and edge deployments. It pairs the raw performance of `llama.cpp` with a production-ready, single-file **gRPC runtime**:

> **`llama-runtime-grpc`**

This runtime makes it straightforward to embed LLM inference into .NET and polyglot systems with strong typing, low latency, and predictable performance.

---

## Features

* **Native-first** — direct integration with `llama.cpp` for maximum inference performance.
* **gRPC runtime** — single-file, self-contained `llama-runtime-grpc` built on .NET 10.
* **Secure by default** — API key authentication and environment-based configuration.
* **Benchmarking tools** — compare gRPC runtime vs. `llama.cpp` REST baseline.
* **Cross-platform** — macOS (Apple Silicon) and Linux supported.
* **Production-oriented** — request queueing, predictable memory usage, and strong contracts.

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

You can set the model path using an environment variable or your `.env` file:

```bash
# example override
HostedModel__ModelPath=/absolute/path/to/your/model.guff make llama-runtime-grpc-run
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

Benchmarks include latency and throughput comparisons. Use them to validate improvements or sizing decisions for edge deployments.

---

## Configuration

All required environment variables live in platform-specific env files:

* `.env.macos`
* `.env.linux`

Example `.env` values (required):

```env
PLATFORM=macos-arm64
LLAMA_VERSION=b7932
DOTNET_RUNTIME=osx-arm64
LLAMA_REST_PORT=4999
```

You can also supply configuration via standard environment variables at runtime or a `.env` file loader in your process.

---

## Troubleshooting & tips

* **Model not found** — ensure `HostedModel__ModelPath` points to an existing GGUF file or place `model.gguf` inside `models/`.
* **macOS: permission denied / quarantined** — see the macOS Gatekeeper section above.
* **Logs** — the runtime emits structured logs. Check stdout/stderr for the runtime and native adapter logs.

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

## Why gRPC?

* **Strong contracts** — type-safe, well-defined service interfaces.
* **First-class .NET support** — native integration with ASP.NET Core.
* **Stable inference on limited resources** — request queueing prevents memory exhaustion and yields predictable performance under load.

This runtime is intentionally **boring and fast** — optimized for reliability and predictable latency in edge and local deployments.
