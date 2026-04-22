#include "llama_adapter.h"
#include <assert.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>

static void assert_ok(int rc, const char *msg) {
  if (rc != LLAMA_ADAPTER_OK) {
    fprintf(stderr, "FAIL: %s (code=%d)\n", msg, rc);
    exit(1);
  }
}

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
  llama_adapter_generation_params_t params = {512, 0.0f, 1.0f, 0xFFFFFFFFu};
  llama_adapter_infer_result_t result = {0};
  rc = llama_infer(ctx, "Hello! Tell me a short sentence about llamas.", &params,
                   output, sizeof(output), &result);
  assert_ok(rc, "infer 1");
  printf("Inference 1 (truncated): %.200s\n", output);
  printf("Inference 1 usage: prompt=%d output=%d total=%d bytes=%d\n",
         result.prompt_tokens, result.output_tokens, result.total_tokens,
         result.output_bytes);

  if (strlen(output) == 0) {
    fprintf(stderr, "FAIL: empty inference output (1)\n");
    return 1;
  }
  if (result.prompt_tokens <= 0 || result.output_tokens <= 0 ||
      result.total_tokens != result.prompt_tokens + result.output_tokens) {
    fprintf(stderr, "FAIL: invalid inference usage stats (1)\n");
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

  if (strlen(output) == 0) {
    fprintf(stderr, "FAIL: empty inference output (2)\n");
    return 1;
  }
  if (result.prompt_tokens <= 0 || result.output_tokens <= 0 ||
      result.total_tokens != result.prompt_tokens + result.output_tokens) {
    fprintf(stderr, "FAIL: invalid inference usage stats (2)\n");
    return 1;
  }

  rc = llama_remove_context(ctx);
  assert_ok(rc, "remove_context");
  rc = llama_unload_model(model);
  assert_ok(rc, "unload_model");

  printf("Integration test passed\n");
  return 0;
}
