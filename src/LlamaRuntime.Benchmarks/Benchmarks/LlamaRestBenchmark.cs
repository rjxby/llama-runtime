using System.Net.Http.Json;
using LlamaRuntime.Benchmarks.Common;
using LlamaRuntime.Benchmarks.Configuration;

namespace LlamaRuntime.Benchmarks;

public static class LlamaRestBenchmark
{
    public static async Task<BenchmarkResult> RunAsync(BenchmarkOptions options)
    {
        Console.WriteLine("=== llama.cpp REST benchmark ===");
        var url = options.LlamaRestUrl!;
        Console.WriteLine($"Endpoint: {url}");

        using var httpClient = new HttpClient
        {
            BaseAddress = new Uri(url),
            Timeout = TimeSpan.FromMinutes(5)
        };

        var payload = new
        {
            prompt = options.Prompt,
            n_predict = 128,
            temperature = 0.8
        };

        Console.WriteLine("Warming up...");
        for (int i = 0; i < 5; i++)
        {
            try
            {
                var r = await httpClient.PostAsJsonAsync("", payload);
                r.EnsureSuccessStatusCode();
            }
            catch (Exception ex)
            {
                 Console.WriteLine($"Warmup failed: {ex.Message}");
            }
        }

        Console.WriteLine("Warmup done.\n");

        return await BenchmarkRunner.RunAsync(
            options.Iterations,
            options.Concurrency,
            async _ =>
            {
                var r = await httpClient.PostAsJsonAsync("", payload);
                r.EnsureSuccessStatusCode();
                var json = await r.Content.ReadAsStringAsync();
                return !string.IsNullOrEmpty(json);
            });
    }
}
