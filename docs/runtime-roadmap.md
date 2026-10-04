# Llama Runtime roadmap

This document tracks planned work. Speculative decoding is not implemented; the current runtime reports it as unavailable.

See [architecture](architecture.md#scope) for current responsibilities, caller-owned policies, and the single-model boundary. Multi-model hosting would require a separate architecture decision covering loading, eviction, isolation, and capability discovery.

## Step 1: Speculative decoding

### Goal

Improve latency while keeping the external runtime contract stable.

### Affected areas

- model loader
- generation engine
- configuration
- metrics
- benchmarks
- runtime trace

### Implementation intent

- Add optional draft-model support and speculative token acceptance accounting.
- Keep speculative decoding behind runtime configuration and capability reporting.
- Fall back to normal decoding predictably when speculative mode is unavailable or ineffective.
- Preserve the same request and response contract regardless of whether speculation is enabled.
- Surface speculative usage and acceptance data through metrics and runtime trace without leaking implementation complexity into caller behavior.

### Dependencies

- Depends on the existing trace normalization and structured-output enforcement.
- Should integrate with the existing capability-discovery surface so callers know whether speculation exists for the loaded model/runtime configuration.
- Must be validated alongside structured output to ensure JSON-enforced modes still behave correctly when speculation is enabled.

### Verification

Runtime can:

- run with and without speculative mode
- expose speculative metrics
- preserve behavior when falling back to normal decoding
- benchmark interaction with structured-output modes

## Step 2: Runtime hardening

### Goal

Make the runtime reliable as a reusable worker for proxy integration and operational use.

### Affected areas

- health checks
- startup and shutdown
- configuration loading
- logs
- metrics
- error normalization

### Implementation intent

- Keep readiness tied to successful model load and required startup warm-up.
- Report loaded-model diagnostics clearly enough for upstream systems to distinguish loading, failed, degraded, and ready states.
- Stabilize configuration schema and validation so invalid runtime settings fail fast and predictably.
- Normalize request failures into stable error codes and messages that proxy integrations can classify without parsing ad hoc text.
- Ensure shutdown behavior is explicit and observable, including model unload and request rejection during stopping states.

### Dependencies

- Builds on the existing normalized error, trace, and capability surfaces.
- Should be completed before treating the runtime contract as stable for broad proxy reuse.

### Verification

Runtime can:

- start cleanly
- report loaded-model diagnostics
- fail predictably with normalized error codes and messages
- expose enough health and diagnostic information for proxy integration

## Open questions / deferred items

- Multi-model hosting is deferred. If required later, it should be specified as a separate roadmap item that covers loading policy, eviction policy, memory accounting, request routing, and per-model capability lookup.
- Tool-call normalization is deferred to the proxy. If a later design moves normalized tool-call parsing into runtime, that should be introduced as a separate contract change.
- The current error trailers and codes are defined in [RuntimeErrorMetadata](../src/LlamaRuntime.Presentation.Grpc/Services/RuntimeErrorMetadata.cs). Future hardening must preserve that contract or make a deliberate compatibility change.
- The exact benchmark matrix can remain implementation-defined so long as it covers baseline text generation and speculative decoding where supported.
