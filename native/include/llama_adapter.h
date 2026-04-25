#ifndef LLAMA_ADAPTER_H
#define LLAMA_ADAPTER_H

#include <stddef.h>
#include <stdint.h>

#ifdef __cplusplus
extern "C" {
#endif

#if defined(_WIN32)
#if defined(LLAMA_ADAPTER_BUILD)
#define LLAMA_ADAPTER_API __declspec(dllexport)
#else
#define LLAMA_ADAPTER_API __declspec(dllimport)
#endif
#else
#define LLAMA_ADAPTER_API __attribute__((visibility("default")))
#endif

typedef enum {
  LLAMA_ADAPTER_OK = 0,
  LLAMA_ADAPTER_ERR_INVALID_ARG = 1,
  LLAMA_ADAPTER_ERR_NOT_INITIALIZED = 2,
  LLAMA_ADAPTER_ERR_LOAD_MODEL = 3,
  LLAMA_ADAPTER_ERR_OUT_OF_MEMORY = 4,
  LLAMA_ADAPTER_ERR_INFER = 5,
  LLAMA_ADAPTER_ERR_NOT_IMPLEMENTED = 6,
  LLAMA_ADAPTER_ERR_NOT_FOUND = 7,
  LLAMA_ADAPTER_ERR_IO = 8,
  LLAMA_ADAPTER_ERR_BUFFER_TOO_SMALL = 9,
  LLAMA_ADAPTER_ERR_UNKNOWN = 100
} llama_adapter_error_t;

typedef struct {
  int32_t max_new_tokens;
  float temperature;
  float top_p;
  uint32_t seed;
} llama_adapter_generation_params_t;

typedef struct {
  int32_t prompt_tokens;
  int32_t output_tokens;
  int32_t total_tokens;
  int32_t output_bytes;
} llama_adapter_infer_result_t;

typedef struct {
  int32_t training_context_size;
  int32_t tokenizer_type;
} llama_adapter_model_metadata_t;

typedef struct {
  int32_t context_size;
} llama_adapter_context_metadata_t;

LLAMA_ADAPTER_API int llama_adapter_get_version(char *out,
                                                size_t out_size) noexcept;
LLAMA_ADAPTER_API int llama_load_model(const char *path,
                                       void **model_out) noexcept;
LLAMA_ADAPTER_API int llama_model_get_metadata(
    void *model, llama_adapter_model_metadata_t *metadata_out) noexcept;
LLAMA_ADAPTER_API int llama_unload_model(void *model) noexcept;
LLAMA_ADAPTER_API int llama_create_context(void *model, int n_ctx, int n_batch,
                                           int generation_max_new_tokens,
                                           void **ctx_out) noexcept;
LLAMA_ADAPTER_API int llama_context_get_metadata(
    void *ctx, llama_adapter_context_metadata_t *metadata_out) noexcept;
LLAMA_ADAPTER_API int llama_remove_context(void *ctx) noexcept;
LLAMA_ADAPTER_API int llama_context_reset(void *ctx) noexcept;
LLAMA_ADAPTER_API int llama_count_tokens(void *ctx, const char *prompt,
                                         int32_t *token_count) noexcept;
LLAMA_ADAPTER_API int llama_infer(
    void *ctx, const char *prompt,
    const llama_adapter_generation_params_t *params,
    char *out, size_t out_size,
    llama_adapter_infer_result_t *result) noexcept;

#ifdef __cplusplus
}
#endif

#endif
