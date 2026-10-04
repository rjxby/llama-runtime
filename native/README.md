# Native adapter

The native library exposes `llama.cpp` through the public [C header](include/llama_adapter.h). See [architecture](../docs/architecture.md#native-adapter) for the adapter components and [ownership and lifecycle](../docs/architecture.md#ownership-and-lifecycle) for handle lifetimes and concurrency rules.

## Build and test

From the repository root, download the pinned dependencies and build the adapter:

```bash
make init
make native-build
```

The shared library is written to `native/build/libllama_adapter.dylib` on macOS or `native/build/libllama_adapter.so` on Linux. See [compatibility](../docs/architecture.md#compatibility) for the supported upstream pin and platform verification.

Ordinary builds and packages use `BUILD_TESTING=OFF` and do not fetch test dependencies or require a model. To build and run both native test executables through CTest:

```bash
make native-integration-tests MODEL_PATH=models/stories15M-q4_0.gguf
```

`MODEL_PATH` must name an existing model file. Relative paths resolve from the repository root; absolute paths also work. For direct CMake builds, pass `-DBUILD_TESTING=ON -DMODEL_PATH=/absolute/path/to/model.gguf`. The configured tests receive that path through CTest.

## C API

Include `llama_adapter.h` from a C or C++ caller and link the adapter shared library. The header is the authoritative source for signatures, structs, response formats, and error values.

| Function | Purpose |
| --- | --- |
| `llama_adapter_get_version` | Writes the upstream version embedded at build time. |
| `llama_load_model` / `llama_unload_model` | Load and release a model handle. |
| `llama_model_get_metadata` | Read training-context size and tokenizer type. |
| `llama_create_context` / `llama_remove_context` | Create and release a context handle. |
| `llama_context_get_metadata` | Read the actual context size. |
| `llama_context_reset` | Clear generation state. Inference also resets at entry. |
| `llama_count_tokens` | Count prompt tokens without generating output. |
| `llama_infer` | Generate text and report token/byte usage. |

The inference entry point is:

```c
int llama_infer(
    void *ctx,
    const char *prompt,
    const llama_adapter_generation_params_t *params,
    char *out,
    size_t out_size,
    llama_adapter_infer_result_t *result
);
```

Pass null-terminated prompt and grammar strings. Generation parameters select the output-token reservation, temperature, top-p, seed, response format, grammar, and optional cooperative abort callback. Use `LLAMA_ADAPTER_RESPONSE_FORMAT_TEXT` for text or `LLAMA_ADAPTER_RESPONSE_FORMAT_GRAMMAR` with a prepared GBNF string for constrained output.

The output buffer receives a null-terminated string. `result` reports prompt tokens, output tokens, total tokens, and output bytes excluding the terminator. The caller supplies the buffer and its byte capacity.

## Errors and troubleshooting

Check the integer result of every call against `LLAMA_ADAPTER_OK`. The [error enum](include/llama_adapter.h) lists every exported error value.

- `LLAMA_ADAPTER_ERR_BUFFER_TOO_SMALL` means the generated output and its terminator do not fit the supplied buffer.
- `LLAMA_ADAPTER_ERR_PROMPT_BUDGET` preserves the prompt-token count in `result`; shorten the prompt or lower the reserved output tokens.
- `LLAMA_ADAPTER_ERR_EMPTY_OUTPUT` means generation produced no continuation tokens.
- `LLAMA_ADAPTER_ERR_CANCELLED` means the native operation aborted.

For build errors involving upstream types or functions, check that headers and binaries match the [pinned revision](../docs/architecture.md#compatibility), then rebuild with `make native-build`. For early end-of-generation, check the model, prompt, and output-token reservation. Resetting the context cannot force a model to continue beyond its end-of-generation token.

## License

The adapter and `llama.cpp` use the MIT license. Native builds copy both license files alongside the library.
