# Llama Runtime Roadmap

## Role of Llama Runtime

Llama Runtime is the thin inference worker. It owns:

- model loading
- inference execution
- token estimation
- structured output enforcement
- speculative decoding
- runtime capability reporting
- usage metrics and runtime trace metadata

Llama Runtime does not own:

- conversation structure
- routing and model selection policy
- context reduction policy
- prompt management policy
- tool execution
- agent loop state

The runtime should stay focused on inference-time behavior and expose a small, explicit contract that a proxy or orchestrator can depend on.

## Non-goals / ownership boundaries

This roadmap does not turn the runtime into a general orchestration layer. In particular:

- the runtime should not accept message-array conversation state as its primary input surface
- the runtime should not decide which model to use for a request
- the runtime should not execute tools or maintain multi-step tool loop state
- the runtime should not own prompt templating policy beyond minimal runtime hints exposed for integration

These boundaries keep the runtime reusable as a predictable worker behind a higher-level proxy.

## Current architecture constraints

The current implementation is intentionally single-model per process.

- The hosted model is selected at startup from configuration, not at request time.
- Startup loads one model, warms it, and keeps it resident until shutdown.
- Request routing always targets the already-loaded model instance.
- The runtime does not currently host multiple loaded models behind one serving endpoint.

Because of that, the runtime should not introduce a request-level model selector. Doing so would imply a multi-model architecture that does not exist today and would blur the boundary between runtime execution and upstream routing.

If future requirements demand multi-model hosting in one runtime process, that should be handled as a separate architecture change with explicit loading, eviction, isolation, and capability-discovery rules.

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
- The exact trailer error-code taxonomy should be defined during implementation, but it should be stable and integration-friendly once introduced.
- The exact benchmark matrix can remain implementation-defined so long as it covers baseline text generation and speculative decoding where supported.
