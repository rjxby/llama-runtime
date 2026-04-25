#include "llama_adapter_core.h"
#include "llama-cpp.h"

#include <algorithm>
#include <cstring>
#include <stdexcept>

namespace llama_adapter {

#ifndef LLAMA_ADAPTER_SOURCE_VERSION
#error "LLAMA_ADAPTER_SOURCE_VERSION must be defined by the build"
#endif

Model::~Model() noexcept { free(); }

Error Model::load(const char *path) {
  if (!path)
    return Error::INVALID_ARG;
  try {
    llama_model_params p = llama_model_default_params();
    model_ = llama_model_load_from_file(path, p);
    return model_ ? Error::OK : Error::LOAD_MODEL;
  } catch (const std::bad_alloc &) {
    return Error::OUT_OF_MEMORY;
  } catch (...) {
    return Error::UNKNOWN;
  }
}

Error Model::metadata(llama_adapter_model_metadata_t *metadata) const {
  if (!model_ || !metadata)
    return Error::INVALID_ARG;

  metadata->training_context_size = llama_model_n_ctx_train(model_);
  const llama_vocab * model_vocab = vocab();
  metadata->tokenizer_type =
      model_vocab ? static_cast<int32_t>(llama_vocab_type(model_vocab)) : 0;
  return Error::OK;
}

void Model::free() {
  if (model_) {
    llama_model_free(model_);
    model_ = nullptr;
  }
}

Context::Context(Model *model) noexcept : model_ref_(model) {}

Context::~Context() noexcept { free(); }

Error Context::init(int n_ctx, int n_batch, int generation_max_new_tokens) {
  if (!model_ref_ || !model_ref_->handle())
    return Error::INVALID_ARG;
  try {
    llama_context_params p = llama_context_default_params();
    p.n_ctx = (uint32_t)n_ctx;
    p.n_batch = (uint32_t)n_batch;

    ctx_ = llama_init_from_model(model_ref_->handle(), p);
    if (!ctx_) {
      return Error::LOAD_MODEL;
    }

    ctx_n_ctx_ = static_cast<int>(llama_n_ctx(ctx_));
    ctx_n_batch_ = (p.n_batch > 0) ? static_cast<int>(p.n_batch) : 512;
    generation_max_new_tokens_ =
        (generation_max_new_tokens > 0) ? generation_max_new_tokens : 128;
    n_past_ = 0;
    return Error::OK;
  } catch (const std::bad_alloc &) {
    return Error::OUT_OF_MEMORY;
  } catch (...) {
    return Error::UNKNOWN;
  }
}

Error Context::metadata(llama_adapter_context_metadata_t *metadata) const {
  if (!ctx_ || !metadata)
    return Error::INVALID_ARG;

  metadata->context_size = static_cast<int32_t>(llama_n_ctx(ctx_));
  return Error::OK;
}

void Context::free() {
  if (ctx_) {
    llama_free(ctx_);
    ctx_ = nullptr;
  }
  ctx_n_ctx_ = 0;
  generation_max_new_tokens_ = 128;
  n_past_ = 0;
}

void Context::reset() {
  if (ctx_) {
    llama_memory_clear(llama_get_memory(ctx_), true);
  }
  n_past_ = 0;
}

bool Context::tokenize(const char *prompt, std::vector<llama_token> &tokens) {
  if (!model_ref_ || !model_ref_->handle() || !prompt)
    return false;

  const auto prompt_len = static_cast<int>(std::strlen(prompt));
  const int max_t = std::max(ctx_n_ctx_, prompt_len + 8);
  tokens.resize(max_t);

  const llama_vocab *vocab = model_ref_->vocab();
  if (!vocab)
    return false;

  int32_t n = llama_tokenize(vocab, prompt, (int32_t)std::strlen(prompt),
                             tokens.data(), max_t, true, false);

  if (n < 0 || n == max_t)
    return false;

  tokens.resize(n);
  return true;
}

Error Context::count_tokens(const char *prompt, int32_t *token_count) {
  if (!prompt || !token_count)
    return Error::INVALID_ARG;

  try {
    if (!tokenize(prompt, token_buffer_))
      return Error::IO;

    *token_count = static_cast<int32_t>(token_buffer_.size());
    return Error::OK;
  } catch (const std::bad_alloc &) {
    return Error::OUT_OF_MEMORY;
  } catch (...) {
    return Error::UNKNOWN;
  }
}

bool Context::decode(const std::vector<llama_token> &tokens) {
  if (!ctx_ || tokens.empty())
    return true;

  if (ctx_n_ctx_ > 0 &&
      (n_past_ + static_cast<int>(tokens.size())) > ctx_n_ctx_) {
    return false;
  }

  const int batch_size = ctx_n_batch_;
  for (int i = 0; i < (int)tokens.size(); i += batch_size) {
    int n_tokens = std::min(batch_size, (int)tokens.size() - i);

    pos_buffer_.resize(n_tokens);
    for (int j = 0; j < n_tokens; ++j) {
      pos_buffer_[j] = n_past_ + static_cast<llama_pos>(j);
    }

    llama_batch b{};
    b.n_tokens = static_cast<int32_t>(n_tokens);
    b.token = const_cast<llama_token *>(tokens.data() + i);
    b.embd = nullptr;
    b.pos = pos_buffer_.data();
    b.n_seq_id = nullptr;
    b.seq_id = nullptr;
    b.logits = nullptr;

    if (llama_decode(ctx_, b) < 0) {
      return false;
    }

    n_past_ += n_tokens;
  }

  return true;
}

bool Context::sample_token(
    llama_sampler *sampler, llama_token &token,
    std::vector<llama_token> &generated_tokens) {
  if (!ctx_ || !model_ref_ || !model_ref_->handle())
    return false;

  const llama_vocab *vocab = model_ref_->vocab();
  if (!vocab)
    return false;

  const llama_token eos = llama_vocab_eos(vocab);
  token = llama_sampler_sample(sampler, ctx_, -1);
  if (token == eos)
    return true;

  generated_tokens.push_back(token);

  llama_sampler_accept(sampler, token);
  llama_batch single = llama_batch_get_one(&token, 1);
  if (llama_decode(ctx_, single) < 0)
    return false;
  n_past_++;

  return true;
}

bool Context::generate_tokens(
    std::vector<llama_token> &generated_tokens, const GenParams &params) {
  if (!ctx_ || !model_ref_ || !model_ref_->handle())
    return false;

  auto sampler_params = llama_sampler_chain_default_params();
  llama_sampler_ptr sampler(llama_sampler_chain_init(sampler_params));
  if (!sampler)
    return false;

  const float temperature = params.temperature > 0.0f ? params.temperature : 0.0f;
  const float top_p = (params.top_p > 0.0f && params.top_p <= 1.0f) ? params.top_p : 1.0f;

  if (temperature > 0.0f) {
    llama_sampler_chain_add(sampler.get(), llama_sampler_init_top_k(40));
    if (top_p < 1.0f) {
      llama_sampler_chain_add(sampler.get(), llama_sampler_init_top_p(top_p, 1));
    }
    llama_sampler_chain_add(sampler.get(), llama_sampler_init_temp(temperature));
    llama_sampler_chain_add(sampler.get(), llama_sampler_init_dist(params.seed));
  } else {
    llama_sampler_chain_add(sampler.get(), llama_sampler_init_greedy());
  }

  generated_tokens.clear();
  generated_tokens.reserve((size_t)params.max_new_tokens);

  for (int i = 0; i < params.max_new_tokens; ++i) {
    if (n_past_ >= ctx_n_ctx_)
      break;

    llama_token token = LLAMA_TOKEN_NULL;
    if (!sample_token(sampler.get(), token, generated_tokens))
      return false;
    if (token == llama_vocab_eos(model_ref_->vocab()))
      break;
  }

  return true;
}

bool Context::detokenize(
    const std::vector<llama_token> &tokens, std::string &out) {
  out.clear();
  if (tokens.empty())
    return true;

  const llama_vocab *vocab = model_ref_->vocab();
  if (!vocab)
    return false;

  std::vector<char> buffer(std::max<size_t>(64, tokens.size() * 8));
  while (true) {
    int32_t written = llama_detokenize(
        vocab, tokens.data(), static_cast<int32_t>(tokens.size()),
        buffer.data(), static_cast<int32_t>(buffer.size()), true, true);
    if (written >= 0) {
      out.assign(buffer.data(), static_cast<size_t>(written));
      return true;
    }

    const int32_t required = -written;
    if (required <= static_cast<int32_t>(buffer.size()))
      return false;

    buffer.resize(static_cast<size_t>(required));
  }
}

Error Context::infer(
    const char *prompt, char *out, size_t out_size,
    llama_adapter_infer_result_t *result, const GenParams &params) {
  if (!ctx_ || !prompt)
    return Error::INVALID_ARG;

  reset();

  try {
    if (!tokenize(prompt, token_buffer_))
      return Error::IO;

    if (result) {
      result->prompt_tokens = static_cast<int32_t>(token_buffer_.size());
      result->output_tokens = 0;
      result->total_tokens = result->prompt_tokens;
      result->output_bytes = 0;
    }

    if (ctx_n_ctx_ > 0 &&
        (static_cast<int>(token_buffer_.size()) + params.max_new_tokens) >
            ctx_n_ctx_) {
      return Error::INVALID_ARG;
    }

    if (!decode(token_buffer_)) {
      return Error::IO;
    }

    std::vector<llama_token> generated_tokens;
    if (!generate_tokens(generated_tokens, params))
      return Error::IO;

    std::string generated;
    if (!detokenize(generated_tokens, generated))
      return Error::IO;

    if (result) {
      result->output_tokens = static_cast<int32_t>(generated_tokens.size());
      result->total_tokens = result->prompt_tokens + result->output_tokens;
      result->output_bytes = static_cast<int32_t>(generated.size());
    }

    if (!out) {
      return Error::OK;
    }

    if (out_size <= generated.size()) {
      return Error::BUFFER_TOO_SMALL;
    }

    std::memcpy(out, generated.data(), generated.size());
    out[generated.size()] = '\0';

    return Error::OK;
  } catch (const std::bad_alloc &) {
    if (result) {
      result->prompt_tokens = 0;
      result->output_tokens = 0;
      result->total_tokens = 0;
      result->output_bytes = 0;
    }
    if (out && out_size > 0)
      out[0] = '\0';
    return Error::OUT_OF_MEMORY;
  } catch (...) {
    if (result) {
      result->prompt_tokens = 0;
      result->output_tokens = 0;
      result->total_tokens = 0;
      result->output_bytes = 0;
    }
    if (out && out_size > 0)
      out[0] = '\0';
    return Error::UNKNOWN;
  }
}

const char *source_version() noexcept {
  return LLAMA_ADAPTER_SOURCE_VERSION;
}

} // namespace llama_adapter
