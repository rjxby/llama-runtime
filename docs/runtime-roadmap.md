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

Because of that, Step 1 should not introduce a request-level model selector. Doing so would imply a multi-model architecture that does not exist today and would blur the boundary between runtime execution and upstream routing.

If future requirements demand multi-model hosting in one runtime process, that should be handled as a separate architecture change with explicit loading, eviction, isolation, and capability-discovery rules.

## Step 1: Simple runtime proto

### Goal

Define a simpler runtime request/response contract that matches the current runtime role and architecture.

### Affected areas

- runtime proto
- runtime DTOs
- request parser
- response builder
- tests
- proxy runtime adapter

### Contract decisions

- `GenerateRequest` accepts a prompt string, not a message array.
- `GenerateRequest` does not include a request-level `model` field in the current single-model architecture.
- `GenerateRequest` does not include `tools` or `tool_choice`; those belong to the proxy/orchestrator layer because tool rendering is model- and template-specific prompt policy.
- `GenerateReply` includes the loaded model identity.
- `GetCapabilities` becomes the authoritative source for model metadata and supported runtime features.
- Step 1 returns generated content without requiring the runtime to understand tool schemas or tool-selection policy.
- Any tool-call normalization or parsing beyond raw generated content is deferred to the proxy layer.
- Usage and runtime-trace fields should be normalized when available.

### Suggested proto shape

```proto
syntax = "proto3";

package llama.v2;

service Generator {
  rpc Generate (GenerateRequest) returns (GenerateReply);
  rpc EstimateTokens (EstimateTokensRequest) returns (EstimateTokensReply);
  rpc GetCapabilities (GetCapabilitiesRequest) returns (GetCapabilitiesReply);
}

message GenerateRequest {
  string request_id = 1;
  string prompt = 2;
  ResponseFormat response_format = 3;
  GenerationOptions generation = 4;
}

message GenerateReply {
  string request_id = 1;
  string model = 2;
  string content = 3;
  Usage usage = 4;
  RuntimeTrace runtime_trace = 5;
}

message ResponseFormat {
  string type = 1; // text, json_object
}

message GenerationOptions {
  float temperature = 1;
  int32 max_output_tokens = 2;
  float top_p = 3;
}

message Usage {
  int32 input_tokens = 1;
  int32 output_tokens = 2;
  int32 total_tokens = 3;
}

message RuntimeTrace {
  bool structured_output_applied = 1;
  bool structured_output_satisfied = 2;
  bool speculative_decoding_used = 3;
}

```

### Implementation intent

- Keep request parsing narrow: the runtime receives fully prepared prompt text from the caller.
- Keep tool definitions, tool prompting, and tool-choice policy in the proxy, where model-specific templates and prompt composition already belong.
- Budget input tokens against the final prompt string only; the runtime should not carry hidden tool-rendering overhead that the caller cannot see.
- Map provider- and runtime-specific errors into normalized trailer metadata plus transport-level status.
- Return the loaded model identifier in `GenerateReply` so logs, diagnostics, and proxy caches can correlate responses with the active model.
- Add `GetCapabilities` in the same contract revision so clients do not infer feature support from hard-coded assumptions.
- Leave any parsing of model-emitted tool-call text to the proxy, which already owns tool policy and model-specific prompting.

### Dependencies

- This step establishes the public surface that later steps extend.
- Step 2 depends on the `GetCapabilities` method defined here.
- Step 3 and Step 4 depend on `response_format`, normalized `usage`, and `runtime_trace`.

### Verification

Runtime can:

- accept a prompt string
- return normal text output
- accept structured-output settings
- return normalized usage and trace fields
- avoid requiring a request model selector
- avoid requiring separate tool metadata in the request

## Step 2: Capability discovery

### Goal

Make runtime feature support explicit so the proxy can discover behavior from the loaded model/runtime pair instead of assuming it.

### Affected areas

- runtime API
- startup
- model registry or model metadata surface
- diagnostics
- proxy cache integration

### Capability fields

Capability discovery should report at least:

- model id
- context size
- supports structured output
- supports JSON object output
- supports speculative decoding
- tokenizer family

### Implementation intent

- Build capabilities from the actually loaded model instance and the runtime features enabled for that process.
- Treat the capability payload as cacheable by the proxy, but invalidate it if the loaded model changes.
- Keep capability values descriptive, not policy-bearing. For example, report whether runtime-level JSON object enforcement is supported, not whether a caller should use it.
- Expose enough startup diagnostics to explain why a capability is false when the runtime is otherwise healthy.
- Do not use capability discovery to publish tool-prompt templates or tool-selection policy; those remain proxy concerns.

### Dependencies

- Depends on Step 1 introducing `GetCapabilities`.
- Should land before proxy-side feature negotiation is simplified.

### Verification

Proxy can:

- discover capabilities automatically instead of assuming them
- receive a capability payload that matches the actually loaded model

## Step 3: Structured output

### Goal

Support reliable machine-readable output while keeping enforcement internals hidden behind the runtime contract.

### Affected areas

- generation pipeline
- llama.cpp option mapping
- validation helpers
- runtime trace
- tests
- benchmarks

### Supported external contract values

- `text`
- `json_object`

### Implementation intent

- Implement structured output as a runtime-level enforcement mechanism layered on top of llama.cpp; callers should depend on the runtime contract, not on a llama.cpp-native response-format API.
- Allow internal decoding constraints or post-generation validation if needed, without exposing those internals in the public API.
- Validate JSON-object output before returning success to the caller when `response_format.type` is `json_object`.
- Use normalized trace and error fields to report whether structured-output enforcement was applied and whether enforcement succeeded or failed.
- Record structured-output outcome explicitly in `RuntimeTrace` so successful enforcement, bypassed enforcement, and failed enforcement are distinguishable.

### Dependencies

- Depends on Step 1 response-format and trace fields.
- Benefits from Step 2 capability discovery so callers can avoid unsupported modes.

### Verification

Runtime can:

- enforce JSON output
- validate JSON-object replies
- report structured-output success or failure through normalized trace and error data
- benchmark structured-output behavior per model and config

## Step 4: Speculative decoding

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

- Depends on Step 1 trace normalization.
- Should integrate with Step 2 capability discovery so callers know whether speculation exists for the loaded model/runtime configuration.
- Must be validated alongside Step 3 to ensure structured output still behaves correctly when speculation is enabled.

### Verification

Runtime can:

- run with and without speculative mode
- expose speculative metrics
- preserve behavior when falling back to normal decoding
- benchmark interaction with structured-output modes

## Step 5: Runtime hardening

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

- Builds on the normalized error and trace surfaces introduced earlier.
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
- The exact benchmark matrix can remain implementation-defined so long as it covers baseline text generation, structured output, and speculative decoding where supported.
