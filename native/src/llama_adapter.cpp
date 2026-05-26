#include "llama_adapter.h"
#include "llama_adapter_core.h"

#include <cstddef>
#include <cstring>

static_assert(offsetof(llama_adapter_generation_params_t, max_new_tokens) == 0,
              "max_new_tokens offset changed");
static_assert(offsetof(llama_adapter_generation_params_t, response_format) >
                  offsetof(llama_adapter_generation_params_t, seed),
              "response_format must follow generation fields");
static_assert(offsetof(llama_adapter_generation_params_t, grammar) >
                  offsetof(llama_adapter_generation_params_t,
                           response_format),
              "grammar must follow response_format");

namespace {

int to_public_error(llama_adapter::Error error) noexcept {
  switch (error) {
  case llama_adapter::Error::OK:
    return LLAMA_ADAPTER_OK;
  case llama_adapter::Error::INVALID_ARG:
    return LLAMA_ADAPTER_ERR_INVALID_ARG;
  case llama_adapter::Error::LOAD_MODEL:
    return LLAMA_ADAPTER_ERR_LOAD_MODEL;
  case llama_adapter::Error::OUT_OF_MEMORY:
    return LLAMA_ADAPTER_ERR_OUT_OF_MEMORY;
  case llama_adapter::Error::IO:
    return LLAMA_ADAPTER_ERR_IO;
  case llama_adapter::Error::BUFFER_TOO_SMALL:
    return LLAMA_ADAPTER_ERR_BUFFER_TOO_SMALL;
  case llama_adapter::Error::UNKNOWN:
  default:
    return LLAMA_ADAPTER_ERR_UNKNOWN;
  }
}

llama_adapter::GenParams default_generation_params(
    const llama_adapter::Context *native_context) {
  llama_adapter::GenParams generation_params;
  generation_params.max_new_tokens =
      native_context->generation_max_new_tokens();
  generation_params.temperature = 0.0f;
  generation_params.top_p = 1.0f;
  generation_params.seed = LLAMA_DEFAULT_SEED;
  generation_params.response_format = LLAMA_ADAPTER_RESPONSE_FORMAT_TEXT;
  generation_params.grammar.clear();
  return generation_params;
}

int apply_generation_params(
    llama_adapter::GenParams &generation_params,
    const llama_adapter_generation_params_t *params) {
  if (!params)
    return LLAMA_ADAPTER_OK;

  generation_params.max_new_tokens = params->max_new_tokens > 0
                                         ? params->max_new_tokens
                                         : generation_params.max_new_tokens;
  generation_params.temperature = params->temperature;
  generation_params.top_p = params->top_p;
  generation_params.seed = params->seed;

  switch (params->response_format) {
  case LLAMA_ADAPTER_RESPONSE_FORMAT_TEXT:
  case LLAMA_ADAPTER_RESPONSE_FORMAT_GRAMMAR:
    generation_params.response_format =
        static_cast<llama_adapter_response_format_t>(params->response_format);
    break;
  default:
    return LLAMA_ADAPTER_ERR_INVALID_ARG;
  }

  if (params->grammar)
    generation_params.grammar = params->grammar;
  if (generation_params.response_format == LLAMA_ADAPTER_RESPONSE_FORMAT_GRAMMAR &&
      generation_params.grammar.empty())
    return LLAMA_ADAPTER_ERR_INVALID_ARG;

  return LLAMA_ADAPTER_OK;
}

} // namespace

