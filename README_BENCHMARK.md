# LlamaRuntime Benchmarks

This document explains how to benchmark **LlamaRuntime gRPC** against the **llama.cpp REST server** using the shared .NET benchmark harness.

It is designed to be **reproducible**, **fair**, and **easy to run** on any machine.

---

## 1. Prerequisites

* .NET 10+ SDK installed
* Your `LlamaRuntime` adapter running (gRPC)
* `llama.cpp` server binary (for REST baseline) — you can build or run it from the `vendor` folder using the Makefile
* Model files (e.g., `.gguf`) available locally

---

## 2. Start the servers

### gRPC Adapter (LlamaRuntime)

Ensure it is listening on the port configured in your app (usually `http://localhost:5000`).

### llama.cpp REST server

You can run it directly via the Makefile:

```bash
make run-llama-rest-server MODEL=/path/to/models/stories15M-q4_0.gguf
```

This will start the REST API on the configured port (default `8080`).

---

## 3. Run Benchmarks

The benchmark harness supports **two modes**: `grpc` and `llama-rest`.

### Environment Variables

* `BENCH_ITERATIONS` — number of requests to send (default: 50)
* `BENCH_CONCURRENCY` — number of concurrent requests (default: 1)
* `BENCH_PROMPT` — text prompt to send (default story prompt)
* `BENCH_GRPCURL` — absolute gRPC base URL for the runtime (default: `http://localhost:5000`)
* `BENCH_OUTPUT_FILE` — (Optional) Path to save CSV results. If unset, it generates `benchmark_{mode}_{date}_{counter}.csv`.
* `BENCH_APIKEY` — **Required for gRPC.** API key matching `ApiKeys__Keys__...` in service config.
* `BENCH_LLAMARESTMAXNEWTOKENS` — max tokens requested from the REST baseline. Default: `512` to align with the gRPC runtime default `GenerationMaxNewTokens`.
* `BENCH_LLAMARESTTEMPERATURE` — temperature requested from the REST baseline. Default: `0.0` to align more closely with the gRPC runtime's greedy decoding.

### Run gRPC Benchmark

```bash
make bench-grpc
```

### Run llama.cpp REST Benchmark

```bash
make bench-llama-rest
```

> Both benchmarks share identical timing and concurrency logic, making them directly comparable.
> The REST benchmark defaults are intentionally aligned with the current gRPC runtime defaults. Override them only when you are explicitly testing a different decode configuration.

---

## 4. Output & Logging

Results are logged to the console and optionally to a CSV file.

**CSV Columns:**
`Timestamp, Mode, Iterations, Concurrency, AvgLatency, P50, P90, P99, Throughput, SuccessRate, ErrorCount`

---

## 5. Notes

* Benchmarks include **end-to-end latency**, including client, transport, and server overhead.
* Do **not** interpret these numbers as raw model inference speed; they measure **realistic request performance**.
* Use `BENCH_CONCURRENCY` to test throughput under multiple simultaneous requests.
* Adjust `BENCH_PROMPT` and model size to evaluate different scenarios.
* If you compare gRPC against REST, keep `n_predict` / `GenerationMaxNewTokens` and decoding policy aligned first. Different decode defaults can dominate the results.

---
