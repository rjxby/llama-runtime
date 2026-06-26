#include "llama_adapter.h"
#include <assert.h>
#include <nlohmann/json.hpp>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>

static int is_valid_json_object(const char *output) {
  if (!output) {
    return 0;
  }

  const auto parsed = nlohmann::json::parse(output, nullptr, false);
  return !parsed.is_discarded() && parsed.is_object();
}

static void assert_ok(int rc, const char *msg) {
  if (rc != LLAMA_ADAPTER_OK) {
    fprintf(stderr, "FAIL: %s (code=%d)\n", msg, rc);
    exit(1);
  }
}

static int has_valid_usage(const char *output,
                           const llama_adapter_infer_result_t *result) {
  if (!output || !result) {
    return 0;
  }

  if (result->prompt_tokens <= 0 || result->output_tokens <= 0 ||
      result->total_tokens != result->prompt_tokens + result->output_tokens ||
      result->output_bytes <= 0) {
    return 0;
  }

  return result->output_bytes == (int32_t)strlen(output);
}

static bool always_abort(void *) { return true; }

int main(int argc, char **argv) {
  if (argc < 2) {
    fprintf(stderr, "Usage: %s <model-file>\n", argv[0]);
    return 1;
  }
  const char *model_path = argv[1];

  char version_str[128] = {0};
  int rc = llama_adapter_get_version(version_str, sizeof(version_str));
  assert_ok(rc, "adapter_get_version");
  printf("Adapter version: %s\n", version_str);

  const char *expected_version = getenv("LLAMA_VERSION");
  if (expected_version && strcmp(version_str, expected_version) != 0) {
    fprintf(stderr, "FAIL: adapter version mismatch (expected=%s actual=%s)\n",
            expected_version, version_str);
    return 1;
  }

  void *model = NULL;
  rc = llama_load_model(model_path, &model);
  assert_ok(rc, "load_model");
  assert(model != NULL);

  void *ctx = NULL;
  rc = llama_create_context(model, 4096, 512, 512, &ctx);
  assert_ok(rc, "create_context");
  assert(ctx != NULL);

  char output[4096] = {0};
  llama_adapter_generation_params_t params = {
      512, 0.0f, 1.0f, 0xFFFFFFFFu, LLAMA_ADAPTER_RESPONSE_FORMAT_TEXT,
      nullptr};
  llama_adapter_infer_result_t result = {0};
  rc = llama_infer(ctx, "Now tell me a short joke.", &params,
                   output, sizeof(output), &result);
  assert_ok(rc, "infer 1");
  printf("Inference 1 (truncated): %.200s\n", output);
  printf("Inference 1 usage: prompt=%d output=%d total=%d bytes=%d\n",
         result.prompt_tokens, result.output_tokens, result.total_tokens,
         result.output_bytes);

  if (!has_valid_usage(output, &result)) {
    fprintf(stderr, "FAIL: invalid inference usage stats (1)\n");
    return 1;
  }

  llama_adapter_generation_params_t aborted_params = {
      16, 0.0f, 1.0f, 0xFFFFFFFFu, LLAMA_ADAPTER_RESPONSE_FORMAT_TEXT,
      nullptr, always_abort, nullptr};
  rc = llama_infer(ctx, "This request should abort.", &aborted_params, output,
                   sizeof(output), &result);
  if (rc != LLAMA_ADAPTER_ERR_CANCELLED) {
    fprintf(stderr, "FAIL: aborted inference returned code=%d\n", rc);
    return 1;
  }

  rc = llama_context_reset(ctx);
  assert_ok(rc, "context_reset");

  memset(output, 0, sizeof(output));
  result = {0};
  rc = llama_infer(ctx, "Now tell me a short joke.", &params, output,
                   sizeof(output), &result);
  assert_ok(rc, "infer 2");
  printf("Inference 2 (truncated): %.200s\n", output);
  printf("Inference 2 usage: prompt=%d output=%d total=%d bytes=%d\n",
         result.prompt_tokens, result.output_tokens, result.total_tokens,
         result.output_bytes);

  if (!has_valid_usage(output, &result)) {
    fprintf(stderr, "FAIL: invalid inference usage stats (2)\n");
    return 1;
  }

  if (result.output_tokens <= 1) {
    fprintf(stderr,
            "FAIL: baseline inference did not produce enough tokens for max_new_tokens cap check (output=%d)\n",
            result.output_tokens);
    return 1;
  }

  rc = llama_context_reset(ctx);
  assert_ok(rc, "context_reset max one token");

  memset(output, 0, sizeof(output));
  result = {0};
  llama_adapter_generation_params_t one_token_params = {
      1, 0.0f, 1.0f, 0xFFFFFFFFu, LLAMA_ADAPTER_RESPONSE_FORMAT_TEXT,
      nullptr};
  rc = llama_infer(ctx, "Now tell me a short joke.", &one_token_params, output,
                   sizeof(output), &result);
  assert_ok(rc, "infer max one token");
  printf("Inference max one token (truncated): %.200s\n", output);
  printf("Inference max one token usage: prompt=%d output=%d total=%d bytes=%d\n",
         result.prompt_tokens, result.output_tokens, result.total_tokens,
         result.output_bytes);

  if (!has_valid_usage(output, &result) || result.output_tokens != 1 ||
      result.output_bytes <= 0) {
    fprintf(stderr,
            "FAIL: max_new_tokens=1 did not produce exactly one non-empty output token (output=%d bytes=%d)\n",
            result.output_tokens, result.output_bytes);
    return 1;
  }

  rc = llama_context_reset(ctx);
  assert_ok(rc, "context_reset sampled");

  memset(output, 0, sizeof(output));
  result = {0};
  llama_adapter_generation_params_t sampled_params = {
      8, 0.7f, 0.9f, 123u, LLAMA_ADAPTER_RESPONSE_FORMAT_TEXT, nullptr};
  rc = llama_infer(ctx, "Name one color.", &sampled_params, output,
                   sizeof(output), &result);
  assert_ok(rc, "infer sampled");
  printf("Inference sampled (truncated): %.200s\n", output);
  printf("Inference sampled usage: prompt=%d output=%d total=%d bytes=%d\n",
         result.prompt_tokens, result.output_tokens, result.total_tokens,
         result.output_bytes);

  if (!has_valid_usage(output, &result)) {
    fprintf(stderr, "FAIL: invalid sampled inference usage stats\n");
    return 1;
  }

  rc = llama_context_reset(ctx);
  assert_ok(rc, "context_reset json");

  memset(output, 0, sizeof(output));
  result = {0};
  static const char *json_object_grammar = R"(root ::= object
array ::= "[" space ( value ("," space value)* )? "]" space
boolean ::= ("true" | "false") space
char ::= [^"\\\x7F\x00-\x1F] | [\\] (["\\bfnrt] | "u" [0-9a-fA-F]{4})
decimal-part ::= [0-9]{1,16}
integral-part ::= [0] | [1-9] [0-9]{0,15}
null ::= "null" space
number ::= ("-"? integral-part) ("." decimal-part)? ([eE] [-+]? integral-part)? space
object ::= "{" space ( string ":" space value ("," space string ":" space value)* )? "}" space
space ::= | " " | "\n"{1,2} [ \t]{0,20}
string ::= "\"" char* "\"" space
value ::= object | array | string | number | boolean | null
)";
  llama_adapter_generation_params_t json_params = {
      64, 0.0f, 1.0f, 0xFFFFFFFFu,
      LLAMA_ADAPTER_RESPONSE_FORMAT_GRAMMAR, json_object_grammar};
  rc = llama_infer(ctx,
                   "Return exactly this JSON object: {\"ok\":true}",
                   &json_params, output, sizeof(output), &result);
  assert_ok(rc, "infer grammar");
  printf("Inference grammar JSON object (truncated): %.200s\n", output);
  printf("Inference grammar usage: prompt=%d output=%d total=%d bytes=%d\n",
         result.prompt_tokens, result.output_tokens, result.total_tokens,
         result.output_bytes);

  if (!has_valid_usage(output, &result)) {
    fprintf(stderr, "FAIL: invalid grammar usage stats\n");
    return 1;
  }

  if (!is_valid_json_object(output)) {
    fprintf(stderr,
            "FAIL: grammar inference returned success with invalid JSON: %.500s\n",
            output);
    return 1;
  }

  llama_adapter_generation_params_t missing_grammar_params = {
      16, 0.0f, 1.0f, 0xFFFFFFFFu, LLAMA_ADAPTER_RESPONSE_FORMAT_GRAMMAR,
      nullptr};
  rc = llama_infer(ctx, "Hello", &missing_grammar_params, output,
                   sizeof(output), &result);
  if (rc != LLAMA_ADAPTER_ERR_INVALID_ARG) {
    fprintf(stderr, "FAIL: missing grammar returned code=%d\n", rc);
    return 1;
  }

  llama_adapter_generation_params_t invalid_params = {
      16, 0.0f, 1.0f, 0xFFFFFFFFu, 999, nullptr};
  rc = llama_infer(ctx, "Hello", &invalid_params, output, sizeof(output),
                   &result);
  if (rc != LLAMA_ADAPTER_ERR_INVALID_ARG) {
    fprintf(stderr, "FAIL: invalid response format returned code=%d\n", rc);
    return 1;
  }

  rc = llama_remove_context(ctx);
  assert_ok(rc, "remove_context");
  rc = llama_unload_model(model);
  assert_ok(rc, "unload_model");

  printf("Integration test passed\n");
  return 0;
}
