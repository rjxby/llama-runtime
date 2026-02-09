#include "llama_adapter_core.h"

#include <algorithm>
#include <cstring>
#include <fstream>
#include <stdexcept>

namespace llama_adapter {

Adapter::Adapter() noexcept = default;

Adapter::~Adapter() noexcept {
  free_context();
  free_model();
}

Error Adapter::load_model(const char *path) {
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

void Adapter::reset() {
  if (ctx_) {
    llama_memory_clear(llama_get_memory(ctx_), true);
  }
  n_past_ = 0;
}

Error Adapter::create_context() {
  if (!model_)
    return Error::INVALID_ARG;
  try {
    llama_context_params p = llama_context_default_params();
    ctx_ = llama_init_from_model(model_, p);
    if (!ctx_)
      return Error::LOAD_MODEL;

    ctx_params_ = p;
    ctx_n_ctx_ = (p.n_ctx > 0) ? static_cast<int>(p.n_ctx) : 2048;
    n_past_ = 0;
    return Error::OK;
  } catch (const std::bad_alloc &) {
    return Error::OUT_OF_MEMORY;
  } catch (...) {
    return Error::UNKNOWN;
  }
}

void Adapter::free_context() {
  if (ctx_) {
    llama_free(ctx_);
    ctx_ = nullptr;
  }
  ctx_n_ctx_ = 0;
  n_past_ = 0;
}

void Adapter::free_model() {
  if (model_) {
    llama_model_free(model_);
    model_ = nullptr;
  }
}

bool Adapter::tokenize(const char *prompt, std::vector<llama_token> &tokens) {
  if (!model_ || !prompt)
    return false;

  constexpr int MAX_TOKENS = 16384;
  tokens.resize(MAX_TOKENS);

  const llama_vocab *vocab = llama_model_get_vocab(model_);
  if (!vocab)
    return false;

  int32_t n = llama_tokenize(vocab, prompt, (int32_t)std::strlen(prompt),
                             tokens.data(), MAX_TOKENS, true, false);

  if (n < 0 || n == MAX_TOKENS)
    return false;

  tokens.resize(n);
  return true;
}

bool Adapter::decode(const std::vector<llama_token> &tokens) {
  if (!ctx_ || tokens.empty())
    return true;
  if (ctx_n_ctx_ > 0 &&
      (n_past_ + static_cast<int>(tokens.size())) > ctx_n_ctx_)
    return false;

  std::vector<llama_pos> pos(tokens.size());
  for (size_t i = 0; i < tokens.size(); ++i) {
    pos[i] = n_past_ + static_cast<llama_pos>(i);
  }

  llama_batch b{};
  b.n_tokens = static_cast<int32_t>(tokens.size());
  b.token = const_cast<llama_token *>(tokens.data());
  b.embd = nullptr;
  b.pos = pos.data();
  b.n_seq_id = nullptr;
  b.seq_id = nullptr;
  b.logits = nullptr;

  if (llama_decode(ctx_, b) < 0)
    return false;

  n_past_ += static_cast<int>(tokens.size());
  return true;
}

bool Adapter::generate(std::string &out, size_t limit,
                       const GenParams &params) {
  if (!ctx_ || !model_)
    return false;

  const llama_vocab *vocab = llama_model_get_vocab(model_);
  if (!vocab)
    return false;

  const int32_t n_vocab = llama_vocab_n_tokens(vocab);
  const llama_token eos = llama_vocab_eos(vocab);

  out.clear();
  out.reserve(std::min<size_t>(limit, (size_t)params.max_new_tokens * 8));

  for (int i = 0; i < params.max_new_tokens; ++i) {
    if (n_past_ >= ctx_n_ctx_)
      break;

    const float *logits = llama_get_logits(ctx_);
    if (!logits)
      break;

    int best = 0;
    for (int j = 1; j < n_vocab; ++j) {
      if (logits[j] > logits[best])
        best = j;
    }

    llama_token tok = static_cast<llama_token>(best);
    if (tok == eos)
      break;

    char piece_buf[512];
    int piece_len = llama_token_to_piece(vocab, tok, piece_buf,
                                         (int)sizeof(piece_buf), 0, true);
    if (piece_len <= 0 || piece_len >= (int)sizeof(piece_buf))
      return false;

    if (out.size() + static_cast<size_t>(piece_len) + 1 > limit)
      break;
    out.append(piece_buf, piece_len);

    llama_token t = tok;
    llama_pos p = n_past_;
    llama_batch single{};
    single.n_tokens = 1;
    single.token = &t;
    single.embd = nullptr;
    single.pos = &p;
    single.n_seq_id = nullptr;
    single.seq_id = nullptr;
    single.logits = nullptr;

    if (llama_decode(ctx_, single) < 0)
      return false;
    n_past_++;
  }

  return true;
}

Error Adapter::infer(const char *prompt, char *out, size_t out_size,
                     const GenParams &params) {
  if (!ctx_ || !prompt || !out || out_size == 0)
    return Error::INVALID_ARG;

  reset();

  try {
    std::vector<llama_token> tokens;
    if (!tokenize(prompt, tokens))
      return Error::IO;
    if (!decode(tokens))
      return Error::IO;

    std::string generated;
    if (!generate(generated, out_size, params))
      return Error::IO;

    size_t n = std::min(out_size - 1, generated.size());
    std::memcpy(out, generated.data(), n);
    out[n] = '\0';

    return Error::OK;
  } catch (const std::bad_alloc &) {
    if (out && out_size)
      out[0] = '\0';
    return Error::OUT_OF_MEMORY;
  } catch (...) {
    if (out && out_size)
      out[0] = '\0';
    return Error::UNKNOWN;
  }
}

bool Adapter::find_meta_json(std::string &result) {
  const char *env = std::getenv("LLAMA_ADAPTER_META_JSON");
  if (!env)
    return false;

  std::ifstream f(env);
  if (!f.good())
    return false;

  result = env;
  return true;
}

} // namespace llama_adapter
