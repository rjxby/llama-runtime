# Project backlog

This document tracks open and deferred work for llama-runtime. Items have no committed schedule. Select one concern before implementation; listing an item here does not authorize a code or architecture change.

See [architecture](architecture.md#scope) for runtime responsibilities and caller-owned policies.

## Open work

### B-01: Speculative decoding

Status: Open. The current runtime reports speculative decoding as unavailable.

Add optional speculative decoding to reduce generation latency while preserving the request and response contract.

- Decide how draft-model ownership fits the [single-model boundary](architecture.md#runtime-contract) before implementation.
- Use the existing configuration and capability-discovery paths to enable and report support.
- Fall back predictably to normal decoding when speculation is unavailable or ineffective.
- Report speculative usage and token acceptance through metrics and runtime trace.
- Preserve structured-output enforcement and final JSON validation.

Completion criteria:

- Real-model checks cover enabled, disabled, and fallback behavior.
- Capability replies and runtime traces match the loaded configuration and actual execution.
- Text and schema-constrained JSON remain valid with speculation enabled.
- Benchmarks compare normal and speculative decoding with model hashes, runtime settings, and the upstream revision recorded.

### B-02: Remaining runtime hardening

Status: Open. Model lifecycle, warm-up readiness, configuration validation, normalized errors, and shutdown handling already exist. Review remaining gaps before choosing a fix.

- Check whether existing health responses and loaded-model diagnostics give proxy callers enough information to distinguish loading, warm-up, failure, readiness, and stopping.
- Check configuration validation and request-failure coverage for missing cases.
- Check shutdown observability, including request rejection and model cleanup.
- Keep operational logs free of prompts and generated content.

Completion criteria:

- Record concrete gaps against the existing [lifecycle](architecture.md#ownership-and-lifecycle) and [health contracts](architecture.md#configuration-and-health).
- Fix each selected gap separately with focused regression coverage.
- Preserve the error codes and trailers in [RuntimeErrorMetadata](../src/LlamaRuntime.Presentation.Grpc/Services/RuntimeErrorMetadata.cs), or agree on a compatibility change first.
- Verify any startup, native-loading, or gRPC inference changes with the [required smoke check](../AGENTS.md#grpc-smoke-verification).

### B-03: Linux runtime verification

Status: Open. Linux x64 and arm64 build targets exist, but execution is unverified.

Validate the existing Linux targets before claiming runtime compatibility or adding Linux release packages.

Completion criteria:

- On each target, run the documented dependency verification, native build, managed checks, packaging, native integration, and packaged gRPC smoke commands.
- Record platform details, the GGUF hash, runtime settings, the upstream revision, and request results.
- Report missing prerequisites as unverified checks and update [platform compatibility](architecture.md#compatibility) only for validated targets.

### B-04: Broader benchmark coverage

Status: Open.

Extend measurements to long prompts, larger output reservations, and the upstream REST baseline using the [existing benchmark commands](benchmarking.md).

Completion criteria:

- Record model hashes, runtime settings, the upstream revision, concurrency, and actual token counts.
- Retain raw results and request-failure counts alongside latency and throughput summaries.
- Keep inference quality and performance measurements separate from deterministic managed checks.
- Include normal and speculative decoding comparisons when B-01 is available.

## Deferred work

| ID | Item | Condition for reconsideration |
| --- | --- | --- |
| B-05 | Multi-model hosting | A concrete requirement and a separate architecture decision covering loading, eviction, memory accounting, isolation, request routing, and per-model capability lookup. |
| B-06 | Runtime tool-call normalization | A contract decision moving parsing from the proxy into the runtime. Tool execution remains caller-owned. |
| B-07 | Worker-owned contexts or process-per-request execution | Evidence that the current model-owned pool and exclusive session leases cannot meet a selected requirement. |
| B-08 | Shared batching or prompt caching | Measured workload evidence and an agreed design preserving request isolation and bounded resource use. |
| B-09 | Upstream revision changes | A selected compatibility or capability need, followed by the documented pin, checksum, native integration, and managed verification workflow. |
| B-10 | Assembly rearrangement or benchmark-runner redesign | A concrete limitation that cannot be solved within the existing project boundaries or benchmark commands. |
