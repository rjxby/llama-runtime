#pragma once

#include "llama.h"
#include <cstddef>
#include <cstdint>
#include <fstream>
#include <string>
#include <vector>

namespace llama_adapter {

enum class Error {
  OK = 0,
  INVALID_ARG,
  LOAD_MODEL,
  OUT_OF_MEMORY,
  IO,
  UNKNOWN,
};

struct GenParams {
  int max_new_tokens = 128;
};

class Adapter {
public:
  Adapter() noexcept;
  ~Adapter() noexcept;

  // Model lifecycle
  Error load_model(const char *path);
  Error create_context(); // creates ctx_ from model_ using default params
  void free_context();
  void free_model();

  // Basic operations
  bool tokenize(const char *prompt, std::vector<llama_token> &tokens);
  bool decode(const std::vector<llama_token> &tokens);
  bool generate(std::string &out, size_t limit, const GenParams &params);

  // Convenience wrapper
  Error infer(const char *prompt, char *out, size_t out_size,
              const GenParams &params);

  // Helper to find meta.json
  static bool find_meta_json(std::string &result);

private:
  void reset();

  llama_model *model_ = nullptr;
  llama_context *ctx_ = nullptr;
  llama_context_params ctx_params_{};

  int ctx_n_ctx_ = 0;
  int n_past_ = 0;
};

} // namespace llama_adapter
