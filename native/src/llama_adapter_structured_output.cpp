#include "llama_adapter_structured_output.h"

namespace llama_adapter {

bool create_structured_output_sampler(
    llama_sampler **sampler_out, const llama_vocab *vocab,
    llama_adapter_response_format_t response_format,
    const char *grammar) {
  if (!sampler_out)
    return false;

  *sampler_out = nullptr;

  if (response_format == LLAMA_ADAPTER_RESPONSE_FORMAT_TEXT)
    return true;

  if (response_format != LLAMA_ADAPTER_RESPONSE_FORMAT_GRAMMAR || !vocab ||
      !grammar || !*grammar)
    return false;

  llama_sampler *grammar_sampler =
      llama_sampler_init_grammar(vocab, grammar, "root");
  if (!grammar_sampler)
    return false;

  *sampler_out = grammar_sampler;
  return true;
}

} // namespace llama_adapter
