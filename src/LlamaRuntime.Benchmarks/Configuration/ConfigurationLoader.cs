using LlamaRuntime.Benchmarks.Common;
using Microsoft.Extensions.Configuration;

namespace LlamaRuntime.Benchmarks.Configuration;

public static class ConfigurationLoader
{
    public static BenchmarkOptions Load()
    {
        var builder = new ConfigurationBuilder()
            .AddEnvironmentVariables("BENCH_");

        return Load(builder.Build());
    }

    public static BenchmarkOptions Load(IConfiguration config)
    {
        var options = new BenchmarkOptions();
        config.Bind(options);
        ApplyAliases(config, options);

        if (!BenchmarkResponseFormatParser.TryParse(options.ResponseFormat, out var responseFormat))
        {
            throw new InvalidOperationException("ResponseFormat must be one of: text, json.");
        }

        options.ResponseFormatKind = responseFormat;
        ApplyResponseFormatDefaults(config, options);

        Validate(options);
        return options;
    }

    private static void Validate(BenchmarkOptions options)
    {
        if (options.Mode == BenchmarkMode.LlamaRuntimeGrpc)
        {
            if (string.IsNullOrEmpty(options.ApiKey))
            {
                throw new InvalidOperationException("ApiKey is required for Llama Runtime Grpc mode. Set BENCH_APIKEY.");
            }

            if (!Uri.TryCreate(options.GrpcUrl, UriKind.Absolute, out _))
            {
                throw new InvalidOperationException("GrpcUrl must be an absolute URI. Set BENCH_GRPCURL.");
            }
        }
        else if (options.Mode == BenchmarkMode.LlamaRest)
        {
            if (string.IsNullOrEmpty(options.LlamaRestUrl))
            {
                 throw new InvalidOperationException("LlamaRestUrl is required for LlamaRest mode. Set BENCH_LLAMARESTURL.");
            }
        }

        if (options.Iterations <= 0) throw new InvalidOperationException("Iterations must be > 0");
        if (options.Concurrency <= 0) throw new InvalidOperationException("Concurrency must be > 0");
        if (options.LlamaRestMaxNewTokens <= 0) throw new InvalidOperationException("LlamaRestMaxNewTokens must be > 0");
        if (options.LlamaRestTemperature < 0) throw new InvalidOperationException("LlamaRestTemperature must be >= 0");
    }

    private static void ApplyAliases(IConfiguration config, BenchmarkOptions options)
    {
        options.OutputFile ??= config["OUTPUT_FILE"];
        options.InvocationFile ??= config["INVOCATION_FILE"];

        var logInvocations = config["LOG_INVOCATIONS"];
        if (!string.IsNullOrWhiteSpace(logInvocations))
        {
            if (!bool.TryParse(logInvocations, out var parsed))
            {
                throw new InvalidOperationException("LogInvocations must be true or false.");
            }

            options.LogInvocations = parsed;
        }

        var responseFormat = config["RESPONSE_FORMAT"];
        if (!string.IsNullOrWhiteSpace(responseFormat))
        {
            options.ResponseFormat = responseFormat;
        }
    }

    private static void ApplyResponseFormatDefaults(IConfiguration config, BenchmarkOptions options)
    {
        var promptWasProvided =
            !string.IsNullOrWhiteSpace(config["Prompt"]) ||
            !string.IsNullOrWhiteSpace(config["PROMPT"]);

        if (options.ResponseFormatKind == BenchmarkResponseFormat.Json && !promptWasProvided)
        {
            options.Prompt = BenchmarkJsonSchema.DefaultPrompt;
        }
    }
}
