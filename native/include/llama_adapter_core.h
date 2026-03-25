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

class Model {
public:
  Model() noexcept = default;
  ~Model() noexcept;

  Error load(const char *path);
  void free();

  llama_model *handle() const { return model_; }
  const llama_vocab *vocab() const { return llama_model_get_vocab(model_); }

private:
  llama_model *model_ = nullptr;
};

class Context {
public:
  Context(Model *model) noexcept;
  ~Context() noexcept;

  Error init(int n_ctx, int n_batch, int max_tokens,
             int generation_max_new_tokens);
  int generation_max_new_tokens() const { return generation_max_new_tokens_; }
  void free();
  void reset();

  bool tokenize(const char *prompt, std::vector<llama_token> &tokens);
  Error count_tokens(const char *prompt, int32_t *token_count);
  bool decode(const std::vector<llama_token> &tokens);
  bool generate(std::string &out, size_t limit, const GenParams &params);

  Error infer(const char *prompt, char *out, size_t out_size,
              int32_t *out_written, const GenParams &params);

private:
  Model *model_ref_ = nullptr;
  llama_context *ctx_ = nullptr;

  int ctx_n_ctx_ = 0;
  int ctx_n_batch_ = 0;
  int max_tokens_ = 0;
  int generation_max_new_tokens_ = 128;
  int n_past_ = 0;
};

bool find_meta_json(std::string &result);

} // namespace llama_adapter
