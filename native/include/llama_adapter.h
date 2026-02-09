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
    LLAMA_ADAPTER_ERR_UNKNOWN = 100
} llama_adapter_error_t;

LLAMA_ADAPTER_API int llama_adapter_get_version(char* out, size_t out_size) noexcept;
LLAMA_ADAPTER_API int llama_load_model(const char* path, void** model_out) noexcept;
LLAMA_ADAPTER_API int llama_unload_model(void* model) noexcept;
LLAMA_ADAPTER_API int llama_create_context(void* model, void** ctx_out) noexcept;
LLAMA_ADAPTER_API int llama_remove_context(void* ctx) noexcept;
LLAMA_ADAPTER_API int llama_infer(void* model, void* ctx,
                                  const char* prompt,
                                  char* out,
                                  size_t out_size) noexcept;

#ifdef __cplusplus
}
#endif

#endif // LLAMA_ADAPTER_H
