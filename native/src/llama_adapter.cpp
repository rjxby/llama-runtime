#include "llama_adapter.h"
#include "llama_adapter_core.h"

using llama_adapter::Adapter;

extern "C" {

int llama_adapter_get_version(char* out, size_t out_size) noexcept {
    if (!out || out_size == 0) return LLAMA_ADAPTER_ERR_INVALID_ARG;

    try {
        std::string meta_path;
        if (!llama_adapter::Adapter::find_meta_json(meta_path)) {
            if (out && out_size) out[0] = '\0';
            return LLAMA_ADAPTER_ERR_NOT_FOUND;
        }

        std::ifstream ifs(meta_path);
        if (!ifs.good()) {
            if (out && out_size) out[0] = '\0';
            return LLAMA_ADAPTER_ERR_IO;
        }

        std::string json((std::istreambuf_iterator<char>(ifs)), {});
        auto p = json.find("\"version\"");
        if (p == std::string::npos) return LLAMA_ADAPTER_ERR_IO;

        p = json.find('"', json.find(':', p));
        auto q = json.find('"', p + 1);
        if (p == std::string::npos || q == std::string::npos) return LLAMA_ADAPTER_ERR_IO;

        std::string version = json.substr(p + 1, q - p - 1);

        if (version.size() + 1 > out_size) return LLAMA_ADAPTER_ERR_INVALID_ARG;
        std::memcpy(out, version.c_str(), version.size());
        out[version.size()] = '\0';

        return LLAMA_ADAPTER_OK;
    } catch (...) {
        if (out && out_size) out[0] = '\0';
        return LLAMA_ADAPTER_ERR_UNKNOWN;
    }
}

int llama_load_model(const char* path, void** model_out) noexcept {
    if (!path || !model_out) return LLAMA_ADAPTER_ERR_INVALID_ARG;
    try {
        Adapter* adapter = new Adapter();
        if (adapter->load_model(path) != llama_adapter::Error::OK) {
            delete adapter;
            return LLAMA_ADAPTER_ERR_LOAD_MODEL;
        }
        *model_out = adapter;
        return LLAMA_ADAPTER_OK;
    } catch (...) {
        return LLAMA_ADAPTER_ERR_UNKNOWN;
    }
}

int llama_unload_model(void* model) noexcept {
    if (!model) return LLAMA_ADAPTER_ERR_INVALID_ARG;
    Adapter* adapter = static_cast<Adapter*>(model);
    delete adapter;
    return LLAMA_ADAPTER_OK;
}

int llama_create_context(void* model, void** ctx_out) noexcept {
    if (!model || !ctx_out) return LLAMA_ADAPTER_ERR_INVALID_ARG;
    Adapter* adapter = static_cast<Adapter*>(model);
    if (adapter->create_context() != llama_adapter::Error::OK) return LLAMA_ADAPTER_ERR_LOAD_MODEL;
    *ctx_out = adapter;
    return LLAMA_ADAPTER_OK;
}

int llama_remove_context(void* ctx) noexcept {
    if (!ctx) return LLAMA_ADAPTER_ERR_INVALID_ARG;
    Adapter* adapter = static_cast<Adapter*>(ctx);
    adapter->free_context();
    return LLAMA_ADAPTER_OK;
}

int llama_infer(void* model, void* ctx, const char* prompt, char* out, size_t out_size) noexcept {
    if (!model || !ctx || !prompt || !out || out_size == 0) return LLAMA_ADAPTER_ERR_INVALID_ARG;
    Adapter* adapter = static_cast<Adapter*>(ctx);
    llama_adapter::GenParams params;
    return static_cast<int>(adapter->infer(prompt, out, out_size, params));
}

} // extern "C"
