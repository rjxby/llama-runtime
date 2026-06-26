#pragma once

#include "llama.h"
#include "llama_adapter.h"
#include <cstddef>
#include <cstdint>
#include <string>
#include <vector>

namespace llama_adapter {

enum class Error {
  OK = 0,
  INVALID_ARG,
  LOAD_MODEL,
  OUT_OF_MEMORY,
  IO,
  BUFFER_TOO_SMALL,
  EMPTY_OUTPUT,
  ABORTED,
  UNKNOWN,
};

struct GenParams {
  int max_new_tokens = 128;
  float temperature = 0.0f;
  float top_p = 1.0f;
  uint32_t seed = LLAMA_DEFAULT_SEED;
  llama_adapter_response_format_t response_format =
      LLAMA_ADAPTER_RESPONSE_FORMAT_TEXT;
  std::string grammar;
  llama_adapter_abort_callback_t abort_callback = nullptr;
  void *abort_callback_data = nullptr;
};

class Model {
public:
  Model() noexcept = default;
  ~Model() noexcept;

  Error load(const char *path);
  Error metadata(llama_adapter_model_metadata_t *metadata) const;
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

  Error init(int n_ctx, int n_batch, int generation_max_new_tokens);
  Error metadata(llama_adapter_context_metadata_t *metadata) const;
  int generation_max_new_tokens() const { return generation_max_new_tokens_; }
  void free();
  void reset();

  bool tokenize(const char *prompt, std::vector<llama_token> &tokens);
  Error count_tokens(const char *prompt, int32_t *token_count);
  bool decode(const std::vector<llama_token> &tokens);
  bool invoke_abort_callback();
  bool is_abort_requested() const;
  bool sample_token(
      llama_sampler *sampler, llama_sampler *grammar_sampler,
      llama_token &token,
      std::vector<llama_token> &generated_tokens);
  bool generate_tokens(
      std::vector<llama_token> &generated_tokens, const GenParams &params);
  bool detokenize(
      const std::vector<llama_token> &tokens, std::string &out);

  Error infer(
      const char *prompt, char *out, size_t out_size,
      llama_adapter_infer_result_t *result, const GenParams &params);

private:
  Model *model_ref_ = nullptr;
  llama_context *ctx_ = nullptr;
  std::vector<llama_token> token_buffer_;
  std::vector<llama_pos> pos_buffer_;
  std::vector<llama_token_data> sample_candidates_;

  int ctx_n_ctx_ = 0;
  int ctx_n_batch_ = 0;
  int generation_max_new_tokens_ = 128;
  int n_past_ = 0;
  bool abort_requested_ = false;
  llama_adapter_abort_callback_t abort_callback_ = nullptr;
  void *abort_callback_data_ = nullptr;
};

const char *source_version() noexcept;

} // namespace llama_adapter
