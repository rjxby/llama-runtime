# Benchmarking

The shared .NET benchmark harness compares the gRPC runtime with a separate `llama.cpp` REST server. Use the same model and generation settings when comparing results.

---

## 1. Prerequisites

* .NET 10+ SDK installed
* Your `LlamaRuntime` adapter running (gRPC)
* The pinned `llama.cpp` server binary from `make init`, for the REST baseline
* Model files (e.g., `.gguf`) available locally

---

## 2. Start the servers

### gRPC runtime

Ensure it is listening on the port configured in your app (usually `http://localhost:5000`).

### llama.cpp REST server

You can run it directly via the Makefile:

```bash
make run-llama-rest-server MODEL_PATH=/absolute/path/to/model.gguf
```

This starts the REST API on `LLAMA_REST_PORT` from the selected environment file. Both checked-in environment examples set it to `4999`.

---

## 3. Run benchmarks

The Make targets select `BENCH_MODE=LlamaRuntimeGrpc` or `BENCH_MODE=LlamaRest`. Direct `dotnet run --project src/LlamaRuntime.Benchmarks` calls use the same names.

### Environment variables

| Variable | Meaning and default |
| --- | --- |
| `BENCH_ITERATIONS` | Measured requests. Make defaults to `20`; direct harness runs default to `50`. |
| `BENCH_CONCURRENCY` | Maximum requests per batch. Make defaults to `5`; direct harness runs default to `1`. Each batch finishes before the next starts. |
| `BENCH_PROMPT` | Prompt text. Make passes the story prompt by default. Direct harness runs in `json` mode use the structured extraction prompt when no prompt is supplied. |
| `BENCH_RESPONSEFORMAT` | `text` or `json`. Defaults to `text`. |
| `BENCH_GRPCURL` | Absolute gRPC base URL. Defaults to `http://localhost:5000`. |
| `BENCH_APIKEY` | Required for gRPC. Must match a key in the runtime's `ApiKeys:Keys` configuration. |
| `BENCH_LLAMARESTURL` | Required for direct REST harness runs. Make sets it to `http://localhost:$(LLAMA_REST_PORT)/completion`. |
| `BENCH_LLAMARESTMAXNEWTOKENS` | REST output-token limit. Defaults to `512`, matching the runtime's default `GenerationMaxNewTokens`. |
| `BENCH_LLAMARESTTEMPERATURE` | REST temperature. Defaults to `0.0`, matching the runtime's greedy decoding. |
| `BENCH_OUTPUT_FILE` | CSV path. If unset, generates `benchmark_{mode}_{date}_{counter}.csv`. |
| `BENCH_LOG_INVOCATIONS` | Write prompt and output to one JSONL record per measured invocation. Defaults to `true`. |
| `BENCH_INVOCATION_FILE` | JSONL path. If unset, derives `<summary-name>.invocations.jsonl` from the CSV path. |

### Run gRPC benchmark

```bash
make bench-llama-runtime-grpc
```

To supply a prompt with spaces or quotes:

```bash
make bench-llama-runtime-grpc BENCH_PROMPT='Write a story titled "Two words".'
```

Both benchmark targets export `BENCH_PROMPT` through a target-specific assignment, without inserting prompt text into shell commands. The default prompt is not exported to unrelated targets. Normal Make variable syntax still applies; use `$$` for a literal dollar sign in a Make assignment.

### Run llama.cpp REST benchmark

```bash
make bench-llama-rest
```

### Run JSON benchmarks

```bash
BENCH_RESPONSEFORMAT=json make bench-llama-runtime-grpc
BENCH_RESPONSEFORMAT=json make bench-llama-rest
```

`json` mode uses the same built-in schema for both transports: required string fields `title`, `summary`, `category`, and `next_action`, with `additionalProperties: false`. This keeps benchmark success from being satisfied by `{}`.

Both benchmarks use the same timing and concurrency code. The REST decode defaults match the gRPC runtime's default output ceiling and greedy policy; adjust them if the runtime configuration differs.

---

## 4. Output & logging

The harness runs five warm-up requests before measurement. Warm-up requests do not appear in the timing results or invocation log.

Results are logged to the console and written to a CSV file. Latency statistics include successful requests; error count and success rate report failures separately.

CSV columns:
`Timestamp, Mode, Iterations, Concurrency, AvgLatency, P50, P90, P99, Throughput, SuccessRate, ErrorCount`

When `BENCH_LOG_INVOCATIONS=true`, the harness also writes a JSONL sidecar with one record per non-warmup request. Each record includes timestamp, mode, iteration, request id, response format, measured latency, success/error state, prompt, generated output, and REST parse diagnostics when applicable.

The JSONL sidecar can contain sensitive prompt and model-output payloads. It is intended for benchmark reproducibility and sanity checks only; runtime/service logs remain payload-free.

---

## 5. Notes

* Latency includes client, transport, and server overhead.
* Use `BENCH_CONCURRENCY` to test throughput under multiple simultaneous requests.
* Adjust `BENCH_PROMPT` and model size to evaluate different scenarios.
* If you compare gRPC against REST, keep `n_predict` / `GenerationMaxNewTokens` and decoding policy aligned first. Different decode defaults can dominate the results.
* Runtime `json` mode without a schema only requires an object root, so `{}` is a valid successful output.
* The benchmark intentionally sends the built-in schema whenever `BENCH_RESPONSEFORMAT=json`, so both gRPC and REST must produce useful fields.
* The gRPC benchmark sends `response_format.type=json` plus `response_format.json_schema`. The llama.cpp REST benchmark sends the same schema through `/completion` as `json_schema`.

---
