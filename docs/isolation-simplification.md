# Isolation simplification verification

Verified on October 1, 2026 on macOS arm64. The runtime keeps one server process and shared model weights. Every concurrent request holds an exclusive native context with separate generation state and memory allocations. Contexts remain pooled and bounded by worker count. Native inference clears generation state before each request; no prompt caching or shared batching was introduced.

## Changes by stage

Counts cover production C#/C++ code and the native test build registration. They exclude tests and documentation. Stage diffs count moved code as deletions and additions, and later stages can replace earlier additions. The final production diff adds 454 lines and deletes 689, a net reduction of 235 lines.

| Stage | Added | Deleted | Verification |
| --- | ---: | ---: | --- |
| Atomic binding publication | 8 | 14 | Separate-process probe with a missing export, retry, 64 concurrent initializations, and real allocated stub pointers |
| Model-owned pool and cleanup | 145 | 270 | Exclusive bounded leases, startup-context reuse, acquisition failure/cancellation, waiting acquisition during close, and creation racing close |
| Session disposal race repair | 33 | 23 | Disposal during blocked inference retains the lease and model until inference returns |
| Hosted lifecycle consolidation | 164 | 195 | Startup failure/cancellation, warm-up/readiness, queued and active cancellation, expired stop deadline, repeated disposal, and DI disposal after timeout |
| Decode result repair | 27 | 14 | Injected results 0, 1, 2, -1, and -2 in prefill and the final generated token, then seeded context reuse |
| Immutable prepared request | 61 | 108 | Same request/compiled-constraint object through the queue, invalid input before admission, final JSON validation, and warm-up through the prepared execution API |
| Remove pool reset | 4 | 18 | Managed lease tests and real native reuse after success, failure, cancellation, and incomplete JSON |
| Single generation tokenization | 28 | 22 | Exact budget fit, one token over, no decode on rejection, preserved prompt usage, native error 12, and unchanged gRPC trailers |
| One sampler chain | 7 | 49 | Greedy and seeded stochastic text/JSON, nested output, escaping, Unicode, incomplete output, buffer exhaustion, and native concurrency 2/4 |
| Worker error classification and metadata snapshot follow-up | 3 | 2 | Unexpected provider exceptions stop the host and remain observable; native aborts retain cancellation behavior without a signaled caller token |

Removed `ILlamaContextManager`, its implementation and dictionary, context priming, its DI registration, and `ModelLoaderWorker`. Removed acquisition reset, generation's preliminary token count, provider schema recompilation, manual sampling candidates, separate selection branches, and duplicate sampler acceptance. Sessions, final JSON validation, defensive native checks, bounded queue admission, and process-lifetime library loading remain.

The low-level native session still accepts native execution arguments. The provider and coordinator each expose one prepared generation API. `IEngineModel` now owns session acquisition and supports `IAsyncDisposable`; loaded model metadata is mandatory. Existing `llama.v2`, configuration names, authentication, readiness, error trailers, and exported native struct layouts remain unchanged. Native error value 12 extends the enum without renumbering existing values.

## Correctness checks

The final managed solution passed 92 engine tests and 98 host tests, including gRPC integration tests with an injected native adapter. The real native suite passed both executables against `stories15M-q4_0.gguf`.

```sh
dotnet test src/llama-runtime.slnx --no-restore --verbosity quiet -m:1 -nodeReuse:false -p:UseSharedCompilation=false
make native-integration-tests
```

The blocked-inference host test uses the real DI lifecycle, provider, model, and pool with a controllable native call. A 50 ms stop deadline finishes active and queued callers. DI disposal does not close the active context or model. Releasing native execution allows the worker-completion task to finish cleanup. Separate pool tests cover close during context creation and repeated cleanup.

The internal C++ decode-call seam changes no exported C ABI. Result 0 alone advances positions; result 1 and negative results fail; result 2 cancels. Both a final-token decode abort and cancellation immediately after the last successful decode return cancellation. Subsequent inference on the same context matches a fresh context with the same seed and settings.

Native checks also cover exact prompt/output budget boundaries, prompt counts on rejection, buffer exhaustion, nested escaped Unicode JSON, incomplete JSON, and independent contexts at concurrency 2/4. Pool tests prove allocation limits and exclusive leases. Native startup logs show exactly four context allocations in every package run, including concurrency 2/4, matching the configured limit. The service has one schema-compilation call before admission, and the coordinator/provider forward the resulting immutable object.

