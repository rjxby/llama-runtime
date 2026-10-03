#include "llama_adapter_core.h"
#include <cstdio>
#include <future>
#include <vector>
#include <stdexcept>
#include <string>

using namespace llama_adapter;
namespace {
int calls = 0, fail_at = 0, injected = 0;
int32_t decode_probe(llama_context *ctx, llama_batch batch) {
  ++calls;
  if (calls == fail_at && injected != 0) {
    return injected;
  }
  return llama_decode(ctx, batch);
}
void require(bool condition, const char *message) {
  if (!condition) {
    throw std::runtime_error(message);
  }
}
std::string generate(Context &context, const GenParams &params) {
  char output[4096]{};
  llama_adapter_infer_result_t result{};
  require(context.infer("Once upon a time", output, sizeof(output), &result, params) == Error::OK,
          "Control generation failed");
  return output;
}
std::string literal_grammar(const std::string &json) {
  std::string grammar = "root ::= \"";
  for (char c : json) {
    if (c == '\\' || c == '\"') {
      grammar += '\\';
    }
    grammar += c;
  }
  return grammar + "\"";
}
}
int main(int argc, char **argv) {
  try {
    require(argc == 2, "Model path is required");
    Model model;
    require(model.load(argv[1]) == Error::OK, "Model failed to load");
    GenParams params;
    params.max_new_tokens = 1;
    params.temperature = 0.7f;
    params.top_p = 0.8f;
    params.seed = 42;
    for (const auto format : {LLAMA_ADAPTER_RESPONSE_FORMAT_TEXT, LLAMA_ADAPTER_RESPONSE_FORMAT_GRAMMAR}) {
      params.response_format = format;
      params.grammar = format == LLAMA_ADAPTER_RESPONSE_FORMAT_GRAMMAR ? "root ::= \"{\\\"ok\\\":true}\"" : "";
      for (const int target : {1, 2}) {
        for (const int code : {0, 1, 2, -1, -2}) {
          Context reused(&model, decode_probe), fresh(&model);
          require(reused.init(128, 32, 8) == Error::OK, "Context init failed");
          require(fresh.init(128, 32, 8) == Error::OK, "Fresh context init failed");
          calls = 0; fail_at = target; injected = code;
          char output[4096]{};
          llama_adapter_infer_result_t result{};
          const auto status = reused.infer("Once upon a time", output, sizeof(output), &result, params);
          require(calls >= target, "Requested decode stage was not reached");
          require(status == (code == 0 ? Error::OK : code == 2 ? Error::ABORTED : Error::IO),
                  "Decode outcome mapped incorrectly");
          if (code != 0) {
            require(calls == target, "Decoding continued after failure");
          }
          fail_at = 0;
          require(generate(reused, params) == generate(fresh, params), "Reused context retained failed request state");
        }
      }
    }
    Context budget(&model, decode_probe);
    require(budget.init(128, 32, 8) == Error::OK, "Budget context init failed");
    llama_adapter_context_metadata_t metadata{};
    require(budget.metadata(&metadata) == Error::OK, "Context metadata failed");
    int32_t prompt_tokens = 0;
    require(budget.count_tokens("Once upon a time", &prompt_tokens) == Error::OK, "Counting failed");
    params.response_format = LLAMA_ADAPTER_RESPONSE_FORMAT_TEXT;
    params.grammar.clear();
    params.temperature = 0;
    params.top_p = 1;
    for (int excess : {0, 1}) {
      params.max_new_tokens = metadata.context_size - prompt_tokens + excess;
      calls = 0;
      char output[4096]{};
      llama_adapter_infer_result_t result{};
      auto status = budget.infer("Once upon a time", output, sizeof(output), &result, params);
      require(status == (excess ? Error::PROMPT_BUDGET : Error::OK), "Exact budget boundary failed");
      require(result.prompt_tokens == prompt_tokens, "Prompt usage was lost on budget rejection");
      require(excess ? calls == 0 : calls > 0, "Budget rejection decoded tokens");
      require(result.total_tokens == result.prompt_tokens + result.output_tokens, "Usage sum disagrees");
    }
    const std::string nested = R"({"nested":{"text":"héllo\n\"世界\""},"items":[1,true,null]})";
    for (const float temperature : {0.0f, 0.7f}) {
      Context reused(&model), fresh(&model);
      require(reused.init(256, 32, 8) == Error::OK, "Sampling context init failed");
      require(fresh.init(256, 32, 8) == Error::OK, "Fresh sampling context init failed");
      params.max_new_tokens = 128;
      params.temperature = temperature;
      params.top_p = temperature > 0 ? 0.8f : 1.0f;
      params.response_format = LLAMA_ADAPTER_RESPONSE_FORMAT_GRAMMAR;
      params.grammar = literal_grammar(nested);
      require(generate(reused, params) == nested, "Nested, escaped Unicode JSON failed");
      require(generate(reused, params) == generate(fresh, params), "Seeded JSON reuse differs");
      char tiny[1]{};
      llama_adapter_infer_result_t result{};
      require(reused.infer("Once upon a time", tiny, sizeof(tiny), &result, params) == Error::BUFFER_TOO_SMALL,
              "Buffer exhaustion did not fail");
      require(generate(reused, params) == generate(fresh, params), "Reuse after buffer exhaustion differs");
      params.max_new_tokens = 1;
      require(generate(reused, params) != nested, "Expected incomplete JSON for one token");
      params.max_new_tokens = 128;
      require(generate(reused, params) == generate(fresh, params), "Reuse after incomplete JSON differs");
      params.response_format = LLAMA_ADAPTER_RESPONSE_FORMAT_TEXT;
      params.grammar.clear();
      params.max_new_tokens = 16;
      const auto expected = generate(fresh, params);
      require(generate(reused, params) == expected, "Seeded text reuse differs");
      for (int concurrency : {2, 4}) {
        std::vector<std::future<std::string>> requests;
        for (int i = 0; i < concurrency; ++i) {
          requests.push_back(std::async(std::launch::async, [&] {
            Context isolated(&model);
            require(isolated.init(256, 32, 8) == Error::OK, "Concurrent context init failed");
            return generate(isolated, params);
          }));
        }
        for (auto &request : requests) {
          require(request.get() == expected, "Concurrent isolated context differs");
        }
      }
    }
    Context final_abort(&model, decode_probe), fresh_after_abort(&model);
    require(final_abort.init(256, 32, 8) == Error::OK, "Abort context init failed");
    require(fresh_after_abort.init(256, 32, 8) == Error::OK, "Fresh abort context init failed");
    calls = 0;
    params.max_new_tokens = 1;
    params.abort_callback = [](void *) { return calls >= 2; };
    char output[4096]{};
    llama_adapter_infer_result_t result{};
    require(final_abort.infer("Once upon a time", output, sizeof(output), &result, params) == Error::ABORTED,
            "Cancellation after final successful decode returned success");
    params.abort_callback = nullptr;
    require(generate(final_abort, params) == generate(fresh_after_abort, params), "Reuse after final abort differs");
    std::puts("PASS: decode outcomes in prefill and final token, text/grammar, budgets, nested Unicode JSON, buffers, concurrent seeded context reuse");
  } catch (const std::exception &error) {
    std::fprintf(stderr, "FAIL: %s\n", error.what());
    return 1;
  }
}
