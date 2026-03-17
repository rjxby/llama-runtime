#include "llama_adapter_core.h"

#include <algorithm>
#include <cstring>
#include <fstream>
#include <stdexcept>

namespace llama_adapter {

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

void Model::free() {
  if (model_) {
    llama_model_free(model_);
    model_ = nullptr;
  }
}

Context::Context(Model *model) noexcept : model_ref_(model) {}

Context::~Context() noexcept { free(); }

Error Context::init(int n_ctx, int n_batch, int max_tokens) {
  if (!model_ref_ || !model_ref_->handle())
    return Error::INVALID_ARG;
  try {
    fprintf(stderr,
            "[DEBUG] Context::init called with n_ctx=%d, n_batch=%d, "
            "max_tokens=%d\n",
            n_ctx, n_batch, max_tokens);
    llama_context_params p = llama_context_default_params();
    p.n_ctx = (uint32_t)n_ctx;
    p.n_batch = (uint32_t)n_batch;

    ctx_ = llama_init_from_model(model_ref_->handle(), p);
    if (!ctx_) {
      fprintf(stderr, "[DEBUG] llama_init_from_model failed\n");
      return Error::LOAD_MODEL;
    }

    ctx_n_ctx_ = (p.n_ctx > 0) ? static_cast<int>(p.n_ctx) : 2048;
    ctx_n_batch_ = (p.n_batch > 0) ? static_cast<int>(p.n_batch) : 512;
    max_tokens_ = (max_tokens > 0) ? max_tokens : 16384;
    n_past_ = 0;
    return Error::OK;
  } catch (const std::bad_alloc &) {
    return Error::OUT_OF_MEMORY;
  } catch (...) {
    return Error::UNKNOWN;
  }
}

void Context::free() {
  if (ctx_) {
    llama_free(ctx_);
    ctx_ = nullptr;
  }
  ctx_n_ctx_ = 0;
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

  const int max_t = (max_tokens_ > 0) ? max_tokens_ : 16384;
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

bool Context::decode(const std::vector<llama_token> &tokens) {
  if (!ctx_ || tokens.empty())
    return true;

  if (ctx_n_ctx_ > 0 &&
      (n_past_ + static_cast<int>(tokens.size())) > ctx_n_ctx_) {
    fprintf(stderr,
            "[DEBUG] decode failed: n_past_ (%d) + tokens.size() (%zu) > "
            "ctx_n_ctx_ (%d)\n",
            n_past_, tokens.size(), ctx_n_ctx_);
    return false;
  }

  const int batch_size = ctx_n_batch_;
  for (int i = 0; i < (int)tokens.size(); i += batch_size) {
    int n_tokens = std::min(batch_size, (int)tokens.size() - i);

    std::vector<llama_pos> pos(n_tokens);
    for (int j = 0; j < n_tokens; ++j) {
      pos[j] = n_past_ + static_cast<llama_pos>(j);
    }

    llama_batch b{};
    b.n_tokens = static_cast<int32_t>(n_tokens);
    b.token = const_cast<llama_token *>(tokens.data() + i);
    b.embd = nullptr;
    b.pos = pos.data();
    b.n_seq_id = nullptr;
    b.seq_id = nullptr;
    b.logits = nullptr;

    if (llama_decode(ctx_, b) < 0) {
      fprintf(stderr, "[DEBUG] llama_decode failed at chunk %d\n", i);
      return false;
    }

    n_past_ += n_tokens;
  }

  return true;
}

bool Context::generate(std::string &out, size_t limit,
                       const GenParams &params) {
  if (!ctx_ || !model_ref_ || !model_ref_->handle())
    return false;

  const llama_vocab *vocab = model_ref_->vocab();
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

Error Context::infer(const char *prompt, char *out, size_t out_size,
                     int32_t *out_written, const GenParams &params) {
  if (!ctx_ || !prompt)
    return Error::INVALID_ARG;

  reset();

  try {
    std::vector<llama_token> tokens;
    if (!tokenize(prompt, tokens))
      return Error::IO;

    if (!decode(tokens)) {
      fprintf(stderr, "[DEBUG] infer failed at decode stage\n");
      return Error::IO;
    }

    std::string generated;
    // We pass 1GB as a logical limit for the string growth if out_size is 0
    size_t limit = (out && out_size > 0) ? out_size : (1024 * 1024 * 1024);
    if (!generate(generated, limit, params))
      return Error::IO;

    if (out_written) {
      *out_written = static_cast<int32_t>(generated.size());
    }

    if (!out) {
      return Error::OK;
    }

    if (out_size <= generated.size()) {
      return Error::INVALID_ARG;
    }

    std::memcpy(out, generated.data(), generated.size());
    out[generated.size()] = '\0';

    return Error::OK;
  } catch (const std::bad_alloc &) {
    if (out_written)
      *out_written = 0;
    if (out && out_size > 0)
      out[0] = '\0';
    return Error::OUT_OF_MEMORY;
  } catch (...) {
    if (out_written)
      *out_written = 0;
    if (out && out_size > 0)
      out[0] = '\0';
    return Error::UNKNOWN;
  }
}

bool find_meta_json(std::string &result) {
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