A fresh package built with `make pack PACKAGE_DIR=/tmp/llama-runtime-stage-results/stories/pkg` ran outside the checkout, with a copied stories model. It passed authentication rejection, warm-up/readiness, capabilities, generation, estimation/usage agreement, invalid prompt/schema, prompt-budget trailers, nested Unicode schema output, incomplete-JSON rejection, subsequent context reuse, concurrency 2/4, and graceful SIGTERM shutdown. Serving logs contained no validation credentials, request prompts, or generated content.

Each Gemma stage used its own newly built package outside the checkout. The baseline recheck reused the original package without rebuilding it. Artifacts and the temporary measurement client/driver are under `/private/tmp/llama-runtime-stage-results`. No benchmark dependency or project was added to the repository.

## Measurement conditions

- Mac14,9, Apple M2 Pro, 10 physical/logical CPU cores, 16 GiB memory; macOS 27.0.1 build 26A434; .NET SDK 10.0.401.
- Existing upstream pin b10964 throughout. All measured packages use the same core library SHA-256 `ce2d0f2fb38474690394ae3ad5318a6136b1867c7379320816f35a6754d4a6f5`.
- Gemma model `gemma-4-E2B-it-Q4_K_M.gguf`, SHA-256 `f3504b387ee0962b2b041cf3691b1520118822642d67c5294f85ea62c68614b3`.
- Stories model SHA-256 `6151b1929d7f5aa3385d9ddef3393e55587c0a55de661562322bc51dfda93a04`.
- Metal backend, 36/36 model layers offloaded, context 512, logical and effective physical batch 128, f16 K/V, automatic flash attention, KQV offload enabled. Library defaults select 4 generation and 4 batch CPU threads.
- Worker/context limit 4; timing concurrency 1; separate correctness checks at concurrency 2/4. Persistent gRPC connection over local HTTP/2. Warning-level managed logging; no payload logging.
- Configured output ceiling 32, inference buffer 65536 bytes, startup prompt `Hello`. Text reserves 16 tokens; strict JSON reserves 32. Temperature 0 and top-p 1 throughout. No library upgrade, caching, or batching change.
- Each text and JSON case has five warm-ups followed by 100 measured requests, repeated three times. Percentiles use the sorted 50th and 95th observations. Every text request produced 5 input and 16 output tokens; every JSON request produced 8 input and 6 output tokens. Each repetition therefore totals 500/1600 text input/output tokens and 800/600 JSON input/output tokens.
- Text prompt `Once upon a time`. JSON prompt `Return an object with answer yes.` and a strict object schema whose required `answer` string is constrained to `yes`.

The baseline follows the ownership, shutdown, and decode correctness repairs. Stage measurements then apply preparation, reset removal, tokenization removal, and sampler unification in order. These are short completion workloads, not a broad throughput or upstream-server comparison. Long prompts/reservations and upstream comparisons remain in the deferred benchmark work.

## Results

The accompanying [raw measurement record](isolation-measurements.json) preserves all repetitions and actual token counts. The table below reports medians across the three repetitions, in milliseconds.

| Package stage | Text p50 | Text p95 | JSON p50 | JSON p95 |
| --- | ---: | ---: | ---: | ---: |
| baseline | 284.02 | 285.46 | 236.38 | 281.20 |
| prepared | 281.62 | 312.26 | 242.63 | 243.92 |
| reset | 286.79 | 312.68 | 243.28 | 246.96 |
| tokenized | 280.74 | 312.03 | 242.91 | 246.85 |
| sampler | 280.26 | 285.69 | 242.75 | 247.17 |
| baseline-recheck | 280.87 | 310.45 | 243.80 | 247.39 |
| final | 289.56 | 314.56 | 242.57 | 245.80 |

Initial text p95 increased above 5% in intermediate stages. Rechecking the unchanged baseline package reproduced a similar increase. The final package was measured again immediately after that control. Its median text p95 was 314.56 ms versus 310.45 ms for the rechecked baseline, an increase of 1.3%. Median JSON p95 was 245.80 ms versus 247.39 ms, a decrease of 0.6%. This investigation distinguishes observed run variation from a repeatable code regression; the measurements do not establish a latency improvement. Output lengths stayed identical across stages.

Linux execution, worker-owned contexts, process-per-request execution, shared batching, caching, upstream upgrades, assembly rearrangements, and benchmark-runner redesign remain deferred. Existing Linux build code and unrelated backlog findings were retained.
