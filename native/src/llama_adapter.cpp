#include "llama_adapter.h"
#include "llama_adapter_core.h"

extern "C" {

int llama_adapter_get_version(char *out, size_t out_size) noexcept {
  if (!out || out_size == 0)
    return LLAMA_ADAPTER_ERR_INVALID_ARG;

  try {
    std::string meta_path;
    if (!llama_adapter::find_meta_json(meta_path)) {
      if (out && out_size)
        out[0] = '\0';
      return LLAMA_ADAPTER_ERR_NOT_FOUND;
    }

    std::ifstream ifs(meta_path);
    if (!ifs.good()) {
      if (out && out_size)
        out[0] = '\0';
      return LLAMA_ADAPTER_ERR_IO;
    }

    std::string json((std::istreambuf_iterator<char>(ifs)), {});
    auto p = json.find("\"version\"");
    if (p == std::string::npos)
      return LLAMA_ADAPTER_ERR_IO;

    p = json.find('"', json.find(':', p));
    auto q = json.find('"', p + 1);
    if (p == std::string::npos || q == std::string::npos)
      return LLAMA_ADAPTER_ERR_IO;

    std::string version = json.substr(p + 1, q - p - 1);

    if (version.size() + 1 > out_size)
      return LLAMA_ADAPTER_ERR_INVALID_ARG;
    std::memcpy(out, version.c_str(), version.size());
    out[version.size()] = '\0';

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

int llama_unload_model(void *model) noexcept {
  if (!model)
    return LLAMA_ADAPTER_ERR_INVALID_ARG;
  delete static_cast<llama_adapter::Model *>(model);
  return LLAMA_ADAPTER_OK;
}

int llama_create_context(void *model, int n_ctx, int n_batch, int max_tokens,
                         void **ctx_out) noexcept {
  if (!model || !ctx_out)
    return LLAMA_ADAPTER_ERR_INVALID_ARG;
  try {
    llama_adapter::Model *m = static_cast<llama_adapter::Model *>(model);
    llama_adapter::Context *ctx = new llama_adapter::Context(m);
    if (ctx->init(n_ctx, n_batch, max_tokens) != llama_adapter::Error::OK) {
      delete ctx;
      return LLAMA_ADAPTER_ERR_LOAD_MODEL;
    }
    *ctx_out = ctx;
    return LLAMA_ADAPTER_OK;
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
  static_cast<llama_adapter::Context *>(ctx)->reset();
  return LLAMA_ADAPTER_OK;
}

int llama_infer(void *ctx, const char *prompt, char *out, size_t out_size,
                int32_t *out_written) noexcept {
  if (!ctx || !prompt)
    return LLAMA_ADAPTER_ERR_INVALID_ARG;
  llama_adapter::GenParams params;
  return static_cast<int>(static_cast<llama_adapter::Context *>(ctx)->infer(
      prompt, out, out_size, out_written, params));
}
}