extern "C" {

int llama_adapter_get_version(char *out, size_t out_size) noexcept {
  if (!out || out_size == 0)
    return LLAMA_ADAPTER_ERR_INVALID_ARG;

  try {
    const char *version = llama_adapter::source_version();
    if (!version || version[0] == '\0') {
      out[0] = '\0';
      return LLAMA_ADAPTER_ERR_UNKNOWN;
    }

    const size_t version_len = std::strlen(version);
    if (version_len + 1 > out_size)
      return LLAMA_ADAPTER_ERR_BUFFER_TOO_SMALL;
    std::memcpy(out, version, version_len);
    out[version_len] = '\0';

    return LLAMA_ADAPTER_OK;
  } catch (...) {
    if (out && out_size)
      out[0] = '\0';
    return LLAMA_ADAPTER_ERR_UNKNOWN;
  }
}

int llama_load_model(const char *path, void **model_out) noexcept {
  if (!path || !model_out)
    return LLAMA_ADAPTER_ERR_INVALID_ARG;
  try {
    llama_adapter::Model *model = new llama_adapter::Model();
    if (model->load(path) != llama_adapter::Error::OK) {
      delete model;
      return LLAMA_ADAPTER_ERR_LOAD_MODEL;
    }
    *model_out = model;
    return LLAMA_ADAPTER_OK;
  } catch (...) {
    return LLAMA_ADAPTER_ERR_UNKNOWN;
  }
}

int llama_model_get_metadata(void *model,
                             llama_adapter_model_metadata_t *metadata_out) noexcept {
  if (!model || !metadata_out)
    return LLAMA_ADAPTER_ERR_INVALID_ARG;
  try {
    llama_adapter::Model *native_model =
        static_cast<llama_adapter::Model *>(model);
    const auto rc = native_model->metadata(metadata_out);
    return to_public_error(rc);
  } catch (...) {
    return LLAMA_ADAPTER_ERR_UNKNOWN;
  }
}

int llama_unload_model(void *model) noexcept {
  if (!model)
    return LLAMA_ADAPTER_ERR_INVALID_ARG;
  delete static_cast<llama_adapter::Model *>(model);
  return LLAMA_ADAPTER_OK;
}

int llama_create_context(void *model, int n_ctx, int n_batch,
                         int generation_max_new_tokens,
                         void **ctx_out) noexcept {
  if (!model || !ctx_out)
    return LLAMA_ADAPTER_ERR_INVALID_ARG;
  try {
    llama_adapter::Model *m = static_cast<llama_adapter::Model *>(model);
    llama_adapter::Context *ctx = new llama_adapter::Context(m);
    if (ctx->init(n_ctx, n_batch, generation_max_new_tokens) !=
        llama_adapter::Error::OK) {
      delete ctx;
      return LLAMA_ADAPTER_ERR_LOAD_MODEL;
    }
    *ctx_out = ctx;
    return LLAMA_ADAPTER_OK;
  } catch (...) {
    return LLAMA_ADAPTER_ERR_UNKNOWN;
  }
}

int llama_context_get_metadata(void *ctx,
                               llama_adapter_context_metadata_t *metadata_out) noexcept {
  if (!ctx || !metadata_out)
    return LLAMA_ADAPTER_ERR_INVALID_ARG;
  try {
    llama_adapter::Context *native_context =
        static_cast<llama_adapter::Context *>(ctx);
    const auto rc = native_context->metadata(metadata_out);
    return to_public_error(rc);
  } catch (...) {
    return LLAMA_ADAPTER_ERR_UNKNOWN;
  }
}

int llama_remove_context(void *ctx) noexcept {
  if (!ctx)
    return LLAMA_ADAPTER_ERR_INVALID_ARG;
  delete static_cast<llama_adapter::Context *>(ctx);
  return LLAMA_ADAPTER_OK;
}

int llama_context_reset(void *ctx) noexcept {
  if (!ctx)
    return LLAMA_ADAPTER_ERR_INVALID_ARG;
  llama_adapter::Context *native_context =
      static_cast<llama_adapter::Context *>(ctx);
  native_context->reset();
  return LLAMA_ADAPTER_OK;
}

int llama_count_tokens(void *ctx, const char *prompt,
                       int32_t *token_count) noexcept {
  if (!ctx || !prompt || !token_count)
    return LLAMA_ADAPTER_ERR_INVALID_ARG;

  llama_adapter::Context *native_context =
      static_cast<llama_adapter::Context *>(ctx);
  const auto rc = native_context->count_tokens(prompt, token_count);
  return to_public_error(rc);
}

int llama_infer(void *ctx, const char *prompt,
                const llama_adapter_generation_params_t *params, char *out,
                size_t out_size,
                llama_adapter_infer_result_t *result) noexcept {
  if (!ctx || !prompt)
    return LLAMA_ADAPTER_ERR_INVALID_ARG;
  llama_adapter::Context *native_context =
      static_cast<llama_adapter::Context *>(ctx);
  llama_adapter::GenParams generation_params =
      default_generation_params(native_context);
  const int params_rc = apply_generation_params(generation_params, params);
  if (params_rc != LLAMA_ADAPTER_OK)
    return params_rc;

  const auto rc =
      native_context->infer(prompt, out, out_size, result, generation_params);
  return to_public_error(rc);
}
}
