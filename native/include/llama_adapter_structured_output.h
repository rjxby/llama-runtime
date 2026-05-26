#pragma once

#include "llama.h"
#include "llama_adapter.h"

namespace llama_adapter {

bool create_structured_output_sampler(
    llama_sampler **sampler_out, const llama_vocab *vocab,
    llama_adapter_response_format_t response_format,
    const char *grammar);

} // namespace llama_adapter
