# 🦙 llama-runtime — Lightweight Adapter for llama.cpp

This project provides a **minimal, safe, and well-structured native integration layer** around [`ggml-org/llama.cpp`](https://github.com/ggml-org/llama.cpp).
It exposes:

- a **clean C++ OO Adapter Core** for internal use
- a **stable C API bridge** for consumption by .NET (or any foreign runtime)
- predictable memory handling
- compatibility-oriented usage of `llama_batch` and inference APIs

The goal is to offer a **tiny runtime layer** that feels like a real SDK, while staying intentionally small, readable, and robust.

---

## ⭐ Key Design Goals

✔ Minimal dependencies
✔ Stable API surface
✔ No hidden allocations inside the adapter
✔ Works safely across llama.cpp versions
✔ Thread-safe *at the level llama.cpp allows*
✔ Good error semantics instead of raw `nullptr` checking
✔ Predictable lifetime rules

---

## 🧩 Architecture

```
llama.cpp
   │
   ├── Adapter Core (C++ OO layer)
   │      src/llama_adapter_core.cpp
   │      include/llama_adapter_core.h
   │
   └── Public C API Bridge
          src/llama_adapter.cpp
          include/llama_adapter.h
```

### Adapter Core (Internal Layer)

A small C++ utility wrapper that:
- loads a model
- creates a context
- tokenizes
- feeds tokens via `llama_decode`
- performs greedy generation
- exposes a safe `infer(prompt → text)` workflow

This keeps all llama.cpp interactions localized and readable.

### C API Bridge (Public Interface)

A pure C ABI-safe API intended for:
- .NET P/Invoke
- Rust FFI
- Go / Swift bindings
- Any foreign runtime

The API translates into stable result codes and never throws across the ABI boundary.

---

## 🧰 llama.cpp Compatibility

This adapter intentionally uses the **most portable llama.cpp integration path**:

- Uses only guaranteed fields of `llama_batch`
  - `n_tokens`
  - `token`
- Uses `llama_get_logits()` (not version-fragile `..._ith(-1)`)
- Avoids assumptions about internal `seq_id`, `pos`, `logits` pointer representation
- Uses official llama.cpp vocab APIs
- Checks for prompt overflow instead of silently truncating

This means it should work across a wide range of llama.cpp versions including `b7622`.

---

## 🔨 Build

The project expects prebuilt llama.cpp binaries (your build system handles downloading them).

Build native library:

```bash
make native-build
```

Run integration tests:

```bash
make integration-tests
```

Resulting library:

```
native/build/libllama_adapter.(a|so|dylib)
```

---

## 🧪 API Overview

### Load Model

```c
int llama_load_model(const char * path, void ** model_out);
```

- Loads a model
- Returns handle in `model_out`

### Free Model

```c
int llama_unload_model(void * model);
```

---

### Create Context

```c
int llama_create_context(void * model, void ** ctx_out);
```

- Creates execution context from model

### Destroy Context

```c
int llama_remove_context(void * ctx);
```

---

### Perform Inference

```c
int llama_infer(
    void * model,
    void * ctx,
    const char * prompt,
    char * out,
    char * out,
    size_t out_size
);
```

- **Resets context state** (clears KV cache)
- tokenizes prompt
- decodes prompt
- greedy generates continuation
- writes null-terminated result to `out`

---

## 📦 Memory Ownership Rules

| Object | Created By | Freed By |
|--------|-----------|----------|
| `llama_model*` | `llama_load_model` | `llama_unload_model` |
| `llama_context*` | `llama_create_context` | `llama_remove_context` |
| Output Buffer | caller allocated | caller frees |

No adapter-owned global allocations.

---

## 🧵 Threading

- Multiple contexts **may exist in parallel**
- Each context is isolated
- Caller owns concurrency guarantees
- Adapter itself holds no shared mutable state

---

## 🚀 Performance Characteristics

- Uses greedy decoding (simple & deterministic)
- Minimal memory copies
- No dynamic allocations inside hot path
- Respects llama.cpp batching rules

This is intentionally a **simple reference runtime**, not a feature-complete server.

---

## ❗ Error Handling

All functions return stable integer error codes:

```
LLAMA_ADAPTER_OK
LLAMA_ADAPTER_ERR_INVALID_ARG
LLAMA_ADAPTER_ERR_LOAD_MODEL
LLAMA_ADAPTER_ERR_NOT_FOUND
LLAMA_ADAPTER_ERR_IO
LLAMA_ADAPTER_ERR_OUT_OF_MEMORY
LLAMA_ADAPTER_ERR_UNKNOWN
```

---

## 🧭 Version Metadata Support

If environment variable is set:

```
LLAMA_ADAPTER_META_JSON=/path/meta.json
```

then:

```c
llama_adapter_get_version(buffer)
```

extracts "version" from the metadata file.

Useful when embedding llama builds.

---

## 🩹 Troubleshooting

### Build fails with `llama_batch` errors

This project intentionally avoids fragile batch fields.
If you see compile errors, ensure:

- You’re using the included version headers
- No modifications were made to batch handling

---

### Getting Empty Output

Check:
- prompt is non-empty
- model supports text generation
- output buffer > 1 byte

---

### EOS finishes too early

Greedy decoding stops at EOS.
Increase max token count if desired (internal limit is currently modest by design).

---

## 🎯 Intended Use Cases

- .NET runtime integration
- lightweight native embedding
- teaching / demonstration of llama.cpp internals
- experimental runtimes
- places where full llama.cpp server would be overkill

---

## 📜 License

This adapter respects llama.cpp license.
Project license MIT.

---

## 🙌 Credits

- llama.cpp authors
- GGML / GGUF ecosystem
- You — for designing a surprisingly clean adapter 😉
