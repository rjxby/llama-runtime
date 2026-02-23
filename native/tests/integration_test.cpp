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

  void *model = NULL;
  rc = llama_load_model(model_path, &model);
  assert_ok(rc, "load_model");
  assert(model != NULL);

  void *ctx = NULL;
  rc = llama_create_context(model, 4096, 512, &ctx);
  assert_ok(rc, "create_context");
  assert(ctx != NULL);

  char output[4096] = {0};
  rc = llama_infer(ctx, "Hello! Tell me a short sentence about llamas.", output,
                   sizeof(output));
  assert_ok(rc, "infer 1");
  printf("Inference 1 (truncated): %.200s\n", output);

  if (strlen(output) == 0) {
    fprintf(stderr, "FAIL: empty inference output (1)\n");
    return 1;
  }

  rc = llama_context_reset(ctx);
  assert_ok(rc, "context_reset");

  memset(output, 0, sizeof(output));
  rc = llama_infer(ctx, "Now tell me a short joke.", output, sizeof(output));
  assert_ok(rc, "infer 2");
  printf("Inference 2 (truncated): %.200s\n", output);

  if (strlen(output) == 0) {
    fprintf(stderr, "FAIL: empty inference output (2)\n");
    return 1;
  }

  rc = llama_remove_context(ctx);
  assert_ok(rc, "remove_context");
  rc = llama_unload_model(model);
  assert_ok(rc, "unload_model");

  printf("Integration test passed\n");
  return 0;
}
