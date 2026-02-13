# llama-runtime

A native-first, secure, **gRPC-based LLM inference runtime** built on top of `llama.cpp`.

---

## Overview

`llama-runtime` is a high-performance inference server designed for local and edge deployments.
It combines the raw speed of `llama.cpp` with a production-ready **gRPC runtime** called:

> **`llama-runtime-grpc`**

This makes it easy to embed LLM inference into .NET or polyglot systems with strong typing, low latency, and predictable performance.

---

## Architecture

```
llama.cpp (C/C++)
   └── Native Adapter (CMake)
         └── llama-runtime-grpc (.NET 10, gRPC)
               └── Client Applications
```

---

## Key Features

- **Native-First** — Direct integration with `llama.cpp`
- **llama-runtime-grpc** — Single-file, self-contained gRPC runtime
- **Secure by Default** — API key auth & environment-based config
- **Benchmarking Built In** — Compare REST vs gRPC performance
- **Cross-Platform** — macOS (Apple Silicon) & Linux

---

## Prerequisites

- **.NET 10 SDK**
- **CMake ≥ 3.14**
- **C/C++ Toolchain** (Clang / GCC / MSVC)
- **Standard tools**: `curl`, `tar`, `unzip`

---

## Getting Started

### 1. Initialize Dependencies

Downloads headers and platform-specific `llama.cpp` binaries.

```bash
make init
```

---

### 2. Build the Runtime

```bash
make runtime
```

This produces a **portable distribution package**:

```
dist/
├── LlamaRuntime.Presentation.Grpc (Executable)
├── libllama_adapter.dylib (Native Adapter)
├── libllama.dylib (Vendor Libs)
├── LICENSE
└── ...
```

---

### 3. Run from Source

```bash
make run-dist
```

> Ensure your model exists at `models/llama.bin` (relative to root)
> (or override `MODEL_PATH` in `.env`)

---

## Running a Release

If you downloaded a release artifact (e.g., `llama-runtime-grpc-osx-arm64.tar.gz`):

1. **Extract the archive**:
   ```bash
   mkdir llama-runtime && tar -xf llama-runtime-grpc-osx-arm64.tar.gz -C llama-runtime
   cd llama-runtime
   ```

2. **Prepare Model**:
   Place your GGUF model file (e.g., `llama.bin`) in a `models` directory or configure the path.

3. **Run**:
   ```bash
   # Example with environment variable for model path
   Llama__Native__ModelPath="/abs/path/to/model.bin" ./LlamaRuntime.Presentation.Grpc
   ```

## Benchmarking

### gRPC Runtime

```bash
make bench-llama-runtime-grpc
```

### llama.cpp REST (baseline)

```bash
make run-llama-rest-server
make bench-llama-rest
```

---

## Environment Configuration

All required variables live in platform-specific env files:

- `.env.macos`
- `.env.linux`

Required:

```env
PLATFORM=macos-arm64
LLAMA_VERSION=b7932
DOTNET_RUNTIME=osx-arm64
LLAMA_REST_PORT=4999
```

---

## License

MIT License — see [LICENSE](LICENSE)

### Third-Party

- **llama.cpp** — MIT
  © Georgi Gerganov & contributors
  https://github.com/ggml-org/llama.cpp

---

## Why gRPC?

- **Strong contracts** — Type-safe, well-defined service interfaces
- **First-class .NET support** — Native integration with ASP.NET Core
- **Stable inference on limited resources** — Built-in request queue prevents memory exhaustion and ensures predictable performance under load

This runtime is designed to be **boring and fast**.
